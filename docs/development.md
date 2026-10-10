# 開発者向けドキュメント

この文書は、Agent Limit Checker の開発、テスト、パッケージ発行の手順を説明します。
利用方法は [README.md](../README.md)、実装の仕様は [design.md](design.md) を参照してください。

## 開発環境

- Windows 10 または Windows 11 の x64 環境を使います。
- .NET 10 SDK をインストールしてください。
- `build-package.bat` と Node.js のテストには Node.js 22.15 以上が必要です。
- パッケージ発行には Git と、ルートで `npm install` して用意する `basic-ftp` が必要です。
- アップロードまで行う場合は GitHub CLI(`gh`)も必要です。`gh auth login` で `ktysne/agent-limit-checker` に Release を作れるアカウントにログインし、`gh auth status` で確かめてから実行してください。zip とページをローカルに作るだけなら不要です。

## リポジトリ構成

| パス | 内容 |
| --- | --- |
| `dotnet/AgentLimitChecker.Core/` | プロバイダ、設定、通知、更新、トレイ操作のアプリケーションロジック |
| `dotnet/AgentLimitChecker.App/` | WPF の画面、システムトレイ、Windows 連携、アプリの起動 |
| `dotnet/AgentLimitChecker.Tests/` | Core と App の xUnit テスト |
| `tools/` | 配布サイト生成、パッケージ発行、クロスレビューの Node.js スクリプト |
| `site/` | 配布ページ、利用ガイド、ライセンスの HTML テンプレート |
| `tests/tools/` | 発行用ツールの Node.js テスト |

## ビルド

Debug ビルドは次のスクリプトで行います。

```powershell
.\build-debug.bat
```

Release の単一 exe を発行するときは次のスクリプトを使います。

```powershell
.\build-release.bat
```

どちらのスクリプトも、`dotnet` が PATH にないときはユーザー単位の .NET SDK の場所を PATH に追加します。
Release では版を入力できますが、省略すると `dotnet/AgentLimitChecker.App/AgentLimitChecker.App.csproj` の既定値を使います。
入力できる版は `X.Y.0` 形式です。

## テスト

アプリ本体と C# のプロバイダ、設定、通知、更新のテストには `dotnet test` を使います。

```powershell
dotnet test AgentLimitChecker.slnx -m:1 -nr:false --blame-hang-timeout 60s
```

`npm test` が実行するのは `tests/tools/*.test.js` の発行ツールのテストです。
アプリ本体のテストは含まれないため、C# の変更を確認するときは `dotnet test` も実行してください。

```powershell
npm test
```

## アプリ稼働中の編集

通常起動は `Local\AgentLimitChecker.Instance` という名前付き Mutex で単一インスタンスに制限されます。
アプリが起動中にもう一つ起動しても、2 つ目はすぐに終了します。
Debug ビルドは起動時に Windows の自動起動 Run キーを登録し直しません。
Release ビルドは設定で自動起動が有効な場合、起動時に Run キーの exe パスを現在の実行ファイルへ更新します。
実機確認では、開発者が普段使う Windows プロファイルの Run キーと `settings.json` を変更しないでください。

## パッケージ発行

パッケージの作成には次のスクリプトを使います。

```powershell
.\build-package.bat
```

スクリプトは作業ツリーが clean であること、版が公開中の版より新しいこと、既存タグが同じビルドコミットを指すことを確認します。
続けて C# のテストと発行ツールのテストを実行し、Release exe とサイトのページを生成します。
配布 zip が `AgentLimitChecker.exe`、`manual.html`、`license.html` の 3 ファイルだけを含むことを確認し、`update-v2.json` を作って C# 側の manifest テストでも受け入れられることを検証します。

アップロードを選ぶと、スクリプトはビルドしたコミットを指す注釈付きタグ `vX.Y.0` を作成して push し、その後に GitHub Release とサイトを更新します。
タグの push の後で `gh` が使えないと、タグだけが公開されて Release とサイトが更新されないため、事前に `gh auth status` を確かめてください。
Release 作成時に既存タグを検証するため、Release を作る前にタグをリモートへ置きます。
アップロードを選ばなければ、作成した zip とサイト用ファイルをローカルに残します。

配布先の接続設定は、[`tools/deploy.config.example.json`](../tools/deploy.config.example.json) を `tools/deploy.config.json` にコピーして入力します。
実設定ファイルは認証情報を含むため Git の対象外です。
設定ファイルを用意できない環境では、`AGENT_LIMIT_CHECKER_FTP_HOST`、`AGENT_LIMIT_CHECKER_FTP_USER`、`AGENT_LIMIT_CHECKER_FTP_PASSWORD`、`AGENT_LIMIT_CHECKER_FTP_REMOTE_ROOT` を設定できます。

## 開発用の環境変数

アプリが読む環境変数は次のとおりです。

| 環境変数 | 用途 |
| --- | --- |
| `CLAUDE_PATH` | Claude CLI の実行ファイルを指定します。 |
| `CODEX_PATH` | Codex CLI の実行ファイルを指定します。 |
| `CODEX_HOME` | Codex の既定ホームを指定し、ホーム探索にも追加します。 |
| `CLAUDE_CONFIG_DIR` | Claude の `.credentials.json` がある設定フォルダーを指定します。 |
| `CLAUDE_OAUTH_TOKEN_ENDPOINT` | Claude OAuth の更新先を開発用に上書きします。 |
| `CLAUDE_OAUTH_CLIENT_ID` | Claude OAuth のクライアント ID を開発用に上書きします。 |
| `AGENT_LIMIT_CHECKER_UPDATE_MANIFEST_URL` | 更新 manifest の取得先を上書きします。有効な開発用 URL は localhost です。 |

発行スクリプトは manifest 契約テスト用に `AGENT_LIMIT_CHECKER_TEST_MANIFEST_PATH` と `AGENT_LIMIT_CHECKER_TEST_MANIFEST_VERSION` を設定します。
この 2 つはテストが読む値であり、通常のアプリ起動では使いません。

## AI 相互レビュー

レビューの起点には次のコマンドを使います。

```powershell
npm run review:codex
```

このコマンドは現在の差分を Codex にレビューさせます。
このリポジトリ固有の観点は [`.cross-review.md`](../.cross-review.md) にあり、汎用のレビュー手順は vendored の [`docs/cross-review.md`](cross-review.md) にあります。
後者と `.claude/skills/` 配下の手順は同期対象のため、直接編集しないでください。
