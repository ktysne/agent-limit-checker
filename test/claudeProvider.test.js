'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { EventEmitter } = require('node:events');

const provider = require('../src/claudeProvider');

const { _private } = provider;

test('normalizes Claude OAuth expiry timestamps', () => {
  assert.equal(_private.normalizeExpiresAt(1_779_632_820), 1_779_632_820_000);
  assert.equal(_private.normalizeExpiresAt(1_779_632_820_450), 1_779_632_820_450);
  assert.equal(_private.normalizeExpiresAt('bad'), null);
});

test('shouldRefresh only fires once the access token has actually expired', () => {
  // refresh token は refresh のたびに回転するので、期限内に前倒しで refresh
  // すると CLI 側の token を無効にしうる。期限切れだけを対象にする。
  const now = 1_779_600_000_000;
  assert.equal(_private.shouldRefresh({ expiresAt: now }, now), true);
  assert.equal(_private.shouldRefresh({ expiresAt: now - 1 }, now), true);
  assert.equal(_private.shouldRefresh({ expiresAt: now + 30_000 }, now), false);
  assert.equal(_private.shouldRefresh({ expiresAt: now + 120_000 }, now), false);
  assert.equal(_private.shouldRefresh({}, now), false);
});

test('parseBucket keeps zero-utilization windows whose resets_at is null', () => {
  // Anthropic は「このウィンドウでまだ消費していない」とき resets_at: null を返す
  // (例: five_hour リセット直後、seven_day_sonnet など)。
  // 旧実装はこれをまるごと null 扱いして UI が "N/A" を出していた。
  const bucket = _private.parseBucket({ utilization: 0, resets_at: null });
  assert.deepEqual(bucket, { utilization: 0, resetsAt: null });
});

test('parseBucket parses microsecond-precision resets_at returned by /api/oauth/usage', () => {
  const bucket = _private.parseBucket({
    utilization: 3,
    resets_at: '2026-05-24T15:30:00.338620+00:00',
  });
  assert.ok(bucket, 'bucket should not be dropped');
  assert.equal(bucket.utilization, 0.03);
  assert.equal(bucket.resetsAt, Date.parse('2026-05-24T15:30:00.338620+00:00'));
});

test('parseBucket returns null when utilization is missing', () => {
  assert.equal(_private.parseBucket(null), null);
  assert.equal(_private.parseBucket({ utilization: null, resets_at: '2026-01-01T00:00:00Z' }), null);
  assert.equal(_private.parseBucket({}), null);
});

test('parseWeeklyScoped reads per-model weekly caps from the limits array', () => {
  // Shape observed on /api/oauth/usage once Fable 5 became conditionally
  // available: the flat `seven_day_sonnet` bucket is null and the scoped cap
  // lives in `limits` as a weekly_scoped entry. `percent` is normalized to the
  // scope's own cap (here Fable's 50%-of-weekly allowance), so 32 → 0.32.
  const scoped = _private.parseWeeklyScoped({
    seven_day_sonnet: null,
    limits: [
      { kind: 'session', group: 'session', percent: 49, resets_at: '2026-07-02T11:29:59+00:00' },
      { kind: 'weekly_all', group: 'weekly', percent: 17, resets_at: '2026-07-08T10:00:00+00:00' },
      {
        kind: 'weekly_scoped',
        group: 'weekly',
        percent: 32,
        resets_at: '2026-07-08T09:59:59+00:00',
        scope: { model: { id: null, display_name: 'Fable' } },
      },
    ],
  });
  assert.deepEqual(scoped, [
    {
      id: null, // Fable currently reports scope.model.id as null
      label: 'Fable',
      utilization: 0.32,
      resetsAt: Date.parse('2026-07-08T09:59:59+00:00'),
    },
  ]);
});

test('parseWeeklyScoped keeps the stable scope id when the API supplies one', () => {
  const scoped = _private.parseWeeklyScoped({
    limits: [
      {
        kind: 'weekly_scoped',
        group: 'weekly',
        percent: 32,
        resets_at: '2026-07-08T09:59:59+00:00',
        scope: { model: { id: 'claude-fable-5', display_name: 'Fable' } },
      },
    ],
  });
  assert.equal(scoped[0].id, 'claude-fable-5');
  assert.equal(scoped[0].label, 'Fable');
});

