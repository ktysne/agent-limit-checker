'use strict';

const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const assert = require('node:assert/strict');

const { resolveExecutableFromPath } = require('../src/cliPaths');

function withTempDir(fn) {
  const root = fs.mkdtempSync(path.join(process.cwd(), '.tmp-cli-paths-'));
  try {
    return fn(root);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

test('resolveExecutableFromPath returns the first candidate in priority order', () => {
  withTempDir((dir) => {
    const ps1 = path.join(dir, 'codex.ps1');
    const exe = path.join(dir, 'codex.exe');
    fs.writeFileSync(ps1, '', 'utf8');
    fs.writeFileSync(exe, '', 'utf8');

    const resolved = resolveExecutableFromPath(['codex.exe', 'codex.ps1'], { PATH: dir });
    assert.equal(resolved, exe);
  });
});

test('resolveExecutableFromPath searches later PATH directories only when needed', () => {
  withTempDir((dirA) => withTempDir((dirB) => {
    const target = path.join(dirB, 'claude.cmd');
    fs.writeFileSync(target, '', 'utf8');

    const resolved = resolveExecutableFromPath(['claude.exe', 'claude.cmd'], {
      PATH: [dirA, dirB].join(path.delimiter),
    });
    assert.equal(resolved, target);
  }));
});
