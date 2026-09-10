'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const https = require('node:https');

const USAGE_URL = 'https://api.anthropic.com/api/oauth/usage';
const CREDENTIALS_PATH = path.join(os.homedir(), '.claude', '.credentials.json');
const REQUEST_TIMEOUT_MS = 12_000;

// Claude Code CLI 自身が OAuth refresh に使う endpoint / client_id。公開仕様では
// なく CLI に埋め込まれた値なので、上流の更新で変わりうる。値が無効になると
// refresh が 400/401 で失敗し、`claude login` によるブラウザ再ログインへ落ちる。
// 環境変数を設定すればここを上書きできる。
const DEFAULT_TOKEN_ENDPOINT = 'https://platform.claude.com/v1/oauth/token';
const DEFAULT_CLIENT_ID = '9d1c250a-e61b-44d9-88ed-5944d1962f5e';

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

// Per-model weekly caps (e.g. the Fable-5 "up to 50% of your weekly limit"
// allowance) now arrive in the structured `limits` array, NOT the flat
// `seven_day_sonnet` field — Anthropic returns the flat scoped buckets as
// `null` once the scoped model changes. Each `weekly_scoped` entry carries its
// own model `display_name` and `resets_at`, and its `percent` is normalized to
// that scope's own cap (100% = that model is exhausted for the week), exactly
// like the old per-model buckets. So Fable's 50%-of-weekly ceiling is baked
// into the denominator — we surface `percent` as-is, the same way the Sonnet
// meter did. Returns an array so multiple scoped models render side by side.
// Falls back to the legacy `seven_day_sonnet` field for older API responses
// that predate the `limits` array.
function parseWeeklyScoped(json) {
  const limits = json && Array.isArray(json.limits) ? json.limits : null;
  if (limits) {
    const scoped = [];
    for (const entry of limits) {
      if (!entry || entry.group !== 'weekly' || entry.kind !== 'weekly_scoped') continue;
      const utilization = typeof entry.percent === 'number' ? entry.percent / 100 : null;
      if (utilization == null) continue;
      const model = entry.scope && entry.scope.model;
      // `id` is the stable scope identifier (survives display renames);
      // `display_name` is the human label and may be renamed or absent. Keep
      // both — the notification dedupe key wants the stable id, the UI wants
      // the label.
      const id = model && typeof model.id === 'string' && model.id ? model.id : null;
      const label = model && typeof model.display_name === 'string' && model.display_name
        ? model.display_name
        : 'スコープ';
      let resetsAt = null;
      if (entry.resets_at) {
        const parsed = Date.parse(entry.resets_at);
        if (!Number.isNaN(parsed)) resetsAt = parsed;
      }
      scoped.push({ id, label, utilization, resetsAt });
    }
    // `limits` present but no usable weekly_scoped entry (partial rollout, or a
    // shape change that skipped them all) — fall through to the legacy flat
    // field so an existing scoped cap doesn't silently disappear from the UI.
    if (scoped.length) return scoped;
  }
  const legacy = parseBucket(json && json.seven_day_sonnet);
  return legacy ? [{ id: null, label: 'Sonnet', ...legacy }] : [];
}

