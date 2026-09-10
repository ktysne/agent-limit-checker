'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { _private } = require('../src/claudeProvider');

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
