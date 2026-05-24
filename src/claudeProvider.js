'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const https = require('node:https');

const USAGE_URL = 'https://api.anthropic.com/api/oauth/usage';
const CREDENTIALS_PATH = path.join(os.homedir(), '.claude', '.credentials.json');
const REQUEST_TIMEOUT_MS = 12_000;

async function readAccessToken() {
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
  const token = parsed && parsed.claudeAiOauth && parsed.claudeAiOauth.accessToken;
  if (!token || typeof token !== 'string') {
    throw makeError(
      'claude_credentials_missing',
      'アクセストークンが見つかりません。`claude login` で再ログインしてください。',
    );
  }
  return token;
}

function makeError(code, message, extra) {
  const err = new Error(message);
  err.code = code;
  if (extra) Object.assign(err, extra);
  return err;
}

function httpGetJson(url, headers) {
  return new Promise((resolve, reject) => {
    const req = https.request(
      url,
      {
        method: 'GET',
        headers,
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
    req.end();
  });
}

function parseBucket(bucket) {
  if (!bucket) return null;
  const utilization = typeof bucket.utilization === 'number' ? bucket.utilization / 100 : null;
  const resetsAtRaw = bucket.resets_at;
  if (utilization == null || !resetsAtRaw) return null;
  const resetsAt = Date.parse(resetsAtRaw);
  if (Number.isNaN(resetsAt)) return null;
  return { utilization, resetsAt };
}

async function fetch() {
  const token = await readAccessToken();
  const { json } = await httpGetJson(USAGE_URL, {
    Authorization: `Bearer ${token}`,
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

async function shutdown() {
  // nothing persistent
}

module.exports = { fetch, shutdown };
