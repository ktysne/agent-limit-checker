# C# + WPF 移行の計画(2026-10-10)

Electron 版(main.js、`src/`、`renderer/`、合計約 4,700 行)を C# + WPF のネイティブアプリに置き換える計画である。
あわせて、開発者の他のネイティブアプリ(agent-gc、screen-recorder、rec-studio)と同じ配布の仕組みを入れる。
対象は、アプリ個別のページ、自動アップデート、ドキュメント、`build-*.bat` による発行である。
起点のコミットは `3c72273`(v3.7.0)。
対象は Windows だけで、macOS と Linux への対応は外す(開発者の指定)。
アプリの機能の追加と見た目の刷新は対象から外し、今の機能と挙動をそのまま移す。
移行と改善を同じ差分に混ぜると、回帰の原因を切り分けられないためである。
次の 2 つも対象から外す。
- アプリアイコンの作成:開発者が行う(2026-10-10 に `assets/icon.png` と `assets/icon-transparent.png` を配置済み)。
- ktysne.info のトップページで、上部のエフェクターの UI から個別ページへリンクすること:4.0.0 の発行の後に対応する。[ktysne/ktysne-top#20](https://github.com/ktysne/ktysne-top/issues/20) で追う。

## 現在地と次にやること

- 統合ブランチ `feature/csharp-wpf` を作成済みで、各段の PR を「対応順序、ブランチ、PR」の表のとおりスタック式に積んでいる。どこまで進んだかは表の「状態」の列を見る。
- 次のセッションは、表で「未着手」の最初の段から着手する。その段のブランチは、1 段下のブランチの先頭から切る。

## 決定済みの判断

| 項目 | 決定 | 理由 |
|---|---|---|
| 言語と UI | C# + WPF(2026-10-10 開発者が決定) | `SizeToContent` と論理単位の配置で、内容に合わせた高さの調整と端数の表示倍率の問題を素直に解ける。`Process`、`HttpClient`、`System.Text.Json` が標準にあり、`codex app-server` の常駐を少ないコードで書ける |
| 対象 OS | Windows のみ(2026-10-10 開発者が指定) | 利用者が Windows だけである |
| ブランチの運用 | 統合ブランチ `feature/csharp-wpf` で開発し、動作の安定を確かめてから main へマージする(2026-10-10 開発者が指定) | 移行の途中の状態を main に出さず、Electron 版のリリースを止めないため |
| 段ごとの PR | 各段の PR は統合ブランチへ向けてスタック式に積む。段の間とマージは merge commit でつなぐ | 段ごとにレビューできる大きさに保ち、下から順にマージできるようにするため(/stacked-pr) |
| 移す順序 | 危険の大きい `codex app-server` の通信を UI より先に作る | 技術的に成り立たない場合に、UI を作り込む前に気付けるようにするため |
| 互換 | 設定ファイル `%APPDATA%\agent-limit-checker\settings.json` の場所と形式、自動起動の Run キーの値の名前を今と同じにする | 利用者の設定と自動起動の登録を、移行後もそのまま引き継ぐため |
| テストの移植 | `test/*.test.js` の各ケースを xUnit へ同じ意味で移す | 今のテストは過去の不具合の再発防止を含むため、ケースを落とすと回帰を検出できなくなる |
| C# のコードの置き場所 | `dotnet/` に置き、移行後もそのままにする(2026-10-10 開発者が決定) | 移行の途中で Electron 版と並べて置け、最後に場所を移す差分が要らない |
| 配布の形 | .NET ランタイムを同梱した単一の exe を zip にする(2026-10-10 開発者が決定) | 利用者に .NET ランタイムの導入を求めず、インストールなしで動かせる |
| `package.json` の扱い | ai-cross-review の `npm run review:*` と `sync`、発行スクリプトの `release:*` のために残し、Electron の依存を消す(2026-10-10 開発者が決定) | レビューの手順と基盤の同期を変えずに済む |
| 配布の仕組みの手本 | agent-gc に合わせる(2026-10-10 開発者が他のネイティブアプリと同じ仕組みを指定) | 3 つのうち agent-gc だけが C# + WPF で、単一 exe の zip という配布の形も同じである。更新の処理をテストできる層に分けてあり、設計資料に仕様がまとまっている |
| バージョンの正本と発行 | `.csproj` の `<Version Condition="'$(Version)' == ''">` を既定値とし、`build-release.bat` と `build-package.bat` で入力した版を `-p:Version=` で渡す。タグ `vX.Y.Z` は `build-package.bat` が付ける。`npm run release:minor` と `release:major` は廃止する | 他のネイティブアプリと発行の手順をそろえるため。版を入力して発行する方式なので、版を上げるだけのコミットが要らない |
| 移行後の最初の版 | 4.0.0 | CLAUDE.md の「大きな機能変更」に当たる |
| Release の置き場 | ソースのリポジトリ `ktysne/agent-limit-checker` の Releases(2026-10-10 開発者が決定) | 公開リポジトリなので、配信専用のリポジトリを増やさずに済む。発行の順(送信の後にタグを push する)は agent-gc に合わせる |
| 配布ページのパス | `https://ktysne.info/agent-limit-checker/`(2026-10-10 開発者が決定) | リポジトリ名と同じにし、他のアプリ(`/agent-gc/` など)と命名をそろえる |
| Electron 版の利用者への案内 | README と最後の v3.x の Release に、配布ページから 4.0.0 を入れ直す案内を書く(2026-10-10 開発者が決定) | v3.x には更新の仕組みが無く、自動では 4.0.0 に上がらないため。README は P11、Release の案内は 4.0.0 の発行時に書く |
| 自動アップデート | agent-gc と同じ自前の仕組みを移す。`https://ktysne.info/agent-limit-checker/update-v2.json` を読み、zip の SHA-256 を照合し、適用役のプロセスが置き換えて起動を確かめ、失敗したら元へ戻す | 他のアプリと manifest の形式と配信先をそろえ、ライブラリへの依存を増やさないため |

## 構成の方針

- 置き場所は、リポジトリ直下の `dotnet/` にする。agent-gc にならい、テストできる処理と UI を分ける。
  - `dotnet/AgentLimitChecker.Core/`:プロバイダ、設定、通知、更新の処理。UI に依存しない
  - `dotnet/AgentLimitChecker.App/`:WPF アプリ本体。トレイ、ポップオーバー、プロセスの起動
  - `dotnet/AgentLimitChecker.Tests/`:xUnit のテスト
  - ソリューションのファイルはリポジトリ直下に置く
- 配布ページの雛形は `site/` に、発行スクリプトは `tools/release-site.js` に、発行のバッチはリポジトリ直下に置く。
- 移行の途中は Electron 版のファイルを残し、P10 で消す。途中の段で Electron 版との挙動を見比べられるようにするためである。
- 対象のフレームワークは .NET 10 の `net10.0-windows` にする(agent-gc と同じ)。
- 非同期処理は `async`/`await` で書き、UI への反映は `Dispatcher` を経る。取得の処理は UI スレッドを止めない。
- exe の名前は `AgentLimitChecker.exe`、zip の名前は `AgentLimitChecker-X.Y.Z-win-x64.zip` にする。zip の中身は agent-gc と同じく exe、`manual.html`、`license.html` の 3 つにする。

### 表示倍率とフォントの要件

開発者は 4K のディスプレイを 150% の表示倍率で使っている(作業領域は約 2560×1400 の論理ピクセル)。
表示倍率の不具合とフォントの見た目は、移行で崩れやすいので、各段で次を守る(2026-10-10 開発者の指定)。

- DPI の扱い
  - `app.manifest` で Per-Monitor V2 を宣言する(`dpiAwareness` に `PerMonitorV2`)。WinForms の部品(`NotifyIcon` とそのメニュー)も同じプロセスなので、この宣言に従う。
  - Win32 の API(`Shell_NotifyIconGetRect`、`GetMonitorInfo`)は物理ピクセルを返し、WPF の `Left`、`Top`、`Width`、`Height` は論理単位(1/96 インチ)である。位置は、表示先のモニターの DPI で換算する。モニターごとの DPI は `GetDpiForMonitor` で得る。換算を混ぜると、150% でポップオーバーの位置と大きさが 1.5 倍ずれる。
  - 倍率の違うモニターへ移ったときは `DpiChanged` を受けて、位置と大きさを決め直す。
  - 細い線と枠がぼけないよう、ルートで `UseLayoutRounding="True"` を有効にする。
  - トレイのアイコンは、`SystemInformation.SmallIconSize`(150% では 24px)の大きさの画像を渡す。16px の画像を拡大させると、ぼける。P5 で数値を描くアイコンも、この大きさで描く。
  - トレイのメニューの文字の大きさと位置が、150% で正しいかを実機で確かめる。崩れるなら、WPF の `ContextMenu` に替える。
- フォント
  - 目標は Electron 版と同じ見た目である。今のポップオーバーは `font-family: "Segoe UI", "Yu Gothic UI", "Meiryo", sans-serif` で、基準の大きさは 13px である(`renderer/style.css`)。
  - WPF の既定のフォントは、日本語の文字に意図しない代替のフォントを使うことがある。ルートの `FontFamily` に `Segoe UI, Yu Gothic UI, Meiryo` を明示し、`Language` を `ja-JP` にする。
  - CSS の px と WPF の論理単位は同じ大きさ(1/96 インチ)なので、`style.css` の `font-size` と `font-weight` の値をそのまま使う。
  - `AllowsTransparency="True"` は使わない。文字のアンチエイリアスが ClearType からグレースケールに落ち、にじんで見えるためである。Electron 版のポップオーバーも透過していない(`main.js` の `transparent: false`)。
  - `TextOptions.TextFormattingMode` と `TextRenderingMode` は、150% で Electron 版のスクリーンショットと並べて比べ、近いほうに決める。
- 確かめ方
  - P1、P5、P6 では、150% のモニターで、発行した exe の画面のスクリーンショットを撮る。P6 では Electron 版の同じ状態のスクリーンショットと並べ、文字の形、大きさ、太さ、にじみ、余白を比べる。比べた画像は PR に貼る。

### Electron 版との対応

| Electron 版 | C# 版での置き換え |
|---|---|
| `app.requestSingleInstanceLock`、`second-instance` | 名前付き `Mutex` と、既存のプロセスへ表示を頼む仕組み(名前付きイベントなど) |
| `Tray`、`Menu` | `System.Windows.Forms.NotifyIcon` と `ContextMenuStrip`(`UseWindowsForms` を有効にする)。トレイ用のライブラリを使う案は P1 で比べる |
| `BrowserWindow`(ポップオーバー) | 枠なしの WPF `Window`。`SizeToContent="Height"`、`Deactivated` で隠す、不透明度のアニメーションでフェードする |
| `screen.getDisplayNearestPoint`、`workArea` | `Shell_NotifyIconGetRect` でアイコンの位置を得て、`MonitorFromPoint` と `GetMonitorInfo` で作業領域を得る |
| `nativeTheme` | レジストリの `AppsUseLightTheme` を読み、`SystemEvents.UserPreferenceChanged` で変化を受ける |
| `ipcMain`、`preload.js` | 不要になる。ViewModel のメソッド呼び出しに置き換える |
| `app.setLoginItemSettings` | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` へ直接書く |
| `node:https` | `HttpClient` |
| `child_process.spawn` | `Process`。`codex app-server` は標準入出力をリダイレクトし、Job Object で終了時に子プロセスを道連れにする |
| `nativeImage`(トレイアイコンの生成) | `WriteableBitmap` か GDI+ で描き、`Icon` に変換する |
| `app.getPath('logs')` | `%APPDATA%\agent-limit-checker\logs`の `agent-limit-checker.log`。Electron 版と同じファイルで、P1 で追記されることを確かめた |
| `node --test` | `dotnet test`(xUnit) |
| `electron-builder` の portable exe | `dotnet publish` の単一 exe を `build-package.bat` で zip にする |

## 対応順序、ブランチ、PR

各段の PR は統合ブランチ `feature/csharp-wpf` の上に積む。
P0 だけは文書のみの変更なので main へ向ける。

| 順 | 対象 | 主な変更先 | 区分 | ブランチ | base | PR | 状態 |
|---|---|---|---|---|---|---|---|
| P0 | この計画の資料 | `docs/handover/` | メインセッション | `chore/csharp-wpf-migration-plan` | main | [#49](https://github.com/ktysne/agent-limit-checker/pull/49) | マージ済み(2026-10-10 確認) |
| P1 | 土台:ソリューション、テストの枠、単一インスタンス、終了だけのトレイ、ログ、`build-debug.bat` | `dotnet/`、`build-debug.bat` | standard | `feature/csharp-wpf-p1-scaffold` | `feature/csharp-wpf` | [#50](https://github.com/ktysne/agent-limit-checker/pull/50) | レビュー収束、マージ待ち(2026-10-10) |
| P2 | Codex:CLI の探索、ホームの探索、`codex app-server` の JSON-RPC クライアント | `dotnet/AgentLimitChecker.Core/Providers/Codex*` | hard | `feature/csharp-wpf-p2-codex` | P1 | [#51](https://github.com/ktysne/agent-limit-checker/pull/51) | レビュー収束、マージ待ち(2026-10-10) |
| P3 | Claude:資格情報の読み取り、利用量の取得、OAuth の更新、Retry-After | `dotnet/AgentLimitChecker.Core/Providers/Claude*` | hard | `feature/csharp-wpf-p3-claude` | P2 | [#52](https://github.com/ktysne/agent-limit-checker/pull/52) | レビュー収束、マージ待ち(2026-10-10) |
| P4 | 設定と ntfy への通知 | `dotnet/AgentLimitChecker.Core/Settings*`、`Notifications/` | standard | `feature/csharp-wpf-p4-settings-ntfy` | P3 | [#53](https://github.com/ktysne/agent-limit-checker/pull/53) | レビュー収束、マージ待ち(2026-10-10) |
| P5 | 取得の周期、トレイアイコンの描画、トレイのメニュー、ログイン用の端末の起動、ログイン完了の監視、自動の再認証 | `dotnet/AgentLimitChecker.App/` | hard | `feature/csharp-wpf-p5-shell` | P4 | [#54](https://github.com/ktysne/agent-limit-checker/pull/54) | レビュー収束、マージ待ち(2026-10-10) |
| P6 | ポップオーバーの UI:各サービスの表示、週の配分の目安、Codex の複数アカウント、設定パネル、テーマ、位置、表示倍率 | `dotnet/AgentLimitChecker.App/Views/`、`ViewModels/` | hard | `feature/csharp-wpf-p6-popover` | P5 | [#55](https://github.com/ktysne/agent-limit-checker/pull/55) | レビュー収束、マージ待ち(2026-10-10) |
| P7 | 自動起動(Run キー)と Electron 版からの移行、`build-release.bat` | `dotnet/AgentLimitChecker.App/AutoLaunch*`、`build-release.bat` | standard | `feature/csharp-wpf-p7-autolaunch` | P6 | [#56](https://github.com/ktysne/agent-limit-checker/pull/56) | レビュー収束、マージ待ち(2026-10-10) |
| P8 | 自動アップデート:manifest の取得と検証、zip の取得と照合、適用役、元へ戻す処理、後始末、通知の画面、設定パネルの「アップデートを確認」とトレイメニュー | `dotnet/AgentLimitChecker.Core/Updates/`、`dotnet/AgentLimitChecker.App/Updates/` | hard | `feature/csharp-wpf-p8-update` | P7 | [#57](https://github.com/ktysne/agent-limit-checker/pull/57) | レビュー収束、マージ待ち(2026-10-10) |
| P9 | 配布ページと発行:`site/` の 3 つの雛形、`tools/release-site.js` とそのテスト、`build-package.bat`、`tools/deploy.config.example.json` | `site/`、`tools/`、`build-package.bat`、`package.json` | standard | `feature/csharp-wpf-p9-release` | P8 | 作成中 | 実装中 |
| P10 | Electron 版の削除 | `main.js`、`preload.js`、`src/`、`renderer/`、`test/`、`smoke-test.js`、`package.json` | light | `feature/csharp-wpf-p10-remove-electron` | P9 | 未作成 | 未着手 |
| P11 | ドキュメントの整備:README、`docs/development.md`、`docs/design.md`、CLAUDE.md、AGENTS.md、`.cross-review.md` | `README.md`、`docs/`、`CLAUDE.md` ほか | standard | `feature/csharp-wpf-p11-docs` | P10 | 未作成 | 未着手 |
| 統合 | 実機での安定の確認後、統合ブランチを main へ | — | — | `feature/csharp-wpf` | main | 未作成 | 未着手 |
| 発行 | `build-package.bat` で 4.0.0 を発行し、配布ページと `update-v2.json` を公開する | — | 開発者 | main | — | — | 未着手 |

順序の依存は次のとおりである。

- P2 から P4 は互いに独立しているが、同じ `.csproj` とテストのプロジェクトに足すので、競合を避けるために積む。
- P5 は P2 から P4 のすべてを使う。
- P6 は P5 の状態(スナップショット)を表示する。
- P7 は exe の形が決まってからでないと、自動起動の登録先が決まらない。
- P8 の更新の画面は、P6 の設定パネルと P5 のトレイメニューに入口を置く。適用役は P7 の自動起動の登録先を変えない作りにする。
- P9 の `build-package.bat` は P7 の `build-release.bat` と同じ発行の引数を使う。配布ページの `manual.html` は P6 の画面を説明する。
- P11 は P10 の後に書く。Electron 版を消した後の構成を説明するためである。

### 各段の完了条件

- **P1**:`dotnet build` と `dotnet test` が通る。exe を起動するとトレイにアイコンが出て、メニューから終了できる。2 つ目を起動すると、すぐに終わる。単一 exe を発行でき、その大きさを記録する。
- **P2**:`test/codexProvider.test.js`、`test/cliPaths.test.js`、`test/codexHomes.test.js` の各ケースを移したテストが通る。実機で、既定のホームと別のホームの両方から利用量を取得できる。`.cmd` と `.ps1` の両方の形で入った `codex` を起動できる。アプリを強制終了しても `codex` の子プロセスが残らない。
- **P3**:`test/claudeProvider.test.js` の各ケースを移したテストが通る。期限切れの token だけを更新し、期限内に前倒しで更新しない(CLI と同時に更新すると片方の refresh token が無効になるため)。
- **P4**:`test/settings.test.js`、`test/ntfyNotifier.test.js` の各ケースを移したテストが通る。Electron 版が書いた `settings.json` をそのまま読める。
- **P5**:`test/loginCommand.test.js` の各ケースを移したテストが通る。トレイのアイコンとメニューが Electron 版と同じ内容を示す。ログイン用の端末が開き、ログインの完了を検知して再取得する。
- **P6**:`test/weeklyPace.test.js` の各ケースを移したテストが通る。150% の表示倍率で、ポップオーバーが内容に合った高さになり、切れもスクロールもしない。タスクバーの上下左右のどの位置でも作業領域の中に収まる。
- **P7**:Electron 版で自動起動を有効にした環境で C# 版を起動すると、設定が有効のまま表示される。Run キーが C# 版の exe を指すように登録し直され、ログオン時に C# 版が起動する。`build-release.bat` で入力した版が、exe のファイルのバージョンとポップオーバーの表示に出る。
- **P8**:agent-gc の更新の処理(`src/AgentGc.Core/Application/Update*.cs` と設計資料 34.13 章)と同じ検査をテストで確かめる。対象は、manifest の形式、URL の許可、リダイレクトの許可、SHA-256、zip のエントリー、自動で適用できる環境の判定である。開発用の環境変数で manifest の URL を差し替え、ローカルに置いた新しい版へ更新でき、起動に失敗する版では元へ戻ることを実機で確かめる。
- **P9**:`tools/release-site.js` のテストが通る。`generate` で配布ページと `update-v2.json` を作れる。`upload --dry-run` で送る内容を確かめられる。`build-package.bat` は agent-gc と同じ順で検査する(未コミットの変更、既存のタグ、公開中の版より新しいか、テスト、zip の中身の件数)。
- **P10**:Electron 関係のファイルと依存が消え、`dotnet test` と `npm run review:codex` が動く。
- **P11**:README の前半が利用者向け、末尾が開発者向けになる。`docs/development.md` にビルド、テスト、発行の手順がある。`docs/design.md` にプロバイダ、ポップオーバー、更新の仕様がある。CLAUDE.md のバージョニング、テスト、アプリ稼働中の編集の節が C# 版に合っている。

## 開発者の作業

次は開発者が行う。どれも P9 の発行の確認と、統合後の 4.0.0 の発行より前に要る。

- アプリアイコンの作成(2026-10-10 に配置済み)。元画像は背景ありの `assets/icon.png` を使い、背景なしが要る箇所だけ `assets/icon-transparent.png` を使う(開発者の指定)。exe とトレイの `dotnet/AgentLimitChecker.App/app.ico` は `python tools/make-app-icon.py` で作る。P9 でサイト用の `site/assets/app-icon-256.png` も同じスクリプトで作る。Electron 版の `assets/app-icon.ico` は P10 で消す。
- 発行先の準備。ktysne.info に `/agent-limit-checker/` を用意し、`tools/deploy.config.json`(コミットしない)に FTPS の接続情報を書く。

## 実機での確認(統合ブランチを main へ入れる前)

統合の PR を出す前に、統合ブランチから発行した exe で次を確かめる。

1. 150% の表示倍率と、倍率の違うモニターを 2 枚つないだ状態で、ポップオーバーの位置と高さが正しい。
2. タスクバーを下、上、左、右に置いて、ポップオーバーが作業領域に収まる。
3. Claude と Codex(複数のホームを含む)の利用量が、Electron 版と同じ値で表示される。
4. token の期限切れから自動で更新し、`claude` CLI 側のログインが切れない。
5. ntfy への通知が、リセットの時刻とクレジットの期限で届く。
6. 自動起動でログオン時に起動し、`--hidden` でポップオーバーを出さずに常駐する。
7. 数日間の常駐で、メモリーの増加と `codex` の子プロセスの残りが無い。
8. 開発用の manifest で、更新の通知、今すぐ更新、後で、このバージョンをスキップの 3 つが動く。更新の後も自動起動が働く。

## 落とし穴

- Electron 版と C# 版は単一インスタンスの仕組みが別なので、同時に起動できてしまう。両方が同じ `settings.json` を書き、同じ CLI の資格情報を更新するので、実機で試すときは Electron 版を終了しておく。
- Electron 版がトレイで動いている間は `npm start` がすぐ終わる(CLAUDE.md「アプリ稼働中の編集」)。見比べるときは portable exe を終了してから起動する。
- Electron 版が登録した Run キーの値の名前と、`getLoginItemSettings` が見る形式は未確認である。P7 の前に `test/login-item-probe.js` で実際の値を確かめる。
- npm で入れた `codex` の実体は `.cmd` か `.ps1` である。`.cmd` は `cmd /d /s /c` を、`.ps1` は `powershell -File` を経て起動する必要がある(`src/codexProvider.js` の起動の分岐を参照)。npm の `.ps1` の shim は標準入力を `$input |` で渡すので、`powershell -File` 経由では入力が終わるまで node へ届かず、`initialize` がタイムアウトする。C# 版は、shim と同じ規則で `node` と `node_modules/@openai/codex/bin/codex.js` を直接起動して避ける。Electron 版にはこの対策が無い。
- 自動アップデートは、%TEMP% からの起動と書き込めない場所(Program Files など)では自動で適用できない。agent-gc と同じく、その場合はブラウザで zip を開く動きにする。
- 発行の手順の違いに注意する。screen-recorder はタグを先に push し、Release をソースのリポジトリに作る。agent-gc は送信の後にタグを push する。この計画では agent-gc の順に合わせる。
- `build-package.bat` は Node 22.15 以上と `basic-ftp` を前提にする。`package.json` の `devDependencies` に `basic-ftp` を足す。
- ai-cross-review の観点ファイル `.cross-review.md` は Electron 前提で書かれている。P1 のレビューから C# の観点(資格情報をログに出さない、子プロセスの後始末、UI スレッドを止めない)を足しておく。正式な書き換えは P11 で行う。
- Windows の Bash ツールでは、続けて書いた `\` が半分になる(anthropics/claude-code#98622)。パスを含むスクリプトは、ファイルに書いてから実行する。

## 参照する手本

| 対象 | 手本 |
|---|---|
| 自動アップデートの処理 | agent-gc の `src/AgentGc.Core/Application/Update*.cs`、`src/AgentGc.Core/Infrastructure/HttpUpdateManifestFetcher.cs`、`src/AgentGc.App/Updates/` |
| 自動アップデートの仕様 | agent-gc の `docs/codex_claude_worktree_cleaner_design.md` 34.13 章 |
| 配布ページの雛形 | agent-gc の `site/index.template.html`、`manual.template.html`、`license.template.html` |
| 発行スクリプト | agent-gc の `tools/release-site.js` と `tests/tools/release-site.test.js`。小さく作るときは screen-recorder の同名ファイル |
| 発行のバッチ | agent-gc の `build-debug.bat`、`build-release.bat`、`build-package.bat` |
| 開発者向けの文書 | agent-gc の `docs/development.md` |
| トップページの掲載 | `D:\Desktop\Develop\ktysne-top` の `data/site.json`(各アプリの `url` と `pedal`) |

## 次のセッションの開始用プロンプト

```text
docs/handover/2026-10-10-csharp-wpf-migration.md を読み、C# + WPF への移行を進めてください。
統合ブランチ feature/csharp-wpf を最新の origin/main から作って push し、P1 から順に、計画の区分で実装を委譲してください。
各段の PR は計画の表のとおりスタック式で積み、段ごとにクロスレビューの三択を提示してください。
進んだら、計画の表の「PR」と「状態」の列を更新してください。
```
