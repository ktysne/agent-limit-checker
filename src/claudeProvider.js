'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const https = require('node:https');
const { spawn } = require('node:child_process');

const { resolveClaudeExecutable } = require('./cliPaths');

const USAGE_URL = 'https://api.anthropic.com/api/oauth/usage';
const CREDENTIALS_PATH = path.join(os.homedir(), '.claude', '.credentials.json');
const REQUEST_TIMEOUT_MS = 12_000;
const REFRESH_MARGIN_MS = 60_000;
const CLI_NUDGE_TIMEOUT_MS = 15_000;

async function readCredentials() {
  let raw;
  try {
    raw = await fs.readFile(CREDENTIALS_PATH, 'utf8');
  } catch (err) {
    if (err && err.code === 'ENOENT') {
      throw makeError(
        'claude_credentials_missing',
        'Claude Code の認証情報が見つかりません。`claude login` を実行してください。',
      );
    }
    throw makeError('claude_credentials_unreadable', `認証ファイルを読めません: ${err.message}`);
  }
  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    throw makeError('claude_credentials_invalid', `認証ファイルの JSON が不正です: ${err.message}`);
  }
  const oauth = parsed && parsed.claudeAiOauth;
  const token = oauth && oauth.accessToken;
  if (!oauth || !token || typeof token !== 'string') {
    throw makeError(
      'claude_credentials_missing',
      'アクセストークンが見つかりません。`claude login` で再ログインしてください。',
    );
  }
  return { raw: parsed, oauth, accessToken: token };
}

function makeError(code, message, extra) {
  const err = new Error(message);
  err.code = code;
  if (extra) Object.assign(err, extra);
  return err;
}

function httpJson(url, options) {
  return new Promise((resolve, reject) => {
    const body = options.body || null;
    const req = https.request(
      url,
      {
        method: options.method || 'GET',
        headers: options.headers,
        timeout: REQUEST_TIMEOUT_MS,
      },
      (res) => {
        // Mirror the upstream behaviour of refusing to follow redirects so that
        // Bearer tokens are never replayed against a redirect target.
        if (res.statusCode >= 300 && res.statusCode < 400) {
          res.resume();
          reject(makeError('claude_redirect_refused', `予期しないリダイレクト (status ${res.statusCode})`));
          return;
        }
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => {
          const body = Buffer.concat(chunks).toString('utf8');
          if (res.statusCode === 200) {
            try {
              resolve({ status: res.statusCode, headers: res.headers, json: JSON.parse(body) });
            } catch (err) {
              reject(makeError('claude_decode_failed', `レスポンスのJSONを解釈できません: ${err.message}`));
            }
            return;
          }
          if (res.statusCode === 401) {
            reject(makeError(
              'claude_unauthorized',
              'Anthropic から認証エラー (401)。`claude login` で再ログインしてください。',
            ));
            return;
          }
          if (res.statusCode === 429) {
            const retryAfterRaw = res.headers['retry-after'];
            const retryAfter = retryAfterRaw ? Number(retryAfterRaw) : null;
            const minsText = retryAfter && !Number.isNaN(retryAfter)
              ? `約 ${Math.max(1, Math.round(retryAfter / 60))} 分後に再試行します。`
              : '次回ポーリングまで待機します。';
            reject(makeError(
              'claude_rate_limited',
              `Anthropic API のレート制限に達しました (429)。${minsText}`,
              { retryAfter },
            ));
            return;
          }
          reject(makeError(
            'claude_http_error',
            `Anthropic API エラー (status ${res.statusCode})`,
            { status: res.statusCode, body: body.slice(0, 500) },
          ));
        });
      },
    );
    req.on('timeout', () => {
      req.destroy(makeError('claude_timeout', '通信がタイムアウトしました。'));
    });
    req.on('error', (err) => {
      if (err.code === 'claude_timeout') {
        reject(err);
        return;
      }
      reject(makeError('claude_network', `ネットワークエラー: ${err.message}`));
    });
    if (body) req.write(body);
    req.end();
  });
}

function httpGetJson(url, headers) {
  return httpJson(url, { method: 'GET', headers });
}

function httpPostJson(url, headers, body) {
  return httpJson(url, { method: 'POST', headers, body });
}

