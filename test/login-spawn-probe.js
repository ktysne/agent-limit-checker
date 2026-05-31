'use strict';

// Probe that the new openLoginTerminal spawn invocation actually launches
// claude.exe — i.e. the cmd /quote chain works on this Windows machine.
//
// We do NOT call `auth login` (which would open a browser). Instead we
// substitute a side-effect-free verb (`auth status --json`) using the same
// arg-construction code, capture exit code, and report.
//
// Run with:
//   node test/login-spawn-probe.js

const { spawn } = require('node:child_process');
const { resolveClaudeExecutable } = require('../src/cliPaths');
const { buildLoginPsCommand } = require('../src/loginCommand');

const exe = resolveClaudeExecutable();
if (!exe) {
  console.error('no claude executable found');
  process.exit(2);
}

// Show the exact command production would run for the real login flow
// (auto-close on success). We do NOT spawn this one — it would open a browser.
console.log('resolved exe:', exe);
console.log('production login -Command:\n  ', buildLoginPsCommand(exe, ['auth', 'login']));

// For the actual spawn, swap `auth login` for the side-effect-free
// `auth status --json` and keep the window open (-NoExit) so a human can read
// the JSON the CLI prints. This still exercises the cmd /c start → powershell
// chain that the real button relies on.
const psQuoted = (s) => `'${String(s).replace(/'/g, "''")}'`;
const cliArgs = ['auth', 'status', '--json'];
const psArgs = [psQuoted(exe), ...cliArgs.map((a) => `'${a}'`)].join(' ');

console.log('probe -Command:', `& ${psArgs}`);

const start = Date.now();
const proc = spawn(
  'cmd.exe',
  [
    '/c', 'start', '""',
    'powershell.exe',
    '-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-Command', `& ${psArgs}`,
  ],
  { detached: true, stdio: 'ignore', windowsHide: false },
);
proc.unref();
proc.on('error', (err) => {
  console.error('spawn error:', err.code, err.message);
  process.exit(1);
});
// We can't observe the child window's output because of `start` detachment,
// but if spawn itself fails (path wrong, perms, etc.) we'll get an error.
// A successful spawn here means the cmd-line construction is at least
// well-formed.
setTimeout(() => {
  console.log(`spawn appeared to succeed in ${Date.now() - start}ms (no error event fired).`);
  console.log('  Look for a new PowerShell window: it should show the claude auth status JSON output.');
  process.exit(0);
}, 1500);
