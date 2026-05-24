'use strict';

const { spawn } = require('node:child_process');
const { resolveCodexExecutable } = require('./cliPaths');

const REQUEST_TIMEOUT_MS = 10_000;
const START_TIMEOUT_MS = 12_000;

function makeError(code, message, extra) {
  const err = new Error(message);
  err.code = code;
  if (extra) Object.assign(err, extra);
  return err;
}

class CodexClient {
  constructor() {
    this.proc = null;
    this.pending = new Map(); // id -> {resolve, reject, timer}
    this.nextId = 1;
    this.stdoutBuffer = '';
    this.initializing = null;
    this.lastError = null;
  }

  async ensureStarted() {
    if (this.proc && !this.proc.killed && this.proc.exitCode === null) return;
    if (this.initializing) return this.initializing;

    this.initializing = (async () => {
      const exe = resolveCodexExecutable();
      if (!exe) {
        throw makeError(
          'codex_cli_missing',
          'Codex CLI が見つかりません。`npm i -g @openai/codex` を実行してください。',
        );
      }

      const lowered = exe.toLowerCase();
      const isPs1 = lowered.endsWith('.ps1');
      const isCmd = lowered.endsWith('.cmd') || lowered.endsWith('.bat');
      const childEnv = buildChildEnv(process.env);

      let proc;
      try {
        if (isPs1) {
          proc = spawn(
            'powershell.exe',
            ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', exe, 'app-server'],
            { stdio: ['pipe', 'pipe', 'pipe'], env: childEnv, windowsHide: true },
          );
        } else if (isCmd) {
          // Node 20+ refuses to spawn .cmd/.bat directly (CVE-2024-27980).
          // Wrap with cmd.exe /d /s /c — mirrors what `shell: true` does, but explicit so
          // we never pass user-derived strings through shell parsing.
          proc = spawn(
            process.env.ComSpec || 'cmd.exe',
            ['/d', '/s', '/c', `"${exe}" app-server`],
            {
              stdio: ['pipe', 'pipe', 'pipe'],
              env: childEnv,
              windowsHide: true,
              windowsVerbatimArguments: true,
            },
          );
        } else {
          proc = spawn(exe, ['app-server'], {
            stdio: ['pipe', 'pipe', 'pipe'],
            env: childEnv,
            windowsHide: true,
          });
        }
      } catch (err) {
        throw makeError('codex_spawn_failed', `codex app-server を起動できません: ${err.message}`);
      }

      this.proc = proc;
      this.stdoutBuffer = '';

      proc.stdout.setEncoding('utf8');
      proc.stdout.on('data', (chunk) => this._onStdout(chunk));
      proc.stderr.on('data', () => {
        // drain only; do not propagate noise but capture last line for diagnostics
      });
      proc.on('exit', (code) => this._onExit(code));
      proc.on('error', (err) => {
        this.lastError = err;
        this._failAll(makeError('codex_process_exited', `codex app-server プロセスエラー: ${err.message}`));
      });

      // JSON-RPC handshake.
      const initParams = {
        clientInfo: { name: 'agent-limit-checker', version: '0.1.0' },
        capabilities: {},
      };
      await this._request('initialize', initParams, START_TIMEOUT_MS);
      this._sendNotification('initialized', {});
    })();

    try {
      await this.initializing;
    } finally {
      this.initializing = null;
    }
  }

  stop() {
    if (this.proc) {
      try {
        this.proc.stdin && this.proc.stdin.end();
      } catch { /* ignore */ }
      try {
        if (!this.proc.killed) this.proc.kill();
      } catch { /* ignore */ }
    }
    this._failAll(makeError('codex_process_exited', 'codex app-server を停止しました。'));
    this.proc = null;
    this.stdoutBuffer = '';
  }

  async readRateLimits() {
    await this.ensureStarted();
    const envelope = await this._request('account/rateLimits/read', {}, REQUEST_TIMEOUT_MS);
    if (!envelope || envelope.result == null) {
      throw makeError('codex_rpc_error', 'account/rateLimits/read のレスポンスに result がありません');
    }
    return envelope.result;
  }

