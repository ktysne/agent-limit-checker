'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

// Only the pure normalizers are exercised here: load/save read the Electron
// userData path, which does not exist under `node --test`.
const { _private: { normalizeSettings, normalizeCodexAccountNames } } = require('../src/settings');

test('normalizeSettings defaults codexAccountNames to an empty map', () => {
  assert.deepEqual(normalizeSettings({}).codexAccountNames, {});
  assert.deepEqual(normalizeSettings(undefined).codexAccountNames, {});
});

test('normalizeSettings drops a codexAccountNames that is not an object', () => {
  for (const value of ['.codex', 42, null, true, ['.codex']]) {
    assert.deepEqual(normalizeSettings({ codexAccountNames: value }).codexAccountNames, {});
  }
});

test('normalizeSettings keeps only string entries and trims them', () => {
  const names = normalizeSettings({
    codexAccountNames: {
      '.codex': '  Codex Main  ',
      '  .codex-sub  ': 'Codex Sub',
      '.codex-number': 7,
      '.codex-object': { name: 'x' },
      '.codex-null': null,
    },
  }).codexAccountNames;

  assert.deepEqual(names, {
    '.codex': 'Codex Main',
    '.codex-sub': 'Codex Sub',
  });
});

test('normalizeSettings discards an empty name so the account keeps its default', () => {
  const names = normalizeSettings({
    codexAccountNames: { '.codex': '', '.codex-sub': '   ', '': 'nameless' },
  }).codexAccountNames;

  assert.deepEqual(names, {});
});

test('normalizeSettings truncates a display name to 40 characters', () => {
  const long = 'あ'.repeat(60);
  const names = normalizeSettings({ codexAccountNames: { '.codex': long } }).codexAccountNames;

  assert.equal(names['.codex'].length, 40);
  assert.equal(names['.codex'], 'あ'.repeat(40));
});

test('normalizeSettings leaves the other settings untouched', () => {
  const settings = normalizeSettings({
    pollingIntervalSec: 60,
    autoLaunch: true,
    ntfy: { topicUrl: ' https://ntfy.sh/topic ' },
    codexAccountNames: { '.codex': 'Codex Main' },
  });

  assert.equal(settings.pollingIntervalSec, 60);
  assert.equal(settings.autoLaunch, true);
  assert.equal(settings.ntfy.topicUrl, 'https://ntfy.sh/topic');
  assert.deepEqual(settings.codexAccountNames, { '.codex': 'Codex Main' });
});

test('normalizeCodexAccountNames never returns the input object itself', () => {
  const input = { '.codex': 'Codex Main' };
  const names = normalizeCodexAccountNames(input);

  assert.notEqual(names, input);
  assert.deepEqual(names, input);
});