test('parseWeeklyScoped skips scoped entries with a non-numeric percent', () => {
  const scoped = _private.parseWeeklyScoped({
    limits: [
      { kind: 'weekly_scoped', group: 'weekly', percent: null, scope: { model: { display_name: 'Fable' } } },
    ],
  });
  assert.deepEqual(scoped, []);
});

test('parseWeeklyScoped falls back to the legacy seven_day_sonnet bucket', () => {
  // Older API responses (no `limits` array) still carried the scoped weekly
  // cap in the flat `seven_day_sonnet` field. Keep surfacing it as Sonnet.
  const scoped = _private.parseWeeklyScoped({
    seven_day_sonnet: { utilization: 8, resets_at: '2026-07-08T10:00:00+00:00' },
  });
  assert.deepEqual(scoped, [
    {
      id: null,
      label: 'Sonnet',
      utilization: 0.08,
      resetsAt: Date.parse('2026-07-08T10:00:00+00:00'),
    },
  ]);
  assert.deepEqual(_private.parseWeeklyScoped({}), []);
});

test('parseWeeklyScoped falls back to legacy when limits carry no weekly_scoped entry', () => {
  // A partial rollout can send the `limits` array while the scoped cap still
  // lives only in the flat field. Don't let the meter vanish in that window.
  const scoped = _private.parseWeeklyScoped({
    limits: [
      { kind: 'session', group: 'session', percent: 49 },
      { kind: 'weekly_all', group: 'weekly', percent: 17 },
    ],
    seven_day_sonnet: { utilization: 8, resets_at: '2026-07-08T10:00:00+00:00' },
  });
  assert.deepEqual(scoped, [
    {
      id: null,
      label: 'Sonnet',
      utilization: 0.08,
      resetsAt: Date.parse('2026-07-08T10:00:00+00:00'),
    },
  ]);
  // Empty limits + no legacy bucket → nothing to show.
  assert.deepEqual(_private.parseWeeklyScoped({ limits: [] }), []);
});

test('parseCredits reads the usage-credit balance from spend.balance', () => {
  // Observed shape on /api/oauth/usage: `spend.balance` mirrors spend.used /
  // spend.limit — { amount_minor, currency, exponent }. exponent 2 → cents.
  const credits = _private.parseCredits({
    spend: { balance: { amount_minor: 2599, currency: 'USD', exponent: 2 } },
  });
  assert.equal(credits.currency, 'USD');
  assert.equal(credits.unlimited, false);
  assert.ok(Math.abs(credits.amount - 25.99) < 1e-9, `amount was ${credits.amount}`);
});

test('parseCredits hides the row when there is no positive balance', () => {
  // `spend.balance: null` is the common plan-only case — hide, do not show $0.
  assert.equal(_private.parseCredits({ spend: { balance: null } }), null);
  assert.equal(_private.parseCredits({ spend: {} }), null);
  // Older responses predate the `spend` object entirely.
  assert.equal(_private.parseCredits({}), null);
  assert.equal(_private.parseCredits(null), null);
  // A zeroed-out balance is still "no credits available".
  assert.equal(
    _private.parseCredits({ spend: { balance: { amount_minor: 0, currency: 'USD', exponent: 2 } } }),
    null,
  );
  // Missing amount/exponent → can't trust the number, so hide.
  assert.equal(
    _private.parseCredits({ spend: { balance: { currency: 'USD' } } }),
    null,
  );
  // A negative balance is not something to advertise.
  assert.equal(
    _private.parseCredits({ spend: { balance: { amount_minor: -100, currency: 'USD', exponent: 2 } } }),
    null,
  );
  // `balance` must be the money object, not a bare number/string.
  assert.equal(_private.parseCredits({ spend: { balance: 5 } }), null);
  assert.equal(_private.parseCredits({ spend: { balance: '5' } }), null);
});

test('parseCredits defaults the currency to USD when the API omits it', () => {
  const credits = _private.parseCredits({
    spend: { balance: { amount_minor: 500, exponent: 2 } },
  });
  assert.equal(credits.currency, 'USD');
  assert.equal(credits.amount, 5);
});

