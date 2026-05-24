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
