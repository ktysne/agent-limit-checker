'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { buildLoginPsCommand } = require('../src/loginCommand');

test('buildLoginPsCommand stays a single line so it survives the cmd /c start re-parse', () => {
  const cmd = buildLoginPsCommand('C:/Users/me/AppData/Roaming/npm/claude.ps1', ['auth', 'login']);
  assert.equal(cmd.split(/\r?\n/).length, 1);
});

test('buildLoginPsCommand never emits a double quote (cmd would mangle it)', () => {
  const cmd = buildLoginPsCommand('C:/path with spaces/claude.exe', ['auth', 'login']);
  assert.ok(!cmd.includes('"'), 'command must contain no double quotes');
});

test('buildLoginPsCommand single-quotes the executable and every argument', () => {
  const cmd = buildLoginPsCommand('C:/path with spaces/claude.exe', ['auth', 'login']);
  assert.ok(cmd.startsWith("& 'C:/path with spaces/claude.exe' 'auth' 'login';"));
});

test('buildLoginPsCommand escapes single quotes inside the path by doubling them', () => {
  const cmd = buildLoginPsCommand("C:/o'brien/codex.exe", ['login']);
  // A literal single quote inside a PowerShell single-quoted string is '' .
  assert.ok(cmd.includes("& 'C:/o''brien/codex.exe' 'login';"));
});

test('buildLoginPsCommand auto-closes on success and waits (Read-Host) on failure', () => {
  const cmd = buildLoginPsCommand('claude', ['auth', 'login']);
  // Success path: exit 0 OR a null exit code (some shims never set one).
  assert.ok(cmd.includes('if ($c -eq 0 -or $null -eq $c) {'));
  assert.ok(cmd.includes('Start-Sleep -Seconds 2'));
  // Failure path keeps the window open so the error stays readable.
  assert.ok(cmd.includes('} else {'));
  assert.ok(cmd.includes('Read-Host'));
  // No `;` between `}` and `else` — PowerShell rejects that.
  assert.ok(!/}\s*;\s*else/.test(cmd));
});
