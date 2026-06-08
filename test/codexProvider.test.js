'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

const codexProvider = require('../src/codexProvider');
const { _private } = codexProvider;

test('extractPlanLabel reads planType from the top-level rateLimits block', () => {
  assert.equal(
    _private.extractPlanLabel({
      rateLimits: { planType: 'plus' },
    }),
    'Plus',
  );
  assert.equal(
    _private.extractPlanLabel({
      rateLimits: { planType: 'pro' },
    }),
    'Pro',
  );
});

test('extractPlanLabel falls back to rateLimitsByLimitId when top-level is absent', () => {
  // Sorted key iteration: "alpha" before "beta", so we pick alpha's planType.
  assert.equal(
    _private.extractPlanLabel({
      rateLimitsByLimitId: {
        beta: { planType: 'pro' },
        alpha: { planType: 'team' },
      },
    }),
    'Team',
  );
});

test('extractPlanLabel returns null when no planType anywhere', () => {
  assert.equal(_private.extractPlanLabel(null), null);
  assert.equal(_private.extractPlanLabel({}), null);
  assert.equal(
    _private.extractPlanLabel({ rateLimits: { /* no planType */ } }),
    null,
  );
});

test('codex auth file path honors CODEX_HOME', () => {
  const previous = process.env.CODEX_HOME;
  const codexHome = path.join('tmp', 'custom-codex-home');
  try {
    process.env.CODEX_HOME = codexHome;
    assert.equal(_private.codexAuthFile(), path.join(codexHome, 'auth.json'));
    assert.equal(codexProvider.authFilePath(), path.join(codexHome, 'auth.json'));
  } finally {
    if (previous == null) {
      delete process.env.CODEX_HOME;
    } else {
      process.env.CODEX_HOME = previous;
    }
  }
});

test('isRestartableError triggers on a crashed/exited app-server', () => {
  // The process died (exit code null shows up as "exit null"); a respawn fixes it.
  assert.equal(
    _private.isRestartableError({ code: 'codex_process_exited', message: 'codex app-server が終了しました (exit null)' }),
    true,
  );
});

test('isRestartableError triggers on auth-class RPC errors (rotated token)', () => {
  // The real failure from the field: another codex process rotated the shared
  // OAuth token, invalidating the one our app-server cached.
  const msg =
    'Codex RPC エラー: failed to fetch codex rate limits: GET '
    + 'https://chatgpt.com/backend-api/wham/usage failed: 401 Unauthorized; '
    + '{ "error": { "message": "Your authentication token has been invalidated. '
    + 'Please try signing in again.", "code": "token_invalidated", "status": 401 } }';
  assert.equal(_private.isRestartableError({ code: 'codex_rpc_error', message: msg }), true);
  assert.equal(
    _private.isRestartableError({ code: 'codex_rpc_error', message: 'unauthorized' }),
    true,
  );
  assert.equal(
    _private.isRestartableError({ code: 'codex_rpc_error', message: 'not signed in' }),
    true,
  );
  assert.equal(
    _private.isRestartableError({ code: 'codex_rpc_error', message: 'redacted', restartable: true }),
    true,
  );
});

test('isRestartableError leaves non-auth RPC errors alone (no pointless respawn)', () => {
  // A malformed response is a different problem — respawning would just loop.
  assert.equal(
    _private.isRestartableError({ code: 'codex_rpc_error', message: 'account/rateLimits/read のレスポンスに result がありません' }),
    false,
  );
  assert.equal(
    _private.isRestartableError({ code: 'codex_timeout', message: 'RPC account/rateLimits/read がタイムアウトしました' }),
    false,
  );
  assert.equal(_private.isRestartableError(null), false);
});

test('makeCodexRpcError redacts raw auth response details but preserves restartability', () => {
  const err = _private.makeCodexRpcError({
    message:
      'failed to fetch codex rate limits: GET '
      + 'https://chatgpt.com/backend-api/wham/usage failed: 401 Unauthorized; '
      + '{ "access_token": "redacted-token", "error": { "code": "token_invalidated" } }',
  });

  assert.equal(err.code, 'codex_rpc_error');
  assert.equal(err.restartable, true);
  assert.equal(_private.isRestartableError(err), true);
  assert.equal(err.message, 'Codex の認証が失効しています。ログインし直してください。');
  assert.doesNotMatch(err.message, /chatgpt|backend-api|access_token|redacted-token|401/);
});

test('makeCodexRpcError detects auth details nested outside message', () => {
  const err = _private.makeCodexRpcError({
    message: 'request failed',
    data: { status: 401, error: { code: 'token_invalidated' } },
  });

  assert.equal(err.restartable, true);
  assert.equal(_private.isRestartableError(err), true);
  assert.equal(err.message, 'Codex の認証が失効しています。ログインし直してください。');
});

test('makeCodexRpcError redacts non-auth JSON-RPC error details', () => {
  const err = _private.makeCodexRpcError({
    message: 'unexpected upstream payload',
    data: { responseBody: '{"access_token":"redacted-token"}' },
  });

  assert.equal(err.code, 'codex_rpc_error');
  assert.equal(err.restartable, false);
  assert.equal(err.message, 'Codex RPC エラー: rate limit の取得に失敗しました');
  assert.doesNotMatch(err.message, /unexpected upstream|responseBody|access_token|redacted-token/);
});
