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
- エラー時はトレイ tooltip / メニューにも `login required` などを表示

## Claude OAuth refresh
既定では Claude Code CLI が更新した `~/.claude/.credentials.json` を読み直して復旧します。
Anthropic の refresh endpoint / client_id を明示できる環境だけ、以下を設定すると direct refresh を試します。

```powershell
$env:CLAUDE_OAUTH_TOKEN_ENDPOINT = "https://..."
$env:CLAUDE_OAUTH_CLIENT_ID = "..."
```

## トラブルシュート
- **Claude が「401 Unauthorized」になる** → `claude login` を再実行。次回ポーリングで更新後の credentials を読み直します。
- **Claude login ボタンで CLI が見つからない** → `CLAUDE_PATH` に `claude.exe` / `claude.cmd` / `claude.ps1` のパスを設定。
- **Codex が「codex_cli_missing」になる** → PowerShell で `npm i -g @openai/codex` を実行。
- **Codex login ボタンで CLI が見つからない** → `CODEX_PATH` に `codex.exe` / `codex.cmd` / `codex.ps1` のパスを設定。
- **Codex が「codex_spawn_failed: spawn EINVAL」** → Node のバージョンが古い可能性。Node 20.10+ にアップグレード。
- **詳しいエラーを見たい** → Electron の logs ディレクトリに `agent-limit-checker.log` を出力します。

## 開発 / デバッグ
```powershell
# 単体テスト
npm test

# プロバイダ単体テスト
node smoke-test.js

# Windows build 用アイコンを再生成
npm run generate-icon

# 未署名 portable exe を作成
npm run build

# DevTools を開いた状態で起動
npm run dev
# → ポップオーバーを開いてから Ctrl+Shift+I で DevTools
```

`npm run build` は `dist/AgentLimitChecker 0.1.0.exe` を生成します。現在の設定はローカル portable ビルド優先で、Windows 署名/EXE メタデータ編集は無効です。

詳細仕様 / 既知の課題 / 残タスクは [`NOTES.md`](./NOTES.md) を参照。
