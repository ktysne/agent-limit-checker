# agent-limit-checker — 開発メモ / 残課題

最終更新: 2026-05-24 (残課題対応ターン)

## 現状サマリ
- Electron ベースで `npm start` するとシステムトレイに常駐し、左クリックでポップオーバー、右クリックでメニューが出る Windows 版を実装した。
- 参考: macOS 版 [otoha1119/token-checker](https://github.com/otoha1119/token-checker) の挙動・API 仕様を踏襲。
- **スモークテスト結果** (`node smoke-test.js`, `claude login` 実施後):
  - **Claude**: 実データ取得成功。`fiveHour.utilization=0.06 (6%)`, `weekly.utilization=0.09 (9%)`。
  - **Codex**: 実データ取得成功。`fiveHour.utilization=0.03 (3%)`, `weekly.utilization=0`。

## 既知の課題 / 後回しにした項目

### 0. 5h ウィンドウが N/A になる問題 (対応済み, 2026-05-24)
- 症状: スクリーンショット撮影時 (10:38 UTC) に Claude の `5時間` が `N/A` 表示。`週次` は 10% で取れているのに片方だけ落ちる状況。
- 原因切り分け:
  - その瞬間に `GET /api/oauth/usage` を直接叩いて生レスポンスを確認 → `five_hour` も `seven_day` も値が返っていた。
  - だが Anthropic 側の挙動として、**その時間ウィンドウでまだ消費がない場合に `{ "utilization": 0, "resets_at": null }` を返す**ことが判明 (実例: 同じレスポンス中の `seven_day_sonnet` / `seven_day_omelette` も同じ形)。
  - 旧 `parseBucket` は `if (!resetsAtRaw) return null` でバケット丸ごと捨てていたため、UI が「データなし=N/A」と判定。
- 修正:
  - `src/claudeProvider.js` の `parseBucket` で `resets_at: null` でもバケットを残し `{utilization, resetsAt: null}` を返す。
  - `renderer/renderer.js` の `resetText` が `resetsAt == null` のとき「ウィンドウ未開始 (このウィンドウでまだ消費なし)」を表示。
  - 単体テストに以下を追加:
    - `resets_at: null` で `utilization: 0` のバケットがちゃんと残ること
    - マイクロ秒精度 (`...338620+00:00`) の `resets_at` も Date.parse 経由で正しく数値化されること
    - utilization が missing のときは null を返すこと

### A. Claude トークンの自動リフレッシュ (対応済み / 制限あり)
- 実装済み:
  - `~/.claude/.credentials.json` の `expiresAt` を見て期限切れ直前を検知。
  - 401 時に credentials を再読込し、CLI 側で更新済みの access token があれば 1 回だけ再試行。
  - トレイ tooltip / コンテキストメニューに `login required` などのエラー状態を表示。
  - OAuth refresh は `CLAUDE_OAUTH_TOKEN_ENDPOINT` と `CLAUDE_OAUTH_CLIENT_ID` が両方設定されている場合のみ有効化。
- 残リスク:
  - Anthropic の Claude Code 用 OAuth refresh endpoint / client_id は公式公開仕様として確認できていないため、既定では direct refresh しない。
  - endpoint を設定しない場合、最終的な期限切れ復旧は `claude login` に委ねる。

### B. Codex CLI の起動経路 (対応済み)
- `src/cliPaths.js` を追加し、`CODEX_PATH` → `codex.exe` → `codex.cmd` → `codex.bat` → `codex.ps1` → `codex` の順で探索。
- VS Code 拡張に同梱される `openai.chatgpt-*/bin/windows-x86_64/codex.exe` も補助探索する。
- `.cmd` / `.bat` は引き続き `cmd.exe` 経由で起動し、Node 20+ の直接 spawn 制限を回避。

### C. トレイアイコンの描画 (対応済み)
- `src/trayIcon.js` で 32x32 の生 BGRA bitmap を毎回生成して `nativeImage.createFromBitmap` に渡している。
  - macOS 版は SwiftUI ビューを `ImageRenderer` で焼き、ドーナツ + パーセント数字をメニューバーに直接出していた。
  - Windows 版 v1 はトレイアイコンに「2 つのドーナツ」だけを出し、パーセンテージは tooltip / コンテキストメニューに回している (Windows のトレイは文字を載せにくいため)。
- 対応:
  - `screen` から `scaleFactor` を取り、DPI に応じた bitmap を生成。
  - Claude / Codex の中心に小さな `C` / `X` マークを描画。
  - エラー状態は灰色ベース + 赤い `!` バッジで表示。

### D. ポップオーバー UX (対応済み)
- 起動時にトレイ近くにフレームレスウィンドウを表示。
- フォーカスが外れると自動で hide (`blur`)。
- 対応:
  - ESC キーで hide。
  - 表示 / 非表示時に短いフェード。
  - `nativeTheme.shouldUseDarkColors` を renderer に渡し、CSS variables でライト / ダークに追従。

### E. 自動起動 (対応済み / 注意点あり)
- `app.setLoginItemSettings({openAtLogin: true, args:['--hidden']})` を使用。
- 起動引数 `--hidden` は今も明示的には読んでいないが、ポップオーバーは `show=false` で作るためログイン起動時も前面表示されない。
- 起動時に settings 上 autoLaunch が有効なら `app.setLoginItemSettings` を再適用し、portable exe 移動後のパスずれを軽減。
- ⚠️ アプリを移動した後、一度も手動起動しないまま次回ログインすると古いパスが残る可能性はある。installer 化が最終解。

### F. ビルド / 配布 (対応済み)
- `assets/app-icon.ico` を追加し、`package.json` の `win.icon` を `.ico` に変更。
- `npm run generate-icon` でアイコンを再生成できる。
- `npm run build` → `dist/AgentLimitChecker 0.1.0.exe` (portable 単体実行) を確認済み。
- この環境では `winCodeSign` 展開時に symlink 権限エラーが出るため、`win.signAndEditExecutable=false` で未署名 portable を生成する設定にしている。
- TODO: 署名付きで配布する場合は Developer Mode / 管理者権限 / CI の署名環境で `signAndEditExecutable` を戻して確認。

### I. 「ログイン時に起動」スイッチが反応しない問題 (対応済み, 2026-05-24)
- 症状: ポップオーバーの「ログイン時に自動起動」スライダーを押しても、瞬時に OFF に戻ってしまい有効化できない (内部的には Run キーが書き込まれていることもある)。
- 切り分け:
  - 別 `appUserModelId` を使った probe (`test/login-item-probe.js`) で `setLoginItemSettings`/`getLoginItemSettings` の挙動を直接観察。
  - `setLoginItemSettings({openAtLogin: true, path: ..., args: ...})` の直後に **`getLoginItemSettings()` (引数なし) を呼ぶと `openAtLogin: false` が返る**ことが判明。`executableWillLaunchAtLogin: true` / `launchItems` には登録された情報が入っており、登録自体は成功している。
  - 引数付き `getLoginItemSettings({path, args})` だと `openAtLogin: true` になる。Electron は path + args が完全一致した場合のみ `openAtLogin: true` を返す仕様。
- 原因: `src/autoLaunch.js#isEnabled` が引数なしで `getLoginItemSettings()` を呼んでいたため、登録済みでも常に `false` を返していた。IPC ハンドラはこれを snapshot に詰めて返し、renderer の `applySnapshot` が `al.checked = false` を強制セットするため UI が瞬時に OFF に戻る。
- 修正:
  - `isEnabled` を「登録時と同じ `path` + `args` を渡して `getLoginItemSettings` を呼ぶ」「`openAtLogin` が false なら `executableWillLaunchAtLogin` を見る」「それも無ければ `launchItems[].enabled` を確認」の順で読むよう変更。
  - `src/autoLaunch.js#getRegistrablePath()` を追加し、electron-builder portable target のときは `process.env.PORTABLE_EXECUTABLE_FILE` を採用。portable は `%TEMP%\<random>\AgentLimitChecker.exe` に展開され終了時に消えるので、`process.execPath` を Run キーに書くと次回ログイン時にリンク切れになる。launcher exe のパスは固定なのでこちらを使う。
- 検証: `npx electron test/login-item-probe.js` が PASS。`test/login-item-stale-probe.js` で古い path の Run エントリがあるときも Electron が無視することを確認 (= 古いエントリで isEnabled が誤って true を返すことはない)。
- 残り注意点: ユーザのレジストリに既に古い temp path の Run エントリが残っている場合、本修正をビルドしたバイナリで一度トグル OFF→ON すると最新のランチャパスで上書きされる。

### G. 他に未対応
- **CLAUDE_API_KEY 経由**: macOS 版同様、本アプリも OAuth トークン経由でのみ動作。`ANTHROPIC_API_KEY` での代替ログインは未対応。
- **multiple Codex プロファイル**: 1 アカウント内の複数プロファイル (`rateLimitsByLimitId`) は Sort して先頭を採用するだけで、プロファイルの選択 UI はない。ホームを分けた複数アカウントは下記「Codex usage」のとおりアカウントごとに表示する。
- **エラー観測**: `app.getPath('logs')/agent-limit-checker.log` に poll / login / tray 系エラーを書き出す最低限の logger を追加済み。
- **テスト**: `npm test` で `node --test` を実行。Claude refresh 正規化と CLI 探索順の単体テストを追加済み。実接続は引き続き `node smoke-test.js`。
- **i18n**: UI 文字列はすべて日本語ハードコード。

### H. ポップオーバーが開閉のたびに小さくなる問題 (対応済み, 2026-05-24)
- 症状: トレイクリックでポップオーバーを何回か開閉していると、ウィンドウが少しずつ縮んでいく。
- 原因:
  - `positionWindowNearTray()` が `popoverWindow.getBounds()` → `popoverWindow.setBounds({ x, y, width, height })` のラウンドトリップを毎回実行していた。
  - Windows の表示スケーリングが 100% 以外 (125% / 150% など) のとき、Electron は内部で device pixel ↔ logical pixel の変換を行い、その際に丸めが発生する。
  - これを開閉ごとに繰り返すと、開くたびに `width` / `height` が 1〜2px ずつ縮んでいく。
- 修正:
  - `POPOVER_WIDTH = 360`, `POPOVER_HEIGHT = 520` を定数化。
  - `BrowserWindow` 作成時に `useContentSize: true`, `minWidth/maxWidth/minHeight/maxHeight` を同値で固定。
  - `positionWindowNearTray()` を `setPosition(x, y)` + `setContentSize(POPOVER_WIDTH, POPOVER_HEIGHT)` に変更。`getBounds → setBounds` のラウンドトリップを完全に廃止。
  - 既に縮んでしまった状態から復帰するための `setContentSize` を毎回実行。

### I. ポップオーバーに小さなスクロールバーが出る問題 (対応済み, 2026-06-03)
- 症状: タスクバー (トレイ) からアプリを開くと、ほんの少しだけスクロールでき、スクロールバーが出る。縦幅の整合性が少しおかしい。
- 原因:
  - ウィンドウ高さを `POPOVER_HEIGHT = 560` で固定 (`min/maxHeight` も同値ロック) していたが、コンテンツの実高さは状態によって変動する。
  - Claude / Codex の両方がログイン済みで plan ラベル付きのとき、実コンテンツは約 563px になり 560px を超える → 小さなスクロールバーが出る。
  - 逆にバーが少ない状態 (例: Claude のみ ~544px) では 560px が高すぎて下部に余白が出る。これが「縦幅の整合性がおかしい」の正体。
  - 単一のハードコード高さでは全状態に整合できない。
- 修正 (fit-to-content):
  - main は受け取った高さを `POPOVER_MIN/MAX_HEIGHT` でクランプし、`setContentSize(POPOVER_WIDTH, popoverHeight)` でウィンドウをコンテンツに合わせる。`positionWindowNearTray()` も `popoverHeight` を参照してトレイに再アンカー。
  - 高さは renderer の DOM 実測値のみを使い、`getBounds → setBounds` のラウンドトリップは引き続き一切しない → セクション H の DPI 縮みは再発しない。
  - `BrowserWindow` の `min/maxHeight` ロックは撤去 (フィット時にクランプされてしまうため)。幅は `min/maxWidth` で固定のまま、`resizable: false` でユーザリサイズも不可。
- ⚠ 重要 (1.4.0 で再発 → 2026-06-03 に再修正): 単純に `Math.ceil(content)` を送る初版では **分数 DPI で直らなかった**。
  - 実機 (scaleFactor=1.5 / 150%) で計測すると、`setContentSize(360, 563)` を呼んでも実ビューポートは **556px** にしかならず (約 7px 不足)、563px のコンテンツが 7〜13px はみ出してスクロールバーが出続けていた。`getContentSize` の幅も 360→346 とズレており、Electron の非フレーム窓 + 分数 DPI での丸め (不可視 DWM フレーム由来と思われる) が原因。不足量は scaleFactor 依存なので固定値で補正できない。
  - 対策 = **自己補正ループ + `overflow: hidden`**:
    - `renderer.js#syncWindowHeight()` が `content` (= `.container` の高さ) と `overflow` (= `documentElement.scrollHeight - innerHeight`) を見て、はみ出していれば `requestedHeight + overflow` を要求して窓を伸ばし、余白が出れば縮める。`ResizeObserver` + `window` の `resize` で駆動し、2px ヒステリシスで 2〜3 フレーム (実測 5〜7 通知) で収束。実際のはみ出しを観測して詰めるので DPI 補正の固定値が不要。
    - `style.css` の `html, body { overflow: hidden }` でスクロールバーを物理的に出さない。窓は常にコンテンツ以上のサイズに収束するので、隠れるのは収束途中のサブピクセル余白だけ (実コンテンツは欠けない)。
    - `preload.js#reportContentHeight` で `content-height` を送るのは従来通り。
- 検証: `npx electron test/popover-fit-probe.js [scaleFactor]` が PASS。スクリーンショットと同じ最も背の高いスナップショット (Sonnet が「ウィンドウ未開始…」の長行) を実 renderer + preload に流し、`force-device-scale-factor` で **1.0 / 1.25 / 1.5 / 2.0 すべて `scrollPx=0` かつ gap≤1px に収束** することを確認。
- 上限を超えたときの挙動 (現在の仕様):
  - 高さの上限は `min(900, トレイのある display の workArea.height - 8)`。表示領域を読めないときだけ 900 に落とす。1080p の 150% 表示は作業領域が約 690px しかなく、900px 固定では窓が画面外にはみ出すため。
  - 上限でクランプしたときだけ main が `content-clamped` を送り、renderer が `html.clamped` を付けて `overflow-y: auto` にする。通常時は従来どおり `overflow: hidden` のままで、分数 DPI の数 px の余りでスクロールバーは出ない。
  - クランプ中の renderer は、はみ出していても新しい高さを要求しない。伸びない窓に要求を送り続けても収束しないため。コンテンツが上限より低くなるとクランプが解け、通常のフィットループに戻る。
  - main は renderer が最後に報告した生の高さを保持する。表示先の作業領域が変わったとき (ポップオーバーを開くたび) はその値で再クランプし、上限が上がっていれば窓を広げてクランプを解く。クランプ中の renderer は高さを報告しないため、保持した値だけが復帰の手がかりになる。
  - 検証は `popover-fit-probe.js` の clamped ケース (Codex 3 アカウント + 設定パネル)、および ceiling-raised ケース (クランプ後に上限を上げて再適用)。`html.clamped` が付くこと、スクロールできること、クランプ後に要求が止まること、上限が上がるとクランプが解けて窓がコンテンツ高さに収束することを 1.0 / 1.25 / 1.5 / 2.0 で確認。

## ファイル構成
```
agent-limit-checker/
├── main.js                  # Electron main process (Tray + IPC + ポーリング)
├── preload.js               # contextBridge (window.api)
├── package.json
├── smoke-test.js            # Claude/Codex プロバイダを単体で叩く動作確認用
├── src/
│   ├── claudeProvider.js    # ~/.claude/.credentials.json → /api/oauth/usage
│   ├── codexProvider.js     # spawn `codex app-server` + JSON-RPC `account/rateLimits/read`
│   ├── codexHomes.js        # ~/.codex* の走査と Codex アカウント一覧の生成
│   ├── settings.js          # userData/settings.json
│   ├── autoLaunch.js        # app.setLoginItemSettings ラッパ
│   ├── cliPaths.js          # Claude/Codex CLI の探索
│   ├── logger.js            # app logs への簡易ログ出力
│   └── trayIcon.js          # DPI 対応ドーナツ bitmap 生成
├── scripts/
│   └── generate-icon.js     # Windows build 用 .ico 生成
├── test/
│   ├── claudeProvider.test.js
│   └── cliPaths.test.js
├── renderer/
│   ├── index.html
│   ├── style.css
│   └── renderer.js
├── _reference/              # 参考: 元 Swift 実装 (Windows 版には不要だが残してある)
├── NOTES.md                 # 本ファイル
└── README.md
```

## 仕様メモ (参考実装から確定済み)

### Claude usage API
- `GET https://api.anthropic.com/api/oauth/usage`
- Headers: `Authorization: Bearer <access_token>`, `anthropic-beta: oauth-2025-04-20`
- Response 例 (推定):
  ```json
  {
    "five_hour":         { "utilization": 23.5, "resets_at": "2026-05-24T15:00:00Z" },
    "seven_day":         { "utilization": 12.0, "resets_at": "2026-05-31T00:00:00Z" },
    "seven_day_sonnet":  { "utilization": 8.2,  "resets_at": "2026-05-31T00:00:00Z" }
  }
  ```
- `utilization` は 0〜100 (パーセント)。1.0 を超えうる。
- HTTP リダイレクトは追わない (トークン漏洩防止)。
- **利用可能クレジット** (2026-07-19 追加): レスポンス末尾に `spend` オブジェクトがあり、`spend.balance` が使用クレジット残高。形は `spend.used` / `spend.limit` と同じ money 形式:
  ```json
  "spend": { "balance": { "amount_minor": 500, "currency": "USD", "exponent": 2 } }
  ```
  残高が無いアカウント (プランのみ) では `spend.balance: null`。`src/claudeProvider.js#parseCredits` が `amount_minor / 10^exponent` で major 単位に正規化し、正の残高のときだけ `{ amount, currency, unlimited:false }` を返す (無ければ `null` → UI で項目非表示)。旧レスポンスは `spend` 自体が無いので同じく `null`。

### Codex usage (JSON-RPC over stdio)
- spawn: `codex app-server` (Windows は `cmd /c codex.cmd app-server`)
- Handshake: `initialize` → `initialized` (notification)
- 取得: `account/rateLimits/read` (params: `{}`)
- Response:
  ```json
  {
    "rateLimits": {
      "limitId": "codex",
      "primary":   { "usedPercent": 1, "windowDurationMins": 300,   "resetsAt": 1779614312 },
      "secondary": { "usedPercent": 0, "windowDurationMins": 10080, "resetsAt": 1780201112 },
      "planType": "plus"
    },
    "rateLimitsByLimitId": { "codex": { ... } }
  }
  ```
- `usedPercent`: 0〜100 Int / `resetsAt`: Unix epoch 秒 / 300 分=5h, 10080 分=週次。
- **利用可能クレジット** (2026-07-19 追加 / 07-20 単位訂正): `rateLimits`(および各 `rateLimitsByLimitId[*]`) に `credits` が付く:
  ```json
  "credits": { "hasCredits": true, "unlimited": false, "balance": "115.9354600000" }
  ```
  ⚠ `balance` は **ドルではなく Codex「クレジット」の数**を表す 10 進文字列。型 (`app-server-protocol` の `CreditsSnapshot = { hasCredits, unlimited, balance:string|null }`) に currency は無く、codex 本体の `/status` も `"<四捨五入> credits"` と表示する (`$` は付けない)。よって `src/codexProvider.js#parseCredits` は `currency:null`（＝金額ではなく計数）でマークし、`hasCredits:true` かつ残高が正のときだけ `{ amount, currency:null, unlimited:false }` を返す。UI (`formatCredit`) は `currency` が無いとき残高を**小数第3位以下で四捨五入**して 2 桁で「N.NN クレジット」と表示 (`toFixed(2)`。`115.9354… → 115.94`)。`unlimited:true` は `{ amount:null, currency:null, unlimited:true }` として区別 (UI は「無制限」)。それ以外は `null` → UI で項目非表示。`credits` ノードは `rateLimits` 優先、無ければ `rateLimitsByLimitId` をキー昇順で探索し、さらに `hasCredits:true` のノードを優先 (extractPlanLabel と同じ選択規則 + クレジット有無で優先)。
  - 対して Claude 側の `spend.balance` は `currency:"USD"` 付きの**本物の金額**なので、そちらは従来どおり通貨表示 (`$5.00`)。両者は単位が異なる。
- **複数ホーム (複数アカウント)**: `src/codexHomes.js#discoverCodexHomes` がホームディレクトリ直下の `.codex` で始まるディレクトリを走査し、直下に `auth.json` か `config.toml` があるものだけをアカウントとして採用する。どちらも無いディレクトリは Codex のホームだと確定できず、app-server を起動しても失敗するだけなので除外する。
- `CODEX_HOME` が設定されていればそのパスも候補に加える (ホームディレクトリ外でもよい)。走査結果と重複する場合は絶対パス (Windows では小文字化) で 1 件にまとめる。既定のホーム (`CODEX_HOME`、無ければ `~/.codex`) が一覧の先頭に来る。
- app-server はホームごとに 1 プロセス起動し、子プロセスの `CODEX_HOME` をそのホームに固定する (`src/codexProvider.js#buildChildEnv`)。ログイン完了時に停止するのは対象ホームのプロセスだけで、他アカウントの接続は維持する。
- 候補が 1 件も無いときは `codex_home_missing` のプレースホルダを 1 件返す。ポップオーバーに Codex セクションが必ず 1 つ出るので、🔑 から `codex login` を実行して `~/.codex` を作れる。
- トレイのドーナツは 1 個なので、5 時間枠の利用率が最も高いアカウントを表示する。ツールチップ、コンテキストメニュー、ポップオーバーはアカウントごとに 1 行 (1 セクション) を出す。1 アカウントのときの表示は従来と同じ「Codex」のまま。
- **表示名の変更**: 設定画面の「Codex 表示名」でアカウントごとの表示名を上書きできる。設定は `settings.json` の `codexAccountNames` に `{ "<ホームディレクトリ名>": "<表示名>" }` の形で入る (例: `{ ".codex-sub": "Codex Sub" }`)。キーがパスではなくホームディレクトリ名なのは、ユーザが識別子として読めることと、ホームの移動やユーザ名変更で設定が失われないため。値は trim して 40 文字で切り詰め、空文字のエントリは保存せずに削除する (= 既定名に戻る)。上書き名は `Codex (...)` で包まず、そのまま見出し・ツールチップ・トレイメニュー・ntfy 通知文に使う。解決済みの名前は `main.js#decorateCodexAccounts` がスナップショットの各アカウントに `displayName` として付与し、renderer と ntfy はそれを読む。
