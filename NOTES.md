# agent-limit-checker — 開発メモ

最終更新: 2026-05-24

## 現状サマリ
- Electron ベースで `npm start` するとシステムトレイに常駐する Windows 版。左クリックでポップオーバー、右クリックでメニュー。
- 参考: macOS 版 [otoha1119/token-checker](https://github.com/otoha1119/token-checker)。
- スモークテスト (`node smoke-test.js`, `claude login` 実施後): Claude / Codex とも実データ取得成功。

## 未対応 / 既知の制限
- **Claude OAuth direct refresh**: `~/.claude/.credentials.json` の `expiresAt` 監視と 401 時の再読込までは実装済み。しかし Anthropic の OAuth token endpoint / client_id が公開仕様として確認できていないため、direct refresh は `CLAUDE_OAUTH_TOKEN_ENDPOINT` + `CLAUDE_OAUTH_CLIENT_ID` 環境変数が両方設定されている場合のみ有効。それ以外の期限切れ復旧は `claude login` に委ねる。
- **API キー経由のログイン**: Claude / Codex とも OAuth トークン経由でのみ動作。`ANTHROPIC_API_KEY` / `OPENAI_API_KEY` での代替ログインは未対応。
- **複数 Codex プロファイル**: `rateLimitsByLimitId` を sort して見る実装は入っているが、複数アカウントを切り替える UI はなし。
- **i18n**: UI 文字列は日本語ハードコード。
- **コード署名**: 現在 `win.signAndEditExecutable=false` で未署名 portable を生成する設定。署名付き配布する場合は Developer Mode / 管理者権限 / CI の署名環境で `signAndEditExecutable` を戻して確認が必要。
- **portable launcher exe を移動した場合**: 自動起動の Run キーは `PORTABLE_EXECUTABLE_FILE` を介して launcher exe の固定パスを記録する。launcher を移動 / リネームすると次回ログイン時の自動起動がリンク切れになる。installer 化が最終解。

## ファイル構成
```
agent-limit-checker/
├── main.js                       # Electron main process (Tray + IPC + ポーリング)
├── preload.js                    # contextBridge (window.api)
├── package.json
├── smoke-test.js                 # Claude/Codex プロバイダを単体で叩く動作確認用
├── src/
│   ├── claudeProvider.js         # ~/.claude/.credentials.json → /api/oauth/usage
│   ├── codexProvider.js          # spawn `codex app-server` + JSON-RPC `account/rateLimits/read`
│   ├── settings.js               # userData/settings.json
│   ├── autoLaunch.js             # app.setLoginItemSettings ラッパ
│   ├── cliPaths.js               # Claude/Codex CLI の探索
│   ├── logger.js                 # app logs への簡易ログ出力
│   └── trayIcon.js               # DPI 対応ドーナツ bitmap 生成
├── scripts/
│   └── generate-icon.js          # Windows build 用 .ico 生成
├── test/
│   ├── claudeProvider.test.js    # parseBucket / OAuth refresh の単体テスト (npm test)
│   ├── cliPaths.test.js          # CLI 探索順の単体テスト (npm test)
│   ├── popover-size.smoke.js     # ポップオーバー縮小バグの回帰テスト (Electron 必須)
│   ├── login-item-probe.js       # setLoginItemSettings / isEnabled の挙動診断 (Electron 必須)
│   └── login-item-stale-probe.js # 古い Run エントリ存在時の挙動診断 (Electron 必須)
├── renderer/
│   ├── index.html
│   ├── style.css
│   └── renderer.js
├── NOTES.md                      # 本ファイル
└── README.md
```

## 仕様メモ (参考実装から確定済み)

### Claude usage API
- `GET https://api.anthropic.com/api/oauth/usage`
- Headers: `Authorization: Bearer <access_token>`, `anthropic-beta: oauth-2025-04-20`
- レスポンス例 (実機 2026-05-24 採取):
  ```json
  {
    "five_hour":          { "utilization": 3,  "resets_at": "2026-05-24T15:30:00.338620+00:00" },
    "seven_day":          { "utilization": 10, "resets_at": "2026-05-27T10:00:00.338640+00:00" },
    "seven_day_sonnet":   { "utilization": 0,  "resets_at": null },
    "seven_day_omelette": { "utilization": 0,  "resets_at": null }
  }
  ```
- `utilization` は 0〜100 (パーセント)。1.0 を超えうる。
- `resets_at: null` は「このウィンドウでまだ消費なし」を意味する (= 0% / 計測未開始)。
- HTTP リダイレクトは追わない (Bearer トークン漏洩防止)。

### Codex usage (JSON-RPC over stdio)
- spawn: `codex app-server` (Windows は `cmd /c codex.cmd app-server`)
- Handshake: `initialize` → `initialized` (notification)
- 取得: `account/rateLimits/read` (params: `{}`)
- レスポンス:
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