// Anthropic surfaces a usage-credit balance under `spend.balance`, using the
// same money shape as `spend.used` / `spend.limit`:
//   { "amount_minor": 500, "currency": "USD", "exponent": 2 }  → $5.00
// `spend.balance` comes back `null` whenever the account has no credit balance
// (the common case for plan-only accounts) — we return null so the UI hides the
// row instead of showing a bogus $0. Older API responses predate the `spend`
// object entirely; those also fall through to null. `amount` is normalized to
// major currency units (dollars) so the renderer only has to format it.
function parseCredits(json) {
  const spend = json && json.spend;
  const balance = spend && spend.balance;
  if (!balance || typeof balance !== 'object') return null;
  const amountMinor = Number(balance.amount_minor);
  const exponent = Number(balance.exponent);
  if (!Number.isFinite(amountMinor) || !Number.isFinite(exponent)) return null;
  const amount = amountMinor / 10 ** exponent;
  // No balance (or a rounding artefact that lands at/below zero) → hide.
  if (!(amount > 0)) return null;
  const currency = typeof balance.currency === 'string' && balance.currency
    ? balance.currency
    : 'USD';
  return { amount, currency, unlimited: false };
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
    weeklyScoped: parseWeeklyScoped(json),
    credits: parseCredits(json),
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

// refresh token は refresh のたびに回転し、新しい token が出た時点で古い token は
// 無効になる。CLI と同時に refresh すると片方が失われるので、期限内の前倒し
// refresh はしない。「既に期限切れか」だけを見る。
function shouldRefresh(oauth, now = Date.now()) {
  const expiresAt = normalizeExpiresAt(oauth && oauth.expiresAt, now);
  return !!expiresAt && expiresAt <= now;
}

function refreshConfig() {
  return {
    endpoint: process.env.CLAUDE_OAUTH_TOKEN_ENDPOINT || DEFAULT_TOKEN_ENDPOINT,
    clientId: process.env.CLAUDE_OAUTH_CLIENT_ID || DEFAULT_CLIENT_ID,
  };
}

// 不変条件: credentials.json の `claudeAiOauth.scopes` は必ず配列である。CLI は
// この値を配列として扱い、文字列だと `loggedIn: false` になる。token endpoint は
// スペース区切りの文字列 (`scope`) を返すので、ここで配列へ正規化する。
function normalizeScopes(value, fallback) {
  if (Array.isArray(value)) return value;
  if (typeof value === 'string') {
    const parts = value.split(/\s+/).filter(Boolean);
    if (parts.length) return parts;
  }
  return fallback;
}

// 絶対時刻 (epoch 秒/ミリ秒) があればそれを、無ければ相対秒から算出し、
// どちらも無ければ既存値を維持する。
function resolveExpiry(absolute, relativeSeconds, fallback, now) {
  const abs = normalizeExpiresAt(absolute, now);
  if (abs) return abs;
  if (relativeSeconds != null) {
    const secs = Number(relativeSeconds);
    if (Number.isFinite(secs) && secs > 0) return now + secs * 1000;
  }
  return fallback;
}

function mergeRefreshResponse(existingOauth, json, now = Date.now()) {
  const accessToken = json.accessToken || json.access_token;
  if (!accessToken || typeof accessToken !== 'string') {
    throw makeError('claude_refresh_invalid', 'OAuth refresh レスポンスに access token がありません。');
  }

  const refreshToken = json.refreshToken || json.refresh_token || existingOauth.refreshToken;
  const expiresAt = resolveExpiry(
    json.expiresAt || json.expires_at,
    json.expiresIn != null ? json.expiresIn : json.expires_in,
    existingOauth.expiresAt,
    now,
  );
  const refreshTokenExpiresAt = resolveExpiry(
    json.refreshTokenExpiresAt || json.refresh_token_expires_at,
    json.refreshTokenExpiresIn != null ? json.refreshTokenExpiresIn : json.refresh_token_expires_in,
    existingOauth.refreshTokenExpiresAt,
    now,
  );

  // CLI が読む未知のフィールドを落とさないよう、既存の値を土台にする。
  return {
    ...existingOauth,
    accessToken,
    refreshToken,
    expiresAt,
    refreshTokenExpiresAt,
    scopes: normalizeScopes(json.scopes || json.scope, normalizeScopes(existingOauth.scopes, [])),
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

// access token を直接 refresh する。成功したら credentials.json を書き換え、
// 更新後の credentials を返す。他プロセス (CLI) が先に refresh していた場合は
// POST せずにその結果を返す。
async function refreshAccessToken(credentials) {
  const config = refreshConfig();
  const refreshToken = credentials.oauth && credentials.oauth.refreshToken;
  if (!refreshToken || typeof refreshToken !== 'string') {
    throw makeError(
      'claude_refresh_token_missing',
      'refresh token が見つかりません。`claude login` で再ログインしてください。',
    );
  }

  // refresh token 自体の期限 (約 30 日) が切れていれば refresh は必ず失敗する。
  // 無駄な POST を避けて再ログインへ回す。
  const refreshTokenExpiresAt = normalizeExpiresAt(credentials.oauth.refreshTokenExpiresAt);
  if (refreshTokenExpiresAt && refreshTokenExpiresAt <= Date.now()) {
    throw makeError(
      'claude_refresh_expired',
      'refresh token の期限が切れています。`claude login` で再ログインしてください。',
    );
  }

  // refresh token は回転するため、CLI が先に refresh していると手元の token は
  // 既に無効。POST 直前にファイルを読み直し、更新済みならその値をそのまま使う。
  const reread = await readFreshCredentialsIfChanged(credentials.accessToken);
  if (reread) return reread;

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

// refresh の失敗を「refresh token が死んでいて再ログインしか手が無い」ものと
// 「一時的な障害」に分ける。前者だけ claude_unauthorized に変換し、main.js の
// フォールバック (ポップオーバーを開いたときのブラウザ再ログイン) へ渡す。
// 一時障害まで変換すると、通信断やサーバ障害のたびにブラウザが開いてしまう。
function isFatalRefreshError(err) {
  if (!err || !err.code) return false;
  // token endpoint の 400 は invalid_grant 等、401 は client 認証の失敗。
  // どちらも手元の refresh token では復旧できない。
  if (err.code === 'claude_http_error') return err.status === 400;
  if (err.code === 'claude_unauthorized') return true;
  if (err.code === 'claude_refresh_token_missing') return true;
  if (err.code === 'claude_refresh_expired') return true;
  // 429 / 5xx / ネットワーク / タイムアウト、およびレスポンス形状の異常は
  // 再ログインで直るものではないので、そのまま呼び出し元へ返す。
  return false;
}

// 期限切れや 401 からの回復を試みる。成功したら使える credentials を返し、
// 再ログインが要る場合だけ claude_unauthorized を投げる。
async function tryRecoverCredentials(credentials) {
  try {
    return await refreshAccessToken(credentials);
  } catch (err) {
    if (!isFatalRefreshError(err)) throw err;
    // 他プロセス (CLI) が先に refresh していれば、手元の refresh token が
    // 無効になっているだけで再ログインは要らない。更新後の値を使う。
    const reread = await readFreshCredentialsIfChanged(credentials.accessToken);
    if (reread) return reread;
    throw makeError(
      'claude_unauthorized',
      'Anthropic から認証エラー (401)。`claude login` で再ログインしてください。',
    );
  }
}

async function fetch() {
  let credentials = await readCredentials();

  // 期限切れのときだけ事前に refresh する。期限内なら usage を叩き、401 が
  // 返ったときだけ回復に入る。
  if (shouldRefresh(credentials.oauth)) {
    credentials = await tryRecoverCredentials(credentials);
  }

  try {
    const usage = await fetchUsage(credentials.accessToken);
    return { ...usage, plan: extractPlanLabel(credentials.oauth) };
  } catch (err) {
    if (err.code !== 'claude_unauthorized') throw err;

    // 1) 他プロセス (CLI) が既に refresh 済みかもしれないので読み直して 1 回試す。
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

    // 2) 直接 refresh する。再ログインが必要かどうかの判定は
    //    tryRecoverCredentials に任せる。
    const recovered = await tryRecoverCredentials(credentials);
    const usage = await fetchUsage(recovered.accessToken);
    return { ...usage, plan: extractPlanLabel(recovered.oauth) };
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
    refreshConfig,
    isFatalRefreshError,
    parseBucket,
    parseWeeklyScoped,
    parseCredits,
    extractPlanLabel,
  },
};