  _request(method, params, timeoutMs) {
    return new Promise((resolve, reject) => {
      if (!this.proc || this.proc.exitCode !== null) {
        reject(makeError('codex_process_exited', 'codex app-server が起動していません'));
        return;
      }
      const id = this.nextId++;
      const payload = JSON.stringify({ jsonrpc: '2.0', id, method, params });
      const timer = setTimeout(() => {
        if (this.pending.has(id)) {
          this.pending.delete(id);
          reject(makeError('codex_timeout', `RPC ${method} がタイムアウトしました`));
        }
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      try {
        this.proc.stdin.write(payload + '\n');
      } catch (err) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(makeError('codex_write_failed', `stdin への書き込みに失敗: ${err.message}`));
      }
    });
  }

  _sendNotification(method, params) {
    if (!this.proc || this.proc.exitCode !== null) return;
    const payload = JSON.stringify({ jsonrpc: '2.0', method, params });
    try {
      this.proc.stdin.write(payload + '\n');
    } catch { /* ignore */ }
  }

  _onStdout(chunk) {
    this.stdoutBuffer += chunk;
    while (true) {
      const idx = this.stdoutBuffer.indexOf('\n');
      if (idx < 0) break;
      const line = this.stdoutBuffer.slice(0, idx).trim();
      this.stdoutBuffer = this.stdoutBuffer.slice(idx + 1);
      if (!line) continue;
      let msg;
      try {
        msg = JSON.parse(line);
      } catch {
        continue;
      }
      if (msg && typeof msg.id !== 'undefined' && msg.id !== null) {
        const entry = this.pending.get(msg.id);
        if (entry) {
          clearTimeout(entry.timer);
          this.pending.delete(msg.id);
          if (msg.error) {
            entry.reject(makeError('codex_rpc_error', `Codex RPC エラー: ${msg.error.message || JSON.stringify(msg.error)}`));
          } else {
            entry.resolve(msg);
          }
        }
      }
      // notifications without id are ignored for v1
    }
  }

  _onExit(code) {
    this.proc = null;
    this._failAll(makeError('codex_process_exited', `codex app-server が終了しました (exit ${code})`));
  }

  _failAll(err) {
    for (const [, entry] of this.pending) {
      clearTimeout(entry.timer);
      try { entry.reject(err); } catch { /* ignore */ }
    }
    this.pending.clear();
  }
}

function buildChildEnv(base) {
  const allow = [
    'HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'PROGRAMDATA',
    'USERNAME', 'TEMP', 'TMP', 'SystemRoot', 'windir',
    'PATH', 'PATHEXT', 'LANG', 'LC_ALL',
    'CODEX_HOME', 'XDG_CONFIG_HOME', 'XDG_CACHE_HOME',
  ];
  const env = {};
  for (const k of allow) {
    if (base[k] != null) env[k] = base[k];
  }
  return env;
}

function pickWindow(dto, minutes) {
  if (!dto) return null;
  const top = dto.rateLimits;
  if (top) {
    if (top.primary && top.primary.windowDurationMins === minutes) return top.primary;
    if (top.secondary && top.secondary.windowDurationMins === minutes) return top.secondary;
  }
  const byId = dto.rateLimitsByLimitId || {};
  const sorted = Object.keys(byId).sort();
  for (const k of sorted) {
    const snap = byId[k];
    if (snap.primary && snap.primary.windowDurationMins === minutes) return snap.primary;
    if (snap.secondary && snap.secondary.windowDurationMins === minutes) return snap.secondary;
  }
  return null;
}

function windowToRateLimit(win) {
  if (!win) return null;
  if (typeof win.usedPercent !== 'number' || typeof win.resetsAt !== 'number') return null;
  const utilization = Math.max(0, win.usedPercent / 100);
  return { utilization, resetsAt: win.resetsAt * 1000 };
}

const client = new CodexClient();

async function fetch() {
  const dto = await client.readRateLimits();
  return {
    fiveHour: windowToRateLimit(pickWindow(dto, 300)),
    weekly: windowToRateLimit(pickWindow(dto, 10080)),
    weeklySonnet: null,
  };
}

async function shutdown() {
  try {
    client.stop();
  } catch { /* ignore */ }
}

module.exports = { fetch, shutdown };
