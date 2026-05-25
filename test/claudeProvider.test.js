'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { _private } = require('../src/claudeProvider');

test('normalizes Claude OAuth expiry timestamps', () => {
  assert.equal(_private.normalizeExpiresAt(1_779_632_820), 1_779_632_820_000);
  assert.equal(_private.normalizeExpiresAt(1_779_632_820_450), 1_779_632_820_450);
  assert.equal(_private.normalizeExpiresAt('bad'), null);
});

test('detects token refresh window from expiresAt', () => {
  const now = 1_779_600_000_000;
  assert.equal(_private.shouldRefresh({ expiresAt: now + 30_000 }, now), true);
  assert.equal(_private.shouldRefresh({ expiresAt: now + 120_000 }, now), false);
  assert.equal(_private.shouldRefresh({}, now), false);
});

test('buildChildEnv does NOT leak ANTHROPIC_API_KEY to the spawned Claude CLI', () => {
  // If we passed ANTHROPIC_API_KEY through, the CLI would prefer it over the
  // OAuth flow we're trying to refresh — which would silently break the
  // refresh nudge. Allowlist must drop it.
  const env = _private.buildChildEnv({
    ANTHROPIC_API_KEY: 'sk-leaked',
    OPENAI_API_KEY: 'sk-also-leaked',
    PATH: 'C:\\Windows\\System32',
    USERPROFILE: 'C:\\Users\\u',
    HOME: '/home/u',
  });
  assert.equal(env.ANTHROPIC_API_KEY, undefined);
  assert.equal(env.OPENAI_API_KEY, undefined);
  assert.equal(env.PATH, 'C:\\Windows\\System32');
  assert.equal(env.USERPROFILE, 'C:\\Users\\u');
  assert.equal(env.HOME, '/home/u');
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
