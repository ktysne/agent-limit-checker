'use strict';

// Build the single-line PowerShell program that runs an interactive CLI login
// and then *auto-closes its own window on success* — so the user no longer has
// to find the spawned terminal and close it by hand. On failure the window is
// kept open (Read-Host) so the error message stays readable.
//
// Design constraints:
//   * The result is handed to `cmd.exe ... start "" powershell -Command <here>`.
//     cmd re-parses the line, so the command MUST contain no double quotes —
//     every string is single-quoted (PowerShell single quotes are literal,
//     which also makes a path with spaces or quotes impossible to mis-read as
//     a command name; the old `cmd /k` chain tripped over exactly that).
//   * It is one physical line: statements are separated by `;`, never by
//     newlines, because newlines do not survive the cmd command line. Note the
//     absence of a `;` between `}` and `else` — PowerShell forbids it there.
//   * `& 'path' arg...` (the call operator) launches .exe / .ps1 / .cmd / .bat
//     alike, so a single code path covers every shape resolveClaudeExecutable
//     / resolveCodexExecutable can return.
//
const sq = (s) => `'${String(s).replace(/'/g, "''")}'`;

// PowerShell variable names are not quotable, so only a plain identifier can
// safely be interpolated into `$env:<name>`. Anything else is dropped rather
// than risking an injected statement.
const ENV_NAME_RE = /^[A-Za-z_][A-Za-z0-9_]*$/;

// `$env:NAME='value'; ` assignments prepended to the command, used to run the
// login against a specific Codex home (CODEX_HOME). Values are single-quoted
// with the same doubling escape as every other string here, so the no-double-
// quote / single-line constraints above still hold.
function buildEnvPrefix(options) {
  const env = options && options.env;
  if (!env || typeof env !== 'object') return '';
  let prefix = '';
  for (const name of Object.keys(env)) {
    const value = env[name];
    if (value == null || value === '') continue;
    if (!ENV_NAME_RE.test(name)) continue;
    prefix += `$env:${name}=${sq(value)}; `;
  }
  return prefix;
}

// `$LASTEXITCODE` is null when the invoked program never sets an exit code
// (some shims); we treat null as success so a clean login still auto-closes.
function buildLoginPsCommand(exe, cliArgs, options) {
  const envPrefix = buildEnvPrefix(options);
  const invoke = ['&', sq(exe), ...cliArgs.map(sq)].join(' ');
  const okMsg = "'ログインが完了しました。このウィンドウは自動的に閉じます…'";
  const ngMsg = "('ログインに失敗しました (exit ' + $c + ')。Enter キーでこのウィンドウを閉じます。')";
  return (
    `${envPrefix}${invoke}; $c=$LASTEXITCODE; `
    + 'if ($c -eq 0 -or $null -eq $c) { '
    + `Write-Host ''; Write-Host ${okMsg} -ForegroundColor Green; Start-Sleep -Seconds 2 `
    + '} else { '
    + `Write-Host ''; Write-Host ${ngMsg} -ForegroundColor Yellow; [void](Read-Host) `
    + '}'
  );
}

// Build only the `& 'exe' 'arg'...` call-operator invocation, with the same
// single-quote escaping as buildLoginPsCommand. Used for the silent (hidden
// window) background login path, which has no success/failure window handling —
// it just fires the OAuth flow and lets watchForLoginCompletion pick it up.
// Note: this path spawns powershell.exe directly with an argv array, so the
// "no double quotes" constraint of the `cmd /c start` re-parse does not apply
// here — the single-quote style is kept purely for consistency with the
// visible login path.
function buildSilentPsCommand(exe, cliArgs) {
  return ['&', sq(exe), ...cliArgs.map(sq)].join(' ');
}

module.exports = { buildLoginPsCommand, buildSilentPsCommand };
