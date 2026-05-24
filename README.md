# agent-limit-checker (Windows port of token-checker)

Windows のシステムトレイに常駐し、**Claude Code** と **Codex** の API 使用率を可視化するアプリ。

## 必要環境
- Windows 10 / 11
- Node.js 20+ (Electron 32 が動く版)
- `claude` CLI ログイン済み (`~/.claude/.credentials.json` が存在)
- `codex` CLI ログイン済み (`~/.codex/auth.json` が存在)

## 使い方
```powershell
npm install
npm start
```
- トレイにアイコン (2 つのドーナツ) が出る
- 左クリック → ポップオーバー (5h / 週次の使用率と残り時間)
- 右クリック → 更新間隔・自動起動・ログイン・終了などのメニュー
- 既定のポーリング間隔は 5 分

## トラブルシュート
- **Claude が「401 Unauthorized」になる** → `claude login` を再実行。CLI が認証情報を更新するので本アプリは何もしなくて良い。
- **Codex が「codex_cli_missing」になる** → PowerShell で `npm i -g @openai/codex` を実行。
- **Codex が「codex_spawn_failed: spawn EINVAL」** → Node のバージョンが古い可能性。Node 20.10+ にアップグレード。

## 開発 / デバッグ
```powershell
# プロバイダ単体テスト
node smoke-test.js

# DevTools を開いた状態で起動
npm run dev
# → ポップオーバーを開いてから Ctrl+Shift+I で DevTools
```

詳細仕様 / 既知の課題 / 残タスクは [`NOTES.md`](./NOTES.md) を参照。
