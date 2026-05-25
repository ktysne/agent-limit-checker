'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const https = require('node:https');

const USAGE_URL = 'https://api.anthropic.com/api/oauth/usage';
const CREDENTIALS_PATH = path.join(os.homedir(), '.claude', '.credentials.json');
const REQUEST_TIMEOUT_MS = 12_000;
const REFRESH_MARGIN_MS = 60_000;

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

async function fetch() {
  let credentials = await readCredentials();

  if (shouldRefresh(credentials.oauth)) {
    try {
      credentials = await refreshAccessToken(credentials);
    } catch (err) {
      if (err.code !== 'claude_refresh_unconfigured') throw err;
    }
  }

  const plan = extractPlanLabel(credentials.oauth);

  try {
    const usage = await fetchUsage(credentials.accessToken);
    return { ...usage, plan };
  } catch (err) {
    if (err.code !== 'claude_unauthorized') throw err;

    const reread = await readFreshCredentialsIfChanged(credentials.accessToken);
    if (reread) {
      try {
        const usage = await fetchUsage(reread.accessToken);
        return { ...usage, plan: extractPlanLabel(reread.oauth) };
      } catch (retryErr) {
        if (retryErr.code !== 'claude_unauthorized') throw retryErr;
      }
    }

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
  },
};