test('extractPlanLabel parses Claude rateLimitTier into a human label', () => {
  // Observed in `~/.claude/.credentials.json`:
  //   "rateLimitTier": "default_claude_max_5x"
  assert.equal(
    _private.extractPlanLabel({ rateLimitTier: 'default_claude_max_5x' }),
    'Max 5x',
  );
  assert.equal(
    _private.extractPlanLabel({ rateLimitTier: 'default_claude_max_20x' }),
    'Max 20x',
  );
  assert.equal(
    _private.extractPlanLabel({ rateLimitTier: 'default_claude_pro' }),
    'Pro',
  );
  // "teams" with trailing s is normalized to "Team".
  assert.equal(
    _private.extractPlanLabel({ rateLimitTier: 'default_claude_teams' }),
    'Team',
  );
});

test('extractPlanLabel falls back to subscriptionType when tier is missing or unknown', () => {
  assert.equal(
    _private.extractPlanLabel({ subscriptionType: 'max' }),
    'Max',
  );
  assert.equal(
    _private.extractPlanLabel({
      rateLimitTier: 'something_weird',
      subscriptionType: 'pro',
    }),
    'Pro',
  );
  assert.equal(_private.extractPlanLabel({}), null);
  assert.equal(_private.extractPlanLabel(null), null);
});

test('merges snake_case OAuth refresh response without dropping existing metadata', () => {
  const now = 1_000_000;
  const merged = _private.mergeRefreshResponse({
    accessToken: 'old-access',
    refreshToken: 'old-refresh',
    subscriptionType: 'max',
    rateLimitTier: 'standard',
  }, {
    access_token: 'new-access',
    refresh_token: 'new-refresh',
    expires_in: 3600,
    scope: ['profile'],
  }, now);

  assert.equal(merged.accessToken, 'new-access');
  assert.equal(merged.refreshToken, 'new-refresh');
  assert.equal(merged.expiresAt, now + 3_600_000);
  assert.equal(merged.subscriptionType, 'max');
  assert.equal(merged.rateLimitTier, 'standard');
  assert.deepEqual(merged.scopes, ['profile']);
});

test('mergeRefreshResponse splits the space-separated scope string into an array', () => {
  // credentials.json の scopes は配列でなければならない (CLI が配列前提で読む)。
  // token endpoint はスペース区切りの文字列を返すので、ここで配列化する。
  const merged = _private.mergeRefreshResponse(
    { accessToken: 'old', refreshToken: 'old-refresh', scopes: ['user:inference'] },
    { access_token: 'new', scope: 'user:inference user:profile' },
    1_000_000,
  );
  assert.deepEqual(merged.scopes, ['user:inference', 'user:profile']);
});

test('mergeRefreshResponse keeps the existing scopes when the response omits them', () => {
  const merged = _private.mergeRefreshResponse(
    { accessToken: 'old', refreshToken: 'old-refresh', scopes: ['user:inference'] },
    { access_token: 'new' },
    1_000_000,
  );
  assert.deepEqual(merged.scopes, ['user:inference']);
});

test('mergeRefreshResponse records refreshTokenExpiresAt from the response', () => {
  const now = 1_000_000;
  const merged = _private.mergeRefreshResponse(
    { accessToken: 'old', refreshToken: 'old-refresh', refreshTokenExpiresAt: 5 },
    { access_token: 'new', refresh_token_expires_in: 60 },
    now,
  );
  assert.equal(merged.refreshTokenExpiresAt, now + 60_000);

  // 絶対時刻 (epoch 秒) でもよい。
  const absolute = _private.mergeRefreshResponse(
    { accessToken: 'old', refreshToken: 'old-refresh' },
    { access_token: 'new', refresh_token_expires_at: 1_779_632_820 },
    now,
  );
  assert.equal(absolute.refreshTokenExpiresAt, 1_779_632_820_000);

  // 無ければ既存値を維持する。
  const kept = _private.mergeRefreshResponse(
    { accessToken: 'old', refreshToken: 'old-refresh', refreshTokenExpiresAt: 42 },
    { access_token: 'new' },
    now,
  );
  assert.equal(kept.refreshTokenExpiresAt, 42);
});