function parseBucket(bucket) {
  if (!bucket) return null;
  const utilization = typeof bucket.utilization === 'number' ? bucket.utilization / 100 : null;
  if (utilization == null) return null;
  // Anthropic returns `resets_at: null` for any window that has not yet been touched
  // in the current period (seen on `five_hour` right after a reset, and routinely on
  // `seven_day_sonnet` / `seven_day_omelette` etc.). That is "0% used, no countdown yet",
  // NOT "data missing" — surface it as a bucket with utilization=0 and resetsAt=null
  // so the UI can render it instead of falling through to N/A.
  const resetsAtRaw = bucket.resets_at;
  let resetsAt = null;
  if (resetsAtRaw) {
    const parsed = Date.parse(resetsAtRaw);
    if (!Number.isNaN(parsed)) resetsAt = parsed;
  }
  return { utilization, resetsAt };
}

async function fetchUsage(accessToken) {
  const { json } = await httpGetJson(USAGE_URL, {
    Authorization: `Bearer ${accessToken}`,
    'anthropic-beta': 'oauth-2025-04-20',
    Accept: 'application/json',
    'User-Agent': 'agent-limit-checker/0.1.0',
  });

  return {
    fiveHour: parseBucket(json.five_hour),
    weekly: parseBucket(json.seven_day),
    weeklySonnet: parseBucket(json.seven_day_sonnet),
  };
}

// Pretty-print the Claude subscription tier for the popover header.
// Inputs we have observed in `~/.claude/.credentials.json#claudeAiOauth`:
//   rateLimitTier      → "default_claude_max_5x", "default_claude_max_20x",
//                        "default_claude_pro", "default_claude_team[s]"
//   subscriptionType   → "max", "pro", "team", "free"
// Prefer the tier (more specific — captures the 5x / 20x multiplier) and
// fall back to subscriptionType.
function extractPlanLabel(oauth) {
  if (!oauth || typeof oauth !== 'object') return null;
  const tier = typeof oauth.rateLimitTier === 'string' ? oauth.rateLimitTier : '';
  const sub = typeof oauth.subscriptionType === 'string' ? oauth.subscriptionType : '';

  const m = tier && tier.match(/claude[_-](pro|max|team[s]?|enterprise|free)(?:[_-](\d+x))?/i);
  if (m) {
    const base = m[1].toLowerCase().replace(/s$/, '');
    const cap = base.charAt(0).toUpperCase() + base.slice(1);
    return m[2] ? `${cap} ${m[2].toLowerCase()}` : cap;
  }
  if (sub) {
    return sub.charAt(0).toUpperCase() + sub.slice(1).toLowerCase();
  }
  return null;
}

function normalizeExpiresAt(value, now = Date.now()) {
  if (value == null) return null;
  const n = Number(value);
  if (!Number.isFinite(n) || n <= 0) return null;
  return n < 10_000_000_000 ? n * 1000 : n;
}

function shouldRefresh(oauth, now = Date.now()) {
  const expiresAt = normalizeExpiresAt(oauth && oauth.expiresAt, now);
  return !!expiresAt && expiresAt <= now + REFRESH_MARGIN_MS;
}

function refreshConfig() {
  const endpoint = process.env.CLAUDE_OAUTH_TOKEN_ENDPOINT;
  const clientId = process.env.CLAUDE_OAUTH_CLIENT_ID;
  if (!endpoint || !clientId) return null;
  return { endpoint, clientId };
}

function mergeRefreshResponse(existingOauth, json, now = Date.now()) {
  const accessToken = json.accessToken || json.access_token;
  if (!accessToken || typeof accessToken !== 'string') {
    throw makeError('claude_refresh_invalid', 'OAuth refresh レスポンスに access token がありません。');
  }

  const refreshToken = json.refreshToken || json.refresh_token || existingOauth.refreshToken;
  const expiresAt = normalizeExpiresAt(
    json.expiresAt || json.expires_at,
    now,
  ) || (Number.isFinite(Number(json.expiresIn || json.expires_in))
    ? now + Number(json.expiresIn || json.expires_in) * 1000
    : existingOauth.expiresAt);

  return {
    ...existingOauth,
    accessToken,
    refreshToken,
    expiresAt,
    scopes: json.scopes || json.scope || existingOauth.scopes,
  };
}

