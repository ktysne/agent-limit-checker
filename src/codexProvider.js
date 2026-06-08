'use strict';

const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { resolveCodexExecutable } = require('./cliPaths');

const REQUEST_TIMEOUT_MS = 10_000;
const START_TIMEOUT_MS = 12_000;

function makeError(code, message, extra) {
  const err = new Error(message);
  err.code = code;
  if (extra) Object.assign(err, extra);
  return err;
}

// codex persists its OAuth credentials here (honoring CODEX_HOME like the CLI).
// The long-lived `app-server` reads this once at startup and caches the tokens
// in memory.
function codexAuthFile() {
  const home = process.env.CODEX_HOME || path.join(os.homedir(), '.codex');
  return path.join(home, 'auth.json');
}

// A cheap fingerprint of auth.json. When it changes, some *other* codex process
// rewrote the credentials (see CodexClient.readRateLimits) and the tokens our
// app-server cached at startup are stale.
function authSignature() {
  try {
    const st = fs.statSync(codexAuthFile());
    return `${st.mtimeMs}:${st.size}`;
  } catch {
    return null; // missing / unreadable — treat as "unknown", never force a restart
  }
}

// Errors a freshly respawned app-server can recover from: the long-lived
// process crashed/exited, or the rate-limit read came back 401 /
// token_invalidated because the cached OAuth token was invalidated out from
// under us (refresh-token rotation triggered by another codex process —
// `codex login`, the cross-review `codex exec`, etc.). We respawn (re-reading
// auth.json) and retry once.
function isRestartableError(err) {
  if (!err) return false;
  if (err.code === 'codex_process_exited') return true;
  if (err.code !== 'codex_rpc_error') return false;
  if (err.restartable === true) return true;
  return isAuthErrorText(err.message);
}

function isAuthErrorText(text) {
  return /401|unauthor|token_invalid|invalid_grant|sign(?:ed|ing)?\s*in/i.test(
    String(text || ''),
  );
}

function errorText(value) {
  if (!value) return '';
  if (typeof value === 'string') return value;
  const parts = [];
  if (typeof value.message === 'string') parts.push(value.message);
  try {
    parts.push(JSON.stringify(value));
  } catch {
    parts.push(String(value));
  }
  return parts.join(' ');
}

function makeCodexRpcError(rawError) {
  const restartable = isAuthErrorText(errorText(rawError));
  const message = restartable
    ? 'Codex の認証が失効しています。ログインし直してください。'
    : 'Codex RPC エラー: rate limit の取得に失敗しました';
  return makeError('codex_rpc_error', message, { restartable });
}

// Long-lived JSON-RPC client over a single `codex app-server` child process.
//
// The app-server caches the OAuth tokens it read from auth.json at startup. If
// any *other* codex process refreshes those credentials, refresh-token rotation
// invalidates the cached token and our reads start failing with 401 /
// token_invalidated (and the app-server may then exit). readRateLimits guards
// against this two ways: it respawns when auth.json changes under us, and it
// respawns + retries once when a read fails with a restartable error.
class CodexClient {
  constructor() {
    this.proc = null;
    this.pending = new Map(); // id -> {resolve, reject, timer}
    this.nextId = 1;
    this.stdoutBuffer = '';
    this.initializing = null;
    this.lastError = null;
    // auth.json fingerprint captured when the current app-server was started.
    this.authSignature = null;
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
      // All handlers are bound to *this* proc and ignore events once it has been
      // replaced (a restart respawns before the old process's exit/data events
      // drain) so a stale event can't null out the new proc or corrupt its
      // stdout buffer.
      proc.stdout.on('data', (chunk) => {
        if (this.proc !== proc) return;
        this._onStdout(chunk);
      });
      proc.stderr.on('data', () => {
        // drain only; do not propagate noise but capture last line for diagnostics
      });
      proc.on('exit', (code) => this._onExit(proc, code));
      proc.on('error', (err) => {
        if (this.proc !== proc) return;
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
      // Snapshot auth.json *after* the handshake so we can tell later whether
      // another codex process rotated the credentials under us.
      this.authSignature = authSignature();
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
    this.authSignature = null;
  }

  async readRateLimits() {
    await this.ensureStarted();
    // If another codex process rewrote auth.json since our app-server started
    // (cross-review's `codex exec`, a fresh `codex login`, ...), the cached
    // tokens are stale — respawn first so we read the new credentials instead of
    // hitting a 401 on the request below.
    if (this.authSignature != null) {
      const sig = authSignature();
      if (sig != null && sig !== this.authSignature) {
        await this._restart();
      }
    }
    try {
      return await this._readOnce();
    } catch (err) {
      if (!isRestartableError(err)) throw err;
      // Stale-token or crashed app-server: respawn with fresh credentials and
      // retry exactly once so a single rotation can't wedge us until restart.
      await this._restart();
      return await this._readOnce();
    }
  }

  async _readOnce() {
    const envelope = await this._request('account/rateLimits/read', {}, REQUEST_TIMEOUT_MS);
    if (!envelope || envelope.result == null) {
      throw makeError('codex_rpc_error', 'account/rateLimits/read のレスポンスに result がありません');
    }
    return envelope.result;
  }

  async _restart() {
    this.stop();
    await this.ensureStarted();
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
            entry.reject(makeCodexRpcError(msg.error));
          } else {
            entry.resolve(msg);
          }
        }
      }
      // notifications without id are ignored for v1
    }
  }

  _onExit(proc, code) {
    if (this.proc !== proc) return; // stale exit from a process we already replaced
    this.proc = null;
    this.authSignature = null;
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

// `account/rateLimits/read` returns `rateLimits.planType` (e.g. "plus", "pro",
// "team", "free"). Some accounts have several profiles in `rateLimitsByLimitId`
// instead; pick the first one in sorted key order so the choice is
// deterministic across runs.
function extractPlanLabel(dto) {
  if (!dto || typeof dto !== 'object') return null;
  let raw = null;
  if (dto.rateLimits && typeof dto.rateLimits.planType === 'string') {
    raw = dto.rateLimits.planType;
  }
  if (!raw && dto.rateLimitsByLimitId && typeof dto.rateLimitsByLimitId === 'object') {
    const keys = Object.keys(dto.rateLimitsByLimitId).sort();
    for (const k of keys) {
      const snap = dto.rateLimitsByLimitId[k];
      if (snap && typeof snap.planType === 'string') {
        raw = snap.planType;
        break;
      }
    }
  }
  if (!raw) return null;
  return raw.charAt(0).toUpperCase() + raw.slice(1).toLowerCase();
}

const client = new CodexClient();

async function fetch() {
  const dto = await client.readRateLimits();
  return {
    fiveHour: windowToRateLimit(pickWindow(dto, 300)),
    weekly: windowToRateLimit(pickWindow(dto, 10080)),
    weeklySonnet: null,
    plan: extractPlanLabel(dto),
  };
}

async function shutdown() {
  try {
    client.stop();
  } catch { /* ignore */ }
}

module.exports = {
  fetch,
  shutdown,
  authFilePath: codexAuthFile,
  _private: { codexAuthFile, extractPlanLabel, isRestartableError, makeCodexRpcError },
};
