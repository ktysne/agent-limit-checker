# Agent Limit Checker — 開発ガイド (運用ルール)

このファイルは、本リポジトリで作業するときに AI エージェント (Claude / Codex、またはレビュアー) が参照する運用ルールをまとめたものです。

---

## バージョニング

### 起点
- **`1.0.0` を起点**として運用する (それ以前の `0.x` は内部開発期間)。
- バージョンは `package.json` の `version` フィールドが Single Source of Truth。`app.getVersion()` 経由でアプリ内 UI / ログ / electron-builder の portable exe 名に伝播する。

### バンプ規則 (独自ルール: semver と一部異なる)
| 区分 | 例 | 何のとき |
|---|---|---|
| **major** | 1.0.0 → 2.0.0 | 大きな機能変更 (API/UX の刷新、互換性のない仕様変更、メジャー機能追加) |
| **minor** | 1.0.0 → 1.1.0 | それ以外の機能変更 / バグ修正 |
| **patch** | 1.0.0 → 1.0.1 | **使用しない** (上記 minor に集約) |

> ⚠️ `patch` は意図的に運用していません。バグフィックスでも minor を bump します (ユーザ指定)。

### リリースのやり方 (運用フロー)
PR は **version を触らずに** マージし、リリースしたいタイミングで **まとめて bump** します。

```powershell
# minor リリース (バグ修正 / 中小機能追加)
npm run release:minor

# major リリース (大きな機能変更)
npm run release:major
```

内部では `npm version <level> -m "chore: release v%s"` が走り、以下が一度に実行される:
1. `package.json` の `version` を bump
2. `chore: release v1.x.0` というコミットを作成
3. `v1.x.0` という git tag を作成

その後、リモートに push:
```powershell
git push
git push --tags
```

### PR を作るときの判断
- PR の説明に **どのレベルに該当するか** を明記すると後でリリース判断しやすい:
  - `Version impact: major (...)`
  - `Version impact: minor (feature)`
  - `Version impact: minor (bugfix)`
- ただし **PR 自身は `package.json` の `version` を変更しない**。リリース時にまとめて bump する。

### どこに version が現れるか
- `package.json` の `version`
- アプリ内: ポップオーバー footer 左側 (`v1.0.0`)
- ログ起動メッセージ (`logger.info('[app] ready', version)`)
- electron-builder 生成物のファイル名 (`AgentLimitChecker 1.0.0.exe`)
- git tag (`v1.0.0`)

### リリースしないケース
- ドキュメントのみの変更 (例: `chore/notes-cleanup` 系の PR)
- CI / 内部スクリプトのみの変更
- 上記の場合は version を bump しない。次回のリリース時に「(no user-facing change)」として一緒にぶら下げる。

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
npm test            # node --test の単体テスト (Claude/Codex provider, CLI 探索)
node smoke-test.js  # 実 API を叩いてプロバイダ単体動作確認
```

Electron ランタイムが必要な統合テスト/probe は `test/*.smoke.js` / `test/*-probe.js` に置いてあるので、必要時に個別に `npx electron test/<name>.js` で実行する。

---

## アプリ稼働中の編集
- ユーザがトレイで portable exe を動かしている間は **`requestSingleInstanceLock` のせいで `npm start` が即終了する**。修正検証で再起動が必要なときは、まずユーザに portable exe を終了してもらう。
- 自動起動の Run キーやアプリ設定 (`%APPDATA%\agent-limit-checker\settings.json`) を読み書きする調査は、別 `appUserModelId` を使った probe (`test/login-item-probe.js` など) に倣う。

---

## リモートセッション時の作業について

### AI 相互レビュー（ai-cross-review）
相互レビューの手順の正本は [docs/cross-review.md](docs/cross-review.md)（vendored）と、グローバル SKILL `~/.codex/skills/cross-review/SKILL.md`（Claude 側は `~/.claude/skills/cross-review/SKILL.md`、どちらも無い環境では vendored の [.claude/skills/cross-review/SKILL.md](.claude/skills/cross-review/SKILL.md)）である。
このリポジトリ固有のレビュー観点は `.cross-review.md` にある。
3 択、サーキットブレーカー、PR 運用といった汎用ルールはここに写さず、SKILL を参照する。

- 検証コマンド: `npm test`（node --test）。Electron の実行を伴う確認は `npx electron test/<name>.js`、配布物の確認は `npm run build`。
- 基盤の更新: `npm run sync`（検査は `npm run sync:check`、未登録の配布物は `node tools/cross-review.sync.js --check-manifest`）で上流から取り込む。更新手順は「同期 → 表示された移行ノートの作業 → 上の検証コマンド」の順。
- レビューの起点: 既定のレビュアーは実装者と別のベンダーで、実装を一区切りしたら 3 択を提示する（Codex 主導では選択 UI が無ければ本文に 3 択を明記して返信を待つ。詳細は SKILL）。指摘、対応、妥当性確認は PR コメントに残し、本文は `.cross-review/round-<N>-triage.md` を書いて `node tools/cross-review.js comment --round <N>` で生成する。