async function writeCredentials(rawCredentials, oauth) {
  const next = {
    ...rawCredentials,
    claudeAiOauth: oauth,
  };
  const dir = path.dirname(CREDENTIALS_PATH);
  const tmp = path.join(dir, `.credentials.${process.pid}.${Date.now()}.tmp`);
  await fs.writeFile(tmp, `${JSON.stringify(next, null, 2)}\n`, 'utf8');
  await fs.rename(tmp, CREDENTIALS_PATH);
}

async function refreshAccessToken(credentials) {
  const config = refreshConfig();
  if (!config) {
    throw makeError(
      'claude_refresh_unconfigured',
      'OAuth refresh は未設定です。`claude login` で再ログインしてください。',
    );
  }
  const refreshToken = credentials.oauth && credentials.oauth.refreshToken;
  if (!refreshToken || typeof refreshToken !== 'string') {
    throw makeError(
      'claude_refresh_token_missing',
      'refresh token が見つかりません。`claude login` で再ログインしてください。',
    );
  }

  const body = new URLSearchParams({
    grant_type: 'refresh_token',
    refresh_token: refreshToken,
    client_id: config.clientId,
  }).toString();

  const { json } = await httpPostJson(config.endpoint, {
    Accept: 'application/json',
    'Content-Type': 'application/x-www-form-urlencoded',
    'Content-Length': Buffer.byteLength(body),
    'User-Agent': 'agent-limit-checker/0.1.0',
  }, body);

  const oauth = mergeRefreshResponse(credentials.oauth, json);
  await writeCredentials(credentials.raw, oauth);
  return { raw: { ...credentials.raw, claudeAiOauth: oauth }, oauth, accessToken: oauth.accessToken };
}

async function readFreshCredentialsIfChanged(previousAccessToken) {
  const latest = await readCredentials();
  if (latest.accessToken !== previousAccessToken) return latest;
  return null;
}

// Spawn `claude auth status --json` non-interactively. The official CLI
// handles its OAuth refresh internally — if the access token is expired but
// the refresh token is still valid, the CLI will silently update
// `~/.claude/.credentials.json`. We then re-read the file and retry.
//
// Returns `true` if the CLI reported `loggedIn: true` (so a retry has a
// chance of succeeding), `false` otherwise. Never throws.
function spawnClaudeAuthStatus(exe) {
  const env = buildChildEnv(process.env);
  const lowered = exe.toLowerCase();
  if (lowered.endsWith('.ps1')) {
    return spawn(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', exe, 'auth', 'status', '--json'],
      { stdio: ['ignore', 'pipe', 'pipe'], env, windowsHide: true },
    );
  }
  if (lowered.endsWith('.cmd') || lowered.endsWith('.bat')) {
    return spawn(
      process.env.ComSpec || 'cmd.exe',
      ['/d', '/s', '/c', `"${exe}" auth status --json`],
      {
        stdio: ['ignore', 'pipe', 'pipe'],
        env,
        windowsHide: true,
        windowsVerbatimArguments: true,
      },
    );
  }
  return spawn(exe, ['auth', 'status', '--json'], {
    stdio: ['ignore', 'pipe', 'pipe'],
    env,
    windowsHide: true,
  });
}

function buildChildEnv(base) {
  // Whitelist only the env vars Claude CLI needs. Crucially, do NOT pass
  // `ANTHROPIC_API_KEY` — if that's set in the parent process the CLI uses
  // it instead of OAuth, which defeats the purpose of refreshing the
  // OAuth token.
  const allow = [
    'HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'PROGRAMDATA',
    'USERNAME', 'TEMP', 'TMP', 'SystemRoot', 'windir',
    'PATH', 'PATHEXT', 'LANG', 'LC_ALL',
    'CLAUDE_HOME', 'XDG_CONFIG_HOME', 'XDG_CACHE_HOME', 'ComSpec',
  ];
  const env = {};
  for (const k of allow) {
    if (base[k] != null) env[k] = base[k];
  }
  return env;
}