test('mergeRefreshResponse keeps the previous refresh token when the response omits one', () => {
  const merged = _private.mergeRefreshResponse(
    { accessToken: 'old', refreshToken: 'old-refresh' },
    { access_token: 'new' },
    1_000_000,
  );
  assert.equal(merged.refreshToken, 'old-refresh');
});

test('refreshConfig defaults to the CLI endpoint and lets env override it', () => {
  const savedEndpoint = process.env.CLAUDE_OAUTH_TOKEN_ENDPOINT;
  const savedClientId = process.env.CLAUDE_OAUTH_CLIENT_ID;
  try {
    delete process.env.CLAUDE_OAUTH_TOKEN_ENDPOINT;
    delete process.env.CLAUDE_OAUTH_CLIENT_ID;
    const fallback = _private.refreshConfig();
    assert.equal(fallback.endpoint, 'https://platform.claude.com/v1/oauth/token');
    assert.equal(fallback.clientId, '9d1c250a-e61b-44d9-88ed-5944d1962f5e');

    process.env.CLAUDE_OAUTH_TOKEN_ENDPOINT = 'https://example.test/token';
    process.env.CLAUDE_OAUTH_CLIENT_ID = 'client-x';
    const overridden = _private.refreshConfig();
    assert.equal(overridden.endpoint, 'https://example.test/token');
    assert.equal(overridden.clientId, 'client-x');
  } finally {
    if (savedEndpoint == null) delete process.env.CLAUDE_OAUTH_TOKEN_ENDPOINT;
    else process.env.CLAUDE_OAUTH_TOKEN_ENDPOINT = savedEndpoint;
    if (savedClientId == null) delete process.env.CLAUDE_OAUTH_CLIENT_ID;
    else process.env.CLAUDE_OAUTH_CLIENT_ID = savedClientId;
  }
});

test('isFatalRefreshError separates a dead refresh token from a transient failure', () => {
  // 再ログインしか手が無いもの → claude_unauthorized に変換される。
  assert.equal(_private.isFatalRefreshError({ code: 'claude_http_error', status: 400 }), true);
  assert.equal(_private.isFatalRefreshError({ code: 'claude_unauthorized' }), true);
  assert.equal(_private.isFatalRefreshError({ code: 'claude_refresh_token_missing' }), true);
  assert.equal(_private.isFatalRefreshError({ code: 'claude_refresh_expired' }), true);

  // 一時障害 → そのまま投げる (ブラウザを開かせない)。
  assert.equal(_private.isFatalRefreshError({ code: 'claude_rate_limited' }), false);
  assert.equal(_private.isFatalRefreshError({ code: 'claude_http_error', status: 500 }), false);
  assert.equal(_private.isFatalRefreshError({ code: 'claude_http_error', status: 503 }), false);
  assert.equal(_private.isFatalRefreshError({ code: 'claude_network' }), false);
  assert.equal(_private.isFatalRefreshError({ code: 'claude_timeout' }), false);
  assert.equal(_private.isFatalRefreshError({ code: 'claude_refresh_invalid' }), false);
  assert.equal(_private.isFatalRefreshError(null), false);
});

test('describeRefreshFailure falls back to the status when the body carries no OAuth error', () => {
  assert.equal(
    _private.describeRefreshFailure({ status: 400, body: '{"error":"invalid_client"}' }),
    'invalid_client',
  );
  assert.equal(
    _private.describeRefreshFailure({
      status: 400,
      body: '{"error":"invalid_grant","error_description":"refresh token expired"}',
    }),
    'invalid_grant',
  );
  // 自由文や未知のコードはログ / UI に流さない。
  assert.equal(
    _private.describeRefreshFailure({
      status: 400,
      body: '{"error":"weird\nline","error_description":"secret"}',
    }),
    'status 400',
  );
  assert.equal(_private.describeRefreshFailure({ status: 401, body: '<html>nope</html>' }), 'status 401');
  assert.equal(_private.describeRefreshFailure({ code: 'claude_refresh_expired' }), null);
  assert.equal(_private.describeRefreshFailure(null), null);
});

