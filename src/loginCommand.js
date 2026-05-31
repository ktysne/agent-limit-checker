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
// `$LASTEXITCODE` is null when the invoked program never sets an exit code
// (some shims); we treat null as success so a clean login still auto-closes.
function buildLoginPsCommand(exe, cliArgs) {
  const sq = (s) => `'${String(s).replace(/'/g, "''")}'`;
  const invoke = ['&', sq(exe), ...cliArgs.map(sq)].join(' ');
  const okMsg = "'ログインが完了しました。このウィンドウは自動的に閉じます…'";
  const ngMsg = "('ログインに失敗しました (exit ' + $c + ')。Enter キーでこのウィンドウを閉じます。')";
  return (
    `${invoke}; $c=$LASTEXITCODE; `
    + 'if ($c -eq 0 -or $null -eq $c) { '
    + `Write-Host ''; Write-Host ${okMsg} -ForegroundColor Green; Start-Sleep -Seconds 2 `
    + '} else { '
    + `Write-Host ''; Write-Host ${ngMsg} -ForegroundColor Yellow; [void](Read-Host) `
    + '}'
  );
}

module.exports = { buildLoginPsCommand };
