'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { buildLoginPsCommand, buildSilentPsCommand } = require('../src/loginCommand');

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

test('buildLoginPsCommand sets CODEX_HOME before invoking the CLI', () => {
  const cmd = buildLoginPsCommand('codex.exe', ['login'], {
    env: { CODEX_HOME: 'C:/Users/me/.codex-review' },
  });
  assert.ok(cmd.startsWith("$env:CODEX_HOME='C:/Users/me/.codex-review'; & 'codex.exe' 'login';"));
  assert.ok(!cmd.includes('"'), 'command must contain no double quotes');
  assert.equal(cmd.split(/\r?\n/).length, 1);
});

test('buildLoginPsCommand escapes single quotes inside an env value', () => {
  const cmd = buildLoginPsCommand('codex.exe', ['login'], {
    env: { CODEX_HOME: "C:/o'brien/.codex" },
  });
  assert.ok(cmd.startsWith("$env:CODEX_HOME='C:/o''brien/.codex'; "));
  assert.ok(!cmd.includes('"'), 'command must contain no double quotes');
});

test('buildLoginPsCommand emits no env prefix without env options', () => {
  const cmd = buildLoginPsCommand('codex.exe', ['login']);
  assert.ok(cmd.startsWith("& 'codex.exe' 'login';"));
  assert.ok(!cmd.includes('$env:'));
  // An empty value is not an environment override worth emitting.
  assert.ok(!buildLoginPsCommand('codex.exe', ['login'], { env: { CODEX_HOME: '' } }).includes('$env:'));
});

test('buildLoginPsCommand drops env names that are not plain identifiers', () => {
  // A name cannot be quoted in `$env:<name>`, so anything else would inject
  // PowerShell syntax rather than set a variable.
  const cmd = buildLoginPsCommand('codex.exe', ['login'], {
    env: { "BAD; Remove-Item 'x": 'value', CODEX_HOME: 'C:/home/.codex' },
  });
  assert.ok(!cmd.includes('Remove-Item'));
  assert.ok(cmd.startsWith("$env:CODEX_HOME='C:/home/.codex'; "));
});

test('buildSilentPsCommand returns only the call-operator invocation (no window handling)', () => {
  const cmd = buildSilentPsCommand('claude', ['auth', 'login']);
  assert.equal(cmd, "& 'claude' 'auth' 'login'");
  // No success/failure window handling — it is purely the invocation.
  assert.ok(!cmd.includes('Read-Host'));
  assert.ok(!cmd.includes('LASTEXITCODE'));
});

test('buildSilentPsCommand single-quotes a path with spaces and never emits a double quote', () => {
  const cmd = buildSilentPsCommand('C:/path with spaces/claude.exe', ['auth', 'login']);
  assert.ok(!cmd.includes('"'), 'command must contain no double quotes');
  assert.equal(cmd, "& 'C:/path with spaces/claude.exe' 'auth' 'login'");
});

test('buildSilentPsCommand escapes single quotes inside the path by doubling them', () => {
  const cmd = buildSilentPsCommand("C:/o'brien/codex.exe", ['login']);
  assert.equal(cmd, "& 'C:/o''brien/codex.exe' 'login'");
});