// --- refresh 経路のテスト -------------------------------------------------
// credentials は CLAUDE_CONFIG_DIR で一時ディレクトリへ逃がし、HTTP は
// _private.setTransport で差し替える。実物の ~/.claude/.credentials.json には
// 触れない。どのテストも finally で env と transport を必ず戻す。

const DAY_MS = 86_400_000;

const USAGE_BODY = JSON.stringify({
  five_hour: { utilization: 12, resets_at: '2026-07-02T11:29:59+00:00' },
  seven_day: { utilization: 5, resets_at: '2026-07-08T10:00:00+00:00' },
});

function isUsageCall(call) {
  return call.url.includes('/oauth/usage');
}

function oauthFixture(overrides) {
  const now = Date.now();
  return {
    accessToken: 'old-access',
    refreshToken: 'old-refresh',
    expiresAt: now + 3_600_000,
    refreshTokenExpiresAt: now + 30 * DAY_MS,
    scopes: ['user:inference'],
    subscriptionType: 'max',
    rateLimitTier: 'default_claude_max_5x',
    ...overrides,
  };
}

function createHarness(credentials) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'alc-claude-'));
  const file = path.join(dir, '.credentials.json');
  const savedConfigDir = process.env.CLAUDE_CONFIG_DIR;
  fs.writeFileSync(file, `${JSON.stringify(credentials, null, 2)}\n`, 'utf8');
  process.env.CLAUDE_CONFIG_DIR = dir;
  return {
    file,
    read() {
      return JSON.parse(fs.readFileSync(file, 'utf8'));
    },
    write(value) {
      fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
    },
    restore() {
      if (savedConfigDir == null) delete process.env.CLAUDE_CONFIG_DIR;
      else process.env.CLAUDE_CONFIG_DIR = savedConfigDir;
      _private.setTransport(null);
      fs.rmSync(dir, { recursive: true, force: true });
    },
  };
}

// node:https の request と同じ形をした差し替え。handler は記録済みの call を
// 受け取り、{ status, headers, body } を返す (throw で通信エラー)。送信した
// リクエストは impl.calls に残るので、assert は handler の中ではなく fetch()
// の後で行う (handler の中で throw するとネットワークエラーに化ける)。
function mockTransport(handler) {
  const calls = [];
  const impl = (url, options, cb) => {
    const req = new EventEmitter();
    let payload = '';
    req.write = (chunk) => { payload += chunk; };
    req.destroy = (err) => { if (err) req.emit('error', err); };
    req.end = () => {
      const call = {
        url: String(url),
        method: options.method,
        headers: options.headers || {},
        body: payload,
      };
      calls.push(call);
      Promise.resolve()
        .then(() => handler(call))
        .then((result) => {
          const res = new EventEmitter();
          res.statusCode = result.status;
          res.headers = result.headers || {};
          res.resume = () => {};
          cb(res);
          if (result.body) res.emit('data', Buffer.from(result.body));
          res.emit('end');
        }, (err) => { req.emit('error', err); });
    };
    return req;
  };
  impl.calls = calls;
  return impl;
}

test('refreshes an expired access token and fetches usage with the new one', async () => {
  const harness = createHarness({
    schemaVersion: 1,
    claudeAiOauth: oauthFixture({ expiresAt: Date.now() - 1_000, unknownField: 'keep-me' }),
  });
  const transport = mockTransport((call) => (isUsageCall(call)
    ? { status: 200, body: USAGE_BODY }
    : {
      status: 200,
      body: JSON.stringify({
        access_token: 'new-access',
        refresh_token: 'new-refresh',
        expires_in: 28_800,
        scope: 'user:inference user:profile',
      }),
    }));
  _private.setTransport(transport);
  try {
    const usage = await provider.fetch();
    assert.equal(usage.plan, 'Max 5x');
    assert.equal(usage.fiveHour.utilization, 0.12);

    const tokenCalls = transport.calls.filter((c) => !isUsageCall(c));
    assert.equal(tokenCalls.length, 1);
    assert.equal(tokenCalls[0].method, 'POST');
    const params = new URLSearchParams(tokenCalls[0].body);
    assert.equal(params.get('grant_type'), 'refresh_token');
    assert.equal(params.get('refresh_token'), 'old-refresh');
    assert.equal(params.get('client_id'), _private.refreshConfig().clientId);

    const usageCalls = transport.calls.filter(isUsageCall);
    assert.equal(usageCalls.length, 1);
    assert.equal(usageCalls[0].headers.Authorization, 'Bearer new-access');

    const saved = harness.read();
    assert.equal(saved.claudeAiOauth.accessToken, 'new-access');
    assert.equal(saved.claudeAiOauth.refreshToken, 'new-refresh');
    // CLI は scopes を配列としてしか読まない。
    assert.deepEqual(saved.claudeAiOauth.scopes, ['user:inference', 'user:profile']);
    // claudeAiOauth の内外を問わず、知らないフィールドは落とさない。
    assert.equal(saved.claudeAiOauth.unknownField, 'keep-me');
    assert.equal(saved.schemaVersion, 1);
  } finally {
    harness.restore();
  }
});

