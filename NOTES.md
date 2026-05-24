# agent-limit-checker — 開発メモ / 残課題

最終更新: 2026-05-24 (残課題対応ターン)

## 現状サマリ
- Electron ベースで `npm start` するとシステムトレイに常駐し、左クリックでポップオーバー、右クリックでメニューが出る Windows 版を実装した。
- 参考: macOS 版 [otoha1119/token-checker](https://github.com/otoha1119/token-checker) の挙動・API 仕様を踏襲。
- **スモークテスト結果** (`node smoke-test.js`, `claude login` 実施後):
  - **Claude**: 実データ取得成功。`fiveHour.utilization=0.06 (6%)`, `weekly.utilization=0.09 (9%)`。
  - **Codex**: 実データ取得成功。`fiveHour.utilization=0.03 (3%)`, `weekly.utilization=0`。

## 既知の課題 / 後回しにした項目

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

### G. 他に未対応
- **CLAUDE_API_KEY 経由**: macOS 版同様、本アプリも OAuth トークン経由でのみ動作。`ANTHROPIC_API_KEY` での代替ログインは未対応。
- **multiple Codex プロファイル**: `rateLimitsByLimitId` を Sort して見る実装は入れているが、複数アカウントの選択 UI はなし。
- **エラー観測**: `app.getPath('logs')/agent-limit-checker.log` に poll / login / tray 系エラーを書き出す最低限の logger を追加済み。
- **テスト**: `npm test` で `node --test` を実行。Claude refresh 正規化と CLI 探索順の単体テストを追加済み。実接続は引き続き `node smoke-test.js`。
- **i18n**: UI 文字列はすべて日本語ハードコード。

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