async function nudgeClaudeRefresh() {
  const exe = resolveClaudeExecutable();
  if (!exe) return false;

  return new Promise((resolve) => {
    let proc;
    try {
      proc = spawnClaudeAuthStatus(exe);
    } catch {
      resolve(false);
      return;
    }
    const stdoutChunks = [];
    proc.stdout.on('data', (c) => stdoutChunks.push(c));
    proc.stderr.on('data', () => { /* drain */ });
    const timer = setTimeout(() => {
      try { proc.kill(); } catch { /* ignore */ }
      resolve(false);
    }, CLI_NUDGE_TIMEOUT_MS);
    proc.on('error', () => {
      clearTimeout(timer);
      resolve(false);
    });
    proc.on('exit', (code) => {
      clearTimeout(timer);
      if (code !== 0) { resolve(false); return; }
      try {
        const parsed = JSON.parse(Buffer.concat(stdoutChunks).toString('utf8'));
        resolve(parsed && parsed.loggedIn === true);
      } catch {
        resolve(false);
      }
    });
  });
}

// Try every available avenue to obtain a usable access token. Returns the
// (possibly-updated) credentials object on success, or the original
// `credentials` if no avenue worked.
async function tryRecoverCredentials(credentials) {
  // 1) Direct OAuth refresh via env-var-configured endpoint.
  try {
    return await refreshAccessToken(credentials);
  } catch (err) {
    if (err.code !== 'claude_refresh_unconfigured'
        && err.code !== 'claude_refresh_token_missing') {
      throw err;
    }
    // fall through to CLI nudge
  }

  // 2) Spawn `claude auth status` — the CLI refreshes credentials.json
  //    itself when the access token is expired and the refresh token is
  //    still valid.
  const nudged = await nudgeClaudeRefresh();
  if (nudged) {
    const reread = await readFreshCredentialsIfChanged(credentials.accessToken);
    if (reread) return reread;
    // CLI says we're logged in but the file hasn't changed — token is still
    // valid (or CLI didn't refresh). Return the original credentials and let
    // the caller decide what to do.
  }
  return credentials;
}

async function fetch() {
  let credentials = await readCredentials();

  if (shouldRefresh(credentials.oauth)) {
    credentials = await tryRecoverCredentials(credentials);
  }

  const plan = extractPlanLabel(credentials.oauth);

  try {
    const usage = await fetchUsage(credentials.accessToken);
    return { ...usage, plan };
  } catch (err) {
    if (err.code !== 'claude_unauthorized') throw err;

    // 1) Another process may have already refreshed in the meantime.
    const reread = await readFreshCredentialsIfChanged(credentials.accessToken);
    if (reread) {
      try {
        const usage = await fetchUsage(reread.accessToken);
        return { ...usage, plan: extractPlanLabel(reread.oauth) };
      } catch (retryErr) {
        if (retryErr.code !== 'claude_unauthorized') throw retryErr;
        credentials = reread;
      }
    }

    // 2) Nudge the CLI to refresh.
    const nudged = await nudgeClaudeRefresh();
    if (nudged) {
      const afterNudge = await readFreshCredentialsIfChanged(credentials.accessToken);
      if (afterNudge) {
        try {
          const usage = await fetchUsage(afterNudge.accessToken);
          return { ...usage, plan: extractPlanLabel(afterNudge.oauth) };
        } catch (retryErr) {
          if (retryErr.code !== 'claude_unauthorized') throw retryErr;
        }
      }
    }

    // 3) Last resort: direct OAuth refresh via env vars.
    try {
      const refreshed = await refreshAccessToken(credentials);
      const usage = await fetchUsage(refreshed.accessToken);
      return { ...usage, plan: extractPlanLabel(refreshed.oauth) };
    } catch (refreshErr) {
      if (refreshErr.code && refreshErr.code.startsWith('claude_refresh_')) {
        throw makeError(
          'claude_unauthorized',
          'Anthropic から認証エラー (401)。`claude login` で再ログインしてください。',
        );
      }
      throw refreshErr;
    }
  }
}

async function shutdown() {
  // nothing persistent
}

module.exports = {
  fetch,
  shutdown,
  _private: {
    mergeRefreshResponse,
    normalizeExpiresAt,
    shouldRefresh,
    parseBucket,
    extractPlanLabel,
    buildChildEnv,
  },
};