test('a 401 on usage retries with the CLI-refreshed credentials instead of posting', async () => {
  const harness = createHarness({ claudeAiOauth: oauthFixture({ accessToken: 'stale-access' }) });
  const transport = mockTransport((call) => {
    if (!isUsageCall(call)) return { status: 200, body: '{}' };
    if (call.headers.Authorization === 'Bearer stale-access') {
      // 401 を返す前に CLI が refresh を済ませた状況を作る。
      harness.write({
        claudeAiOauth: oauthFixture({ accessToken: 'cli-access', refreshToken: 'cli-refresh' }),
      });
      return { status: 401, body: '{"error":"invalid_token"}' };
    }
    return { status: 200, body: USAGE_BODY };
  });
  _private.setTransport(transport);
  try {
    const usage = await provider.fetch();
    assert.equal(usage.fiveHour.utilization, 0.12);
    assert.equal(transport.calls.filter((c) => !isUsageCall(c)).length, 0, 'must not POST');
    const usageCalls = transport.calls.filter(isUsageCall);
    assert.equal(usageCalls.length, 2);
    assert.equal(usageCalls[1].headers.Authorization, 'Bearer cli-access');
    // 自分では書かないので、CLI が書いた refresh token がそのまま残る。
    assert.equal(harness.read().claudeAiOauth.refreshToken, 'cli-refresh');
  } finally {
    harness.restore();
  }
});

test('a 401 on usage falls through to a direct refresh when the file has not changed', async () => {
  const harness = createHarness({ claudeAiOauth: oauthFixture({ accessToken: 'stale-access' }) });
  const transport = mockTransport((call) => {
    if (!isUsageCall(call)) {
      return { status: 200, body: JSON.stringify({ access_token: 'refreshed-access', expires_in: 28_800 }) };
    }
    return call.headers.Authorization === 'Bearer stale-access'
      ? { status: 401, body: '{"error":"invalid_token"}' }
      : { status: 200, body: USAGE_BODY };
  });
  _private.setTransport(transport);
  try {
    const usage = await provider.fetch();
    assert.equal(usage.fiveHour.utilization, 0.12);
    assert.equal(transport.calls.filter((c) => !isUsageCall(c)).length, 1);
    const usageCalls = transport.calls.filter(isUsageCall);
    assert.equal(usageCalls.length, 2);
    assert.equal(usageCalls[1].headers.Authorization, 'Bearer refreshed-access');
    const saved = harness.read();
    assert.equal(saved.claudeAiOauth.accessToken, 'refreshed-access');
    // 応答が refresh token を返さないときは既存の値を保つ。
    assert.equal(saved.claudeAiOauth.refreshToken, 'old-refresh');
  } finally {
    harness.restore();
  }
});

test('an invalid_grant from the token endpoint asks for a re-login and names the cause', async () => {
  const harness = createHarness({
    claudeAiOauth: oauthFixture({ expiresAt: Date.now() - 1_000 }),
  });
  const transport = mockTransport((call) => (isUsageCall(call)
    ? { status: 200, body: USAGE_BODY }
    : { status: 400, body: '{"error":"invalid_grant","error_description":"refresh token expired"}' }));
  _private.setTransport(transport);
  try {
    await assert.rejects(provider.fetch(), (err) => {
      assert.equal(err.code, 'claude_unauthorized');
      assert.match(err.message, /invalid_grant/);
      assert.match(err.message, /claude login/);
      return true;
    });
    assert.equal(transport.calls.filter(isUsageCall).length, 0);
    // 失敗した refresh でファイルを壊さない。
    assert.equal(harness.read().claudeAiOauth.accessToken, 'old-access');
  } finally {
    harness.restore();
  }
});

