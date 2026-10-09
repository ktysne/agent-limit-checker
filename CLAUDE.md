# Agent Limit Checker — 開発ガイド (運用ルール)

このファイルは、本リポジトリで作業するときに AI エージェント (Claude / Codex、またはレビュアー) が参照する運用ルールをまとめたものです。
アプリは C# + WPF のトレイ常駐アプリで、コードは `dotnet/` にある。
ビルド、テスト、発行の手順は [docs/development.md](docs/development.md)、仕様は [docs/design.md](docs/design.md) にある。

---

## バージョニング

### 起点
- C# 版は **`4.0.0` を起点**とする。`3.x` までは Electron 版である。
- 版の正本は `dotnet/AgentLimitChecker.App/AgentLimitChecker.App.csproj` の `Version`(既定の版)と、`build-release.bat`、`build-package.bat` で入力する版である。入力した版は `-p:Version=` でアセンブリに入り、アプリ内の表示、ログ、exe のファイルのバージョンに伝わる。
- `package.json` は発行とレビューの道具のためだけにあり、`version` を持たない。

### バンプ規則 (独自ルール: semver と一部異なる)
| 区分 | 例 | 何のとき |
|---|---|---|
| **major** | 4.0.0 → 5.0.0 | 大きな機能変更 (API/UX の刷新、互換性のない仕様変更、メジャー機能追加) |
| **minor** | 4.0.0 → 4.1.0 | それ以外の機能変更 / バグ修正 |
| **patch** | 4.0.0 → 4.0.1 | **使用しない** (上記 minor に集約) |

> ⚠️ `patch` は意図的に運用していません。バグフィックスでも minor を上げます (ユーザ指定)。`build-release.bat` と `build-package.bat` は `X.Y.0` の形の版だけを受け付ける。

### リリースのやり方 (運用フロー)
PR は **版を触らずに** マージし、リリースしたいタイミングで `build-package.bat` を実行して版を入力する。

`build-package.bat` は次の順に進む(詳細は [docs/development.md](docs/development.md) の「パッケージ発行」)。
1. 未コミットの変更が無いこと、同じ版のタグが無いこと、公開中の版より新しいことを確かめる。
2. `dotnet test` と `npm test` を実行する。
3. 単一 exe を発行し、`AgentLimitChecker-X.Y.0-win-x64.zip`、配布ページ、`update-v2.json` を作る。
4. ビルドしたコミットにタグ `vX.Y.0` を付けて push し、GitHub の Release を作り、配布サイトへ送る。

### PR を作るときの判断
- PR の説明に **どのレベルに該当するか** を明記すると後でリリース判断しやすい:
  - `Version impact: major (...)`
  - `Version impact: minor (feature)`
  - `Version impact: minor (bugfix)`
- ただし **PR 自身は csproj の `Version` を変更しない**。版はリリース時に入力する。

### どこに version が現れるか
- アプリ内: ポップオーバー footer 左側 (`v4.0.0`)
- ログの起動メッセージ (`[app] ready 4.0.0`)
- exe のファイルのバージョンと製品のバージョン
- 配布の zip の名前 (`AgentLimitChecker-4.0.0-win-x64.zip`) と `update-v2.json`
- git tag (`v4.0.0`)

### リリースしないケース
- ドキュメントのみの変更 (例: `chore/notes-cleanup` 系の PR)
- CI / 内部スクリプトのみの変更
- 上記の場合は版を上げない。次回のリリース時に「(no user-facing change)」として一緒にぶら下げる。

---

## ブランチとコミット

### ブランチ命名
| プレフィックス | 用途 |
|---|---|
| `feature/<topic>` | 新機能 |
| `fix/<topic>` | バグ修正 |
| `chore/<topic>` | ドキュメント / CI / 雑務 |
| `security/<topic>` | 依存バンプ / CVE 対応 |

### コミットメッセージ
- 1 行目: 命令形の短い要約 (英語推奨)
- 必要に応じて空行を挟んで本文に「なぜそうしたか」を書く
- フッタの自動署名は付けない (今のリポジトリは個人の自分用)

---

## テスト

```powershell
dotnet test AgentLimitChecker.slnx -m:1 -nr:false --blame-hang-timeout 60s  # アプリ本体 (dotnet/)
npm test                                                                  # 発行の道具 (tests/tools/) だけ
```

