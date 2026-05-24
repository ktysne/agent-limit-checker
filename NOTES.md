# agent-limit-checker — 開発メモ / 残課題

最終更新: 2026-05-24 (自律実装ターン)

## 現状サマリ
- Electron ベースで `npm start` するとシステムトレイに常駐し、左クリックでポップオーバー、右クリックでメニューが出る Windows 版を実装した。
- 参考: macOS 版 [otoha1119/token-checker](https://github.com/otoha1119/token-checker) の挙動・API 仕様を踏襲。
- **スモークテスト結果** (`node smoke-test.js`):
  - **Claude**: 401 Unauthorized (`~/.claude/.credentials.json` の access token が 2026-05-20 に期限切れ → 約 3.5 日経過)。
    - UI 上は「`claude login` を実行してください」のエラー文言で誘導している。
    - ユーザが `claude login` を再実行すれば直る想定 (CLI が credentials.json を書き換えるため、本アプリは何もしなくて良い)。
  - **Codex**: 実データ取得成功。`fiveHour.utilization=0.01 (1%)`, `weekly.utilization=0`。

## 既知の課題 / 後回しにした項目

### A. Claude トークンの自動リフレッシュ (優先度: 高)
- 現状: access token が切れると 401 のまま放置。ユーザに `claude login` を強いる。
- 望ましい挙動: `~/.claude/.credentials.json` の `refreshToken` を使って自動更新する。
- ブロッカー: Anthropic OAuth のリフレッシュ用エンドポイント URL と `client_id` が macOS 版のソースには含まれていなかった (Keychain から token を取ってくるだけで、refresh は Claude CLI 任せ)。Anthropic の公開仕様 or Claude CLI のソースを確認して以下を埋める必要あり:
  - `POST <token_endpoint>`
  - body: `grant_type=refresh_token&refresh_token=<rt>&client_id=<id>`
- 暫定回避: 5 分おきのポーリングで 401 を検知 → タスクトレイの tooltip で「再ログインが必要」を強調表示する処理を入れても良い (今は popover でしか分からない)。

### B. Codex CLI の起動経路 (優先度: 中)
- npm-global の `codex.cmd` を `cmd.exe /d /s /c "<path>" app-server` でラップして起動している。Node 20+ の CVE-2024-27980 対策で `.cmd` を直接 spawn できない (EINVAL) ためのワークアラウンド。
- `windowsVerbatimArguments: true` を指定しているので、`exe` のパスに `"` が含まれているとコマンドラインが壊れる。通常の `%APPDATA%\npm\codex.cmd` なら問題なし。
- TODO: `.exe` 形式の Codex (npm-global 経由でない) がリリースされた場合の検出を改善。

### C. トレイアイコンの描画 (優先度: 中)
- `src/trayIcon.js` で 32x32 の生 BGRA bitmap を毎回生成して `nativeImage.createFromBitmap` に渡している。
  - macOS 版は SwiftUI ビューを `ImageRenderer` で焼き、ドーナツ + パーセント数字をメニューバーに直接出していた。
  - Windows 版 v1 はトレイアイコンに「2 つのドーナツ」だけを出し、パーセンテージは tooltip / コンテキストメニューに回している (Windows のトレイは文字を載せにくいため)。
- TODO:
  - High DPI (`scaleFactor`) 対応: 現在は scaleFactor=1 固定。複数 DPI で滲む可能性。
  - アイコンセンターに小さな "C" / "X" マークを描いて Claude / Codex を識別しやすくする。
  - エラー状態を視覚化 (grey + ❗ オーバーレイなど)。

### D. ポップオーバー UX (優先度: 中)
- 起動時にトレイ近くにフレームレスウィンドウを表示。
- フォーカスが外れると自動で hide (`blur`)。
- TODO:
  - ESC キーで閉じる。
  - ウィンドウのアニメーション (フェード等)。
  - ライト/ダークテーマの追従 (`nativeTheme.shouldUseDarkColors`)。今はダーク固定。

### E. 自動起動 (優先度: 中)
- `app.setLoginItemSettings({openAtLogin: true, args:['--hidden']})` を使用。
- 起動引数 `--hidden` は今は読んでいない。ログイン時に起動したときにポップオーバーが手前に出ないようにする必要があるかも (今はトレイのみ表示で、ポップオーバーは show=false で作っているので問題ない見込み)。
- ⚠️ 自動起動を ON にした状態で「アプリの場所を移動」した場合、レジストリパスがズレるので動かなくなる。`electron-builder` で installer 化したらこの問題はほぼ解消する。

### F. ビルド / 配布 (優先度: 低)
- `package.json` に `electron-builder` の `portable` 設定は入れたが、まだビルドしていない。
- `npm run build` → `dist/AgentLimitChecker-<ver>.exe` (portable 単体実行) ができる想定。
- TODO: 専用アイコン (`assets/tray-icon.png` or `.ico`) を用意。今は同梱していないので、ビルド時にエラーになる可能性大。

### G. 他に未対応
- **CLAUDE_API_KEY 経由**: macOS 版同様、本アプリも OAuth トークン経由でのみ動作。`ANTHROPIC_API_KEY` での代替ログインは未対応。
- **multiple Codex プロファイル**: `rateLimitsByLimitId` を Sort して見る実装は入れているが、複数アカウントの選択 UI はなし。
- **エラー観測**: 現状 `console.error` で stdout 出力するだけ。本格的にはログファイル (`app.getPath('logs')`) に書き出すべき。
- **テスト**: 単体テストなし。`smoke-test.js` を手動実行する形。
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
│   └── trayIcon.js          # 32x32 ドーナツ bitmap 生成
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