test('a 5xx from the token endpoint stays a transient error', async () => {
  const harness = createHarness({
    claudeAiOauth: oauthFixture({ expiresAt: Date.now() - 1_000 }),
  });
  const transport = mockTransport((call) => (isUsageCall(call)
    ? { status: 200, body: USAGE_BODY }
    : { status: 503, body: 'upstream unavailable' }));
  _private.setTransport(transport);
  try {
    // 一時障害を claude_unauthorized に変換すると、サーバ障害のたびに
    // ブラウザ再ログインが走ってしまう。
    await assert.rejects(provider.fetch(), (err) => {
      assert.equal(err.code, 'claude_http_error');
      assert.equal(err.status, 503);
      return true;
    });
    assert.equal(harness.read().claudeAiOauth.accessToken, 'old-access');
  } finally {
    harness.restore();
  }
});

test('a network failure on the token endpoint stays a transient error', async () => {
  const harness = createHarness({
    claudeAiOauth: oauthFixture({ expiresAt: Date.now() - 1_000 }),
  });
  const transport = mockTransport((call) => {
    if (isUsageCall(call)) return { status: 200, body: USAGE_BODY };
    throw Object.assign(new Error('getaddrinfo ENOTFOUND'), { code: 'ENOTFOUND' });
  });
  _private.setTransport(transport);
  try {
    await assert.rejects(provider.fetch(), (err) => {
      assert.equal(err.code, 'claude_network');
      return true;
    });
    assert.equal(harness.read().claudeAiOauth.accessToken, 'old-access');
  } finally {
    harness.restore();
  }
});

test('an expired refresh token asks for a re-login without posting', async () => {
  const harness = createHarness({
    claudeAiOauth: oauthFixture({
      expiresAt: Date.now() - 1_000,
      refreshTokenExpiresAt: Date.now() - 1_000,
    }),
  });
  const transport = mockTransport(() => ({ status: 200, body: USAGE_BODY }));
  _private.setTransport(transport);
  try {
    await assert.rejects(provider.fetch(), (err) => {
      assert.equal(err.code, 'claude_unauthorized');
      return true;
    });
    assert.equal(transport.calls.length, 0, 'must not touch the network');
    assert.equal(harness.read().claudeAiOauth.accessToken, 'old-access');
  } finally {
    harness.restore();
  }
});

test('a credentials update during the refresh POST wins over our own result', async () => {
  const harness = createHarness({
    claudeAiOauth: oauthFixture({ expiresAt: Date.now() - 1_000 }),
  });
  const transport = mockTransport((call) => {
    if (isUsageCall(call)) return { status: 200, body: USAGE_BODY };
    // 応答を返す前に CLI が refresh を終えた状況を作る。refresh token は回転
    // するので、後から書かれたこちらが有効な組み合わせになる。
    harness.write({
      cliOnlyField: 'written-by-cli',
      claudeAiOauth: oauthFixture({ accessToken: 'cli-access', refreshToken: 'cli-refresh' }),
    });
    return { status: 200, body: JSON.stringify({ access_token: 'mine-access', expires_in: 28_800 }) };
  });
  _private.setTransport(transport);
  try {
    const usage = await provider.fetch();
    assert.equal(usage.fiveHour.utilization, 0.12);
    const usageCalls = transport.calls.filter(isUsageCall);
    assert.equal(usageCalls.length, 1);
    assert.equal(usageCalls[0].headers.Authorization, 'Bearer cli-access');

    const saved = harness.read();
    assert.equal(saved.claudeAiOauth.accessToken, 'cli-access');
    assert.equal(saved.claudeAiOauth.refreshToken, 'cli-refresh');
    assert.equal(saved.cliOnlyField, 'written-by-cli');
  } finally {
    harness.restore();
  }
});
