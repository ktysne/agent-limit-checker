# agent-limit-checker

Windows のシステムトレイに常駐し、**Claude Code** と **Codex** の API 使用率を可視化するアプリ。

## 必要環境
- Windows 10 / 11
- Node.js 22.12+ (Electron 42 のインストール / ビルドに必要)
- `claude` CLI ログイン済み (`~/.claude/.credentials.json` が存在)
- `codex` CLI ログイン済み (ホームディレクトリ直下に `.codex` で始まるディレクトリがあり、その中に `auth.json` か `config.toml` が存在)
  - `~/.codex` と `~/.codex-review` のように複数のホームを使い分けている場合、条件を満たすホームをそれぞれ 1 アカウントとして表示する
  - `CODEX_HOME` を設定している場合は、そのパスも 1 アカウントとして扱う

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
- ntfy アプリで購読する Topic URL を設定すると、5時間 / 週次リセット時刻のスマホ通知を個別に opt-in できます
- 設定画面の「Codex 表示名」で、Codex アカウントごとの表示名をホームディレクトリ名単位で変更できます (例: `.codex-sub` → `Codex Sub`)。ポップオーバー、トレイの tooltip / メニュー、ntfy 通知文のすべてに反映され、空にすると既定の名前 (`Codex` / `Codex (.codex-sub)`) に戻ります

## ntfy 通知
スマホ側の ntfy アプリで推測されにくいトピックを購読し、その Topic URL (例: `https://ntfy.sh/your-random-topic`) を設定してください。
保護された自前サーバーやアカウント付きトピックへ送る場合は、任意で Access token も設定できます。

## Claude OAuth refresh
access token が期限切れになると、`~/.claude/.credentials.json` の refresh token を使って自動で更新します。
ブラウザは開きません。更新した token は同じファイルへ書き戻すので、Claude Code CLI もそのまま使えます。

`claude login` による再ログインが必要なのは、refresh token 自体の期限 (約 30 日) が切れたときと、refresh に使う endpoint / client_id が上流の変更で無効になったときです。
どちらの場合も、エラーメッセージ末尾の `(refresh 失敗: ...)` に token endpoint が返した理由が入ります (`invalid_grant` なら refresh token の失効、`invalid_client` / `invalid_request` なら endpoint / client_id 側)。

refresh に使う endpoint / client_id は CLI と同じ値を既定で使います。上書きしたい場合のみ以下を設定してください。

```powershell
$env:CLAUDE_OAUTH_TOKEN_ENDPOINT = "https://..."
$env:CLAUDE_OAUTH_CLIENT_ID = "..."
```

## トラブルシュート
- **Claude が「401 Unauthorized」になる** → refresh token の期限切れか、refresh endpoint / client_id が上流で変わって refresh 自体が拒否された状態です。どちらもメッセージ末尾の `(refresh 失敗: ...)` で見分けられます。`claude login` を再実行すると、次回ポーリングで更新後の credentials を読み直します。
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

`npm run build` は `dist/AgentLimitChecker <version>.exe` を生成します。現在の設定はローカル portable ビルド優先で、Windows 署名/EXE メタデータ編集は無効です。

## リリース
PR では `package.json` の `version` を変更せず、リリースしたいタイミングでまとめて bump します。
バグ修正や中小機能追加は patch ではなく minor として扱います。

```powershell
# minor リリース (バグ修正 / 中小機能追加)
npm run release:minor

# major リリース (大きな機能変更)
npm run release:major

# 作成された release commit と tag を push
git push
git push --tags
```

`npm run release:*` は `npm version <level> -m "chore: release v%s"` を実行し、`package.json` の version bump、`chore: release vX.Y.0` コミット、`vX.Y.0` tag の作成をまとめて行います。

詳細仕様 / 既知の課題 / 残タスクは [`NOTES.md`](./NOTES.md) を参照。