- アプリ本体の検証は `dotnet test` で行う。`npm test` はアプリ本体を検証しない。
- 実際の Claude と Codex の API、FTP、GitHub の Release に接続するテストは置かない。偽物に差し替えて確かめる。

---

## アプリ稼働中の編集
- 単一インスタンスの Mutex (`Local\AgentLimitChecker.Instance`) があるので、トレイで C# 版が動いている間に起動した 2 つ目は即終了する。修正の確認で起動し直すときは、まずユーザに常駐中のアプリを終了してもらう。
- 3.x(Electron 版)と C# 版は同じ `%APPDATA%\agent-limit-checker\settings.json` と CLI の資格情報を使い、互いの単一インスタンスを検知しない。実機で試すときは Electron 版も終了しておく。
- Release 構成の exe は、設定で自動起動が有効なら、起動時に Run キー (`HKCU\...\Run` の `com.agent-limit-checker.app`) を自分の exe で登録し直す。開発者の環境を書き換えないよう、実機確認は Debug 構成(登録し直さない)で行うか、終わったら使っている exe を起動し直して戻す。

---

## リモートセッション時の作業について
この節は、~/.claude 配下(グローバル CLAUDE.md、スキル、エージェント定義、codex-agent.sh)を読めないクラウド実行のための代替である。Claude Code のローカル実行では `~/.claude/CLAUDE.md` の規則に従う。

### モデル役割分担（メインセッションとサブエージェント）
メインセッションは設計・監査・レビューに専念し、実装は Agent ツールのサブエージェントに切り出す。`~/.claude/agents/` の impl-hard / impl-standard / impl-light はリモートセッションから読めないため、区分名ではなくモデルと effort を直接指定する。

| 区分 | モデル / effort | 想定するタスク |
|---|---|---|
| hard | Opus / high | 複数ファイル・複数層にまたがる設計変更。正しさの検証が難しいロジック |
| standard（既定） | Opus / medium | 仕様が明確な機能追加や不具合修正。既存パターンに沿った実装 |
| light | Sonnet / medium | 文言・ドキュメント修正、レビュー指摘への局所的な追従、定型的なテスト追加 |

迷ったら一段上の区分に倒す。失敗したら effort を一段上げて再委譲し、それでも失敗したらメインセッションが引き取る。
次のいずれかに該当する場合はメインセッションが直接実装し、その理由を作業報告に残す。
- 仕様を依頼文に書けない
- 数行の修正で、依頼文の作成と結果の監査が実装より手間になる
- 監査で得た理解をそのまま修正に使うほうが正確
- hard で 2 回失敗した

サブエージェントへの依頼文には、目的、変更対象、完了条件(例: 指定のテストが通る、対象の全箇所を移行した)、止まって報告する条件、検証方法を書く。

### AI 相互レビュー（ai-cross-review）
相互レビューの手順の正本は [docs/cross-review.md](docs/cross-review.md)（vendored）と、グローバル SKILL `~/.claude/skills/cross-review/SKILL.md`（無い環境では vendored の [.claude/skills/cross-review/SKILL.md](.claude/skills/cross-review/SKILL.md)）である。
このリポジトリ固有のレビュー観点は `.cross-review.md` にある。
3 択、サーキットブレーカー、PR 運用といった汎用ルールはここに写さず、SKILL を参照する。

- 検証コマンド: アプリ本体は `dotnet test AgentLimitChecker.slnx -m:1 -nr:false --blame-hang-timeout 60s`、発行の道具は `npm test`（node --test）。配布物の確認は `build-release.bat`。
- 基盤の更新: `npm run sync`（検査は `npm run sync:check`、未登録の配布物は `node tools/cross-review.sync.js --check-manifest`）で上流から取り込む。更新手順は「同期 → 表示された移行ノートの作業 → 上の検証コマンド」の順。
- レビューの起点: 既定のレビュアーは実装者と別のベンダーで、実装を一区切りしたら 3 択を `AskUserQuestion` で提示する（詳細は SKILL）。指摘、対応、妥当性確認は PR コメントに残し、本文は `.cross-review/round-<N>-triage.md` を書いて `node tools/cross-review.js comment --round <N>` で生成する。
