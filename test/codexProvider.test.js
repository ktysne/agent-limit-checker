'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { _private } = require('../src/codexProvider');

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
