# 設計資料

Agent Limit Checker は、Claude Code と Codex の利用状況を取得する Windows のトレイ常駐アプリです。
UI と Windows 連携を WPF アプリに置き、プロバイダ、設定、通知、更新の処理を Core プロジェクトに分けています。

## Claude の利用状況

Claude の利用状況は `GET https://api.anthropic.com/api/oauth/usage` から取得します。
リクエストには Bearer token と `anthropic-beta: oauth-2025-04-20` を付け、HTTP リダイレクトは追いません。
資格情報は `CLAUDE_CONFIG_DIR` があればそのフォルダーの `.credentials.json`、なければユーザーの `.claude/.credentials.json` から読みます。

`expiresAt` が現在時刻に達した場合だけ refresh token を使って OAuth を更新します。
期限前に更新すると、CLI が保持する回転済み refresh token を無効にするおそれがあるため、前倒しでは更新しません。
HTTP 401 を受けた場合は資格情報ファイルを読み直し、CLI が更新した access token があればその値で一度再試行します。
ファイルへ更新を書き戻す直前にも読み直し、別プロセスが先に access token を更新していた場合はファイルを上書きしません。
更新時は既存 JSON の未知の値を保ち、一時ファイルから置き換えます。

OAuth の token endpoint と client ID は CLI と同じ既定値を使い、`CLAUDE_OAUTH_TOKEN_ENDPOINT` と `CLAUDE_OAUTH_CLIENT_ID` で上書きできます。
refresh token の期限切れや OAuth endpoint の拒否は再ログインを求めます。
通信障害や HTTP 429、5xx は再ログインの要求に変換しません。

5 時間枠は `five_hour`、週次枠は `seven_day` から読みます。
`utilization` があれば `resets_at` が `null` でも利用枠を保持し、画面ではウィンドウ未開始として表示します。
週次スコープ情報は `limits` の weekly-scoped 項目から読み、該当項目が無い場合は `seven_day_sonnet` を使います。
`spend.balance` の `amount_minor` と `exponent` から金額を計算し、正の残高だけを通貨付きで表示します。
`iguana_necktie` に有効な上限と使用額または残額があれば、クラウドセッション用クレジットとして表示します。

## Codex の利用状況

Codex の各ホームでは `codex app-server` を起動し、標準入出力の JSON-RPC で通信します。
起動後に `initialize` を要求し、`initialized` 通知を送り、`account/rateLimits/read` で利用状況を取得します。

ホーム探索ではユーザーのホーム直下にある `.codex` で始まるディレクトリと `CODEX_HOME` を候補にします。
`auth.json` または `config.toml` がある候補だけを残し、正規化した絶対パスで重複を除きます。
既定ホームは `CODEX_HOME`、未設定なら `~/.codex` です。
ホームごとに app-server を 1 つ持ち、子プロセスの `CODEX_HOME` をそのホームに固定します。

Codex の実行ファイルが npm の `.ps1` shim の場合は、shim 経由で標準入力を渡すと app-server の初期化が停止するため、`node` と `node_modules/@openai/codex/bin/codex.js` を直接起動します。
`.cmd` と `.bat` は `cmd.exe` 経由で起動します。
app-server の子プロセスは Windows Job Object に割り当て、所有プロセスの終了時に一緒に終了させます。
プロセスを一時停止状態で Job Object に割り当ててから実行するため、その前に生成した子プロセスが後始末から漏れません。

5 時間枠と週次枠は `windowDurationMins` の 300 と 10080 に対応します。
`usedPercent` は 100 で割り、`resetsAt` は Unix epoch 秒からミリ秒へ変換します。
利用可能クレジットは `rateLimits` を優先し、無い場合は limit ID 順にプロファイルを調べます。
Codex の残高は通貨額ではなくクレジット数として扱い、`unlimited` は残高と区別します。
リセット権は `rateLimitResetCredits.availableCount` と利用可能な項目の最短 `expiresAt` から表示します。

## 取得周期とトレイ

起動後に利用状況を取得し、設定した周期で再取得します。
既定の周期は 5 分で、30 秒、1 分、2 分、5 分、10 分から選べます。
Claude と Codex の取得は UI スレッド外で非同期に行い、画面とトレイの更新は WPF の Dispatcher に渡します。
取得中に再度更新を要求した場合は、進行中の取得を待ち、新しい取得を重ねて開始しません。

トレイアイコンは利用率を円形のメーターで示し、Codex の複数アカウントでは 5 時間枠の使用率が最も高いアカウントを選びます。
ツールチップとメニューにはサービスの利用率とエラー状態を表示します。
Claude の認証エラーが続く場合は、ポップオーバー表示時にログインを促します。

## ポップオーバー

ポップオーバーの幅は 360 WPF 論理単位で、高さは内容に合わせます。
最大高さは 900 論理単位で、画面の作業領域に合わせて制限します。
トレイアイコンの中央に合わせて配置し、上側に収まらない場合は下側へ移し、最後に作業領域内へ収めます。
ディスプレイごとの物理ピクセルと論理単位を DPI で換算し、DPI が変わったときは位置を計算し直します。

アプリケーションマニフェストは Per-Monitor V2 を宣言し、ポップオーバーは `Ideal` の文字整形と ClearType を使います。
レイアウトの端数を丸めて、150% の表示倍率でも文字やメーターがずれにくくします。
通常の文字には `Segoe UI`、`Yu Gothic UI`、`Meiryo` を使います。
アイコンボタンの `⚙` は既定のフォント代替で大きな Emoji に変わるため、`Segoe UI Symbol` を個別に指定します。

## 設定

設定ファイルは `%APPDATA%\agent-limit-checker\settings.json` です。
3.x と同じ JSON を読み、未知のプロパティを保持して部分更新します。
ntfy と Codex 表示名も部分更新し、ほかの保存済みキーを消しません。
書き込みは同じフォルダーに一時ファイルを作成してから置き換えます。
ログは `%APPDATA%\agent-limit-checker\logs\agent-limit-checker.log` に記録します。

## 自動起動

自動起動は現在のユーザーの `Software\Microsoft\Windows\CurrentVersion\Run` に登録します。
Run 値の名前は `com.agent-limit-checker.app` で、値は現在の exe のパスと `--hidden` 引数です。
Windows の `StartupApproved\Run` に無効状態がある場合は、自動起動の設定表示にも反映します。
利用者が自動起動を有効にした場合は無効状態の記録を削除してから Run 値を書きます。
Release 版は設定が有効なら起動時に Run 値のパスを現在の exe へ更新し、Windows 側の無効状態は残します。
Debug 版は Run 値を変更しません。

## 自動アップデート

既定の manifest は `https://ktysne.info/agent-limit-checker/update-v2.json` で、schema 2 の `latest` に版、zip URL、SHA-256、公開日を含みます。
設定で有効なら起動時と 24 時間ごとに確認し、設定画面から手動でも確認できます。
開発用の `AGENT_LIMIT_CHECKER_UPDATE_MANIFEST_URL` は localhost の URL だけを採用します。

配布 zip の URL は HTTPS の許可先に限定します。
GitHub Release の zip は対象版に対応する正確なパスであることを確認し、ダウンロードのリダイレクトは HTTPS の `github.com` または `*.githubusercontent.com` のホストに最大 5 回まで追従します。
ダウンロードした zip は SHA-256 を照合してから検査します。
zip は相対パスを含まないファイル名だけを許し、重複名、予約デバイス名、上限を超えるエントリーを拒否し、直下に `AgentLimitChecker.exe` があることを確認します。

自動適用は、配布用の単一 exe から起動し、インストール先へ書き込める場合に限ります。
一時フォルダーからの起動や書き込み不可などで適用できない場合は配布ページを開きます。
更新作業は `%LOCALAPPDATA%\agent-limit-checker\update` に準備します。
展開した新しい exe を適用役として起動し、旧版の終了を待ってからファイルを置き換えます。
適用後は新しい exe の起動通知または 15 秒間の稼働を確認し、確認できなければ退避ファイルから元へ戻します。
成功した更新の退避ファイルと作業フォルダーは次回起動時に後始末します。

## ntfy 通知

通知先には Topic URL と任意の Access token を設定します。
送信時は Topic URL へ HTTP POST し、Access token があれば Bearer 認証を付けます。
通知対象は 5 時間枠のリセット、週次枠のリセット、Codex リセット権の期限です。
リセット権は最も早い期限の 5 時間前に通知し、その時点を過ぎて期限前であればすぐに通知します。
通知は設定で個別に有効化でき、同じリセットまたは期限を重複して送りません。
