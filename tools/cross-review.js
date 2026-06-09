#!/usr/bin/env node
// tools/cross-review.js
//
// Claude ↔ Codex の相互レビューを 1 コマンドで回す CLI ブリッジ。
// 「片方の AI で実装 → もう片方の AI でレビュー」を、チャットログを手でコピーせず、
// git の差分を直接レビュアー CLI へ渡して実行する。
//
// 使い方:
//   node tools/cross-review.js codex            # 現在のブランチ (main との差分) を Codex がレビュー
//   node tools/cross-review.js claude           # 同上を Claude がレビュー
//   node tools/cross-review.js subagent         # 外部 CLI を起動せずレビュープロンプトを stdout に出す (リモートコントロール用)
//   node tools/cross-review.js codex --fix      # Codex がレビューに加え検出事項を直接修正 (作業ツリー編集)
//   node tools/cross-review.js codex --fix --instructions notes.md  # レビュアーの指摘 (notes.md) を渡して Codex に修正させる
//   node tools/cross-review.js codex --uncommitted    # 未コミットの作業ツリー差分をレビュー
//   node tools/cross-review.js claude --base develop  # 比較先ブランチを変更
//   npm run review:codex                        # = node tools/cross-review.js codex
//   npm run review:codex:fix                    # = node tools/cross-review.js codex --fix
//   npm run review:claude -- --uncommitted      # npm 経由で追加引数を渡す (-- が必要)
//
// 設計判断:
// - 依存パッケージを追加しない (Node 標準 API のみ)。
// - codex / claude いずれも「観点 + スコープ + 差分本文 + モード別指示」を stdin で渡し、
//   汎用 `codex exec` / `claude -p` を使う。codex の専用サブコマンド `codex exec review` は
//   v0.137.0 で `--uncommitted` / `--base` が `[PROMPT]` と排他になり、プロジェクト固有の
//   チェックリスト (.cross-review.md) を同時に渡せなくなったため、汎用 exec + 自前の差分埋め込みに統一した。
// - レビューのみ (既定) は codex を `-s read-only` で起動し、ファイルを書き換えさせない。
//   `--fix` 指定時のみ codex を `-s workspace-write` で起動し、検出事項を作業ツリーへ直接
//   修正させる (相互レビューフローの 3B「レビュー + 修正を依頼」)。claude CLI 経路 (claude -p) の
//   自動修正は未対応 (--fix は codex / subagent のみ)。
// - リモートコントロール環境では codex/claude スタンドアロン CLI を spawn できない。その場合は
//   reviewer に `subagent` を指定すると、外部プロセスを起動せず、組み立てたレビュープロンプト
//   (観点 + スコープ + 差分本文 + モード別指示) を stdout に出すだけにする。呼び出し側 (Claude) が
//   その出力を Agent ツールの客観レビュー用サブエージェントへ渡してレビューさせる
//   (Codex の代わりに「Claude の客観的な観点を持つサブエージェント」がレビュアーになる)。
//   プロンプト組み立て・観点解決・差分収集は codex/claude 経路と同一なので観点が揺れない。
// - プロジェクト固有のレビュー観点は、リポジトリ直下の `.cross-review.md` を単一ソースとして
//   読み込み、各レビュアー (codex / claude / subagent) へ同じチェックリストとして添える (無ければ汎用観点 GENERIC_CHECKLIST へ
//   フォールバック)。この CLI は engine 部分が完全に汎用なので、他リポへ `tools/cross-review.js` を
//   コピーし `.cross-review.md` を置くだけで観点を差し替えて再利用できる。観点を更新したら
//   `.cross-review.md` を直す。
// - レビュアー個別の「申し送り・重点指摘」は `--instructions <path>` で渡す。これは観点
//   (.cross-review.md) を置き換えず、それに加えてプロンプトへ添える別系統。一方のレビュアーが
//   出した指摘をファイルに書き、`codex --fix --instructions <path>` で他方に直接修正させる用途を
//   一級でサポートする (この目的で CROSS_REVIEW_CHECKLIST を流用すると観点が消えるため非推奨)。
// - claude の --uncommitted は Codex の --uncommitted (staged+unstaged+untracked) と結果を
//   揃えるため、tracked 変更 (git diff HEAD) に加えて未追跡ファイルも new file 差分として含める。
// - 前提: codex / claude レビュアーは「スタンドアロン CLI」が PATH にあること
//   (VS Code プラグイン / デスクトップアプリとは別物)。CLI レビューはレビュアー CLI が
//   ネットワーク/API 接続を使うため、必要に応じてサンドボックス無効・ネットワーク許可で起動する
//   (cross-review フロー ドキュメント参照)。
//   subagent レビュアーは外部 CLI を起動しない (プロンプトを stdout に出すだけ) ので CLI 不要。

'use strict';

const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

// レビュー観点はプロジェクト固有なので、リポジトリ直下の `.cross-review.md` を単一ソースとして
// 読み込む。この CLI を他リポへコピーしても `.cross-review.md` を置くだけで観点を差し替えられる。
// 解決順は loadChecklist 参照: 環境変数 CROSS_REVIEW_CHECKLIST(パス) → <cwd>/.cross-review.md →
// <スクリプト>/../.cross-review.md → GENERIC_CHECKLIST。このリポジトリの観点は `.cross-review.md` にある。
const CHECKLIST_FILENAME = '.cross-review.md';

// `.cross-review.md` が無いときの汎用フォールバック観点。特定リポに依存しない一般論のみ。
const GENERIC_CHECKLIST = [
  'あなたはこのリポジトリのコードレビュアーです。',
  '以下の差分を日本語でレビューし、各指摘に重大度 (blocker / 要修正 / 提案) を付けてください。',
  '問題が無ければその旨も明記してください。',
  '',
  '一般的な観点:',
  '- 正当性: ロジック誤り・境界条件・null/undefined・例外処理やエラーハンドリングの漏れ。',
  '- 回帰: 差分に現れていない既存挙動を壊していないか。',
  '- 後方互換: 永続化フォーマット / 公開 API / 設定スキーマの互換性を壊していないか。',
  '- テスト・lint: 変更に見合うテストがあるか、lint / 型チェック / ビルドを通る変更か。',
  '- スコープ: 無関係なリファクタや不要な変更が混ざっていないか。',
  '',
  '(プロジェクト固有の観点は、リポジトリ直下に .cross-review.md を置くと自動で添付されます。)',
].join('\n');

// レビュー観点を解決する。解決順は:
//   1. 環境変数 CROSS_REVIEW_CHECKLIST (パス)
//   2. <cwd>/.cross-review.md            (npm run review:* の通常経路。cwd はパッケージ直下)
//   3. <スクリプト>/../.cross-review.md   (tools/cross-review.js の 1 つ上 = リポジトリ直下。
//                                          cwd がリポ直下でなくても絶対パス等で起動すれば観点を拾える)
//   4. GENERIC_CHECKLIST                  (どれも無ければ汎用観点。起動時に stderr へ警告)
// deps で env / cwd / scriptDir / fs / 警告出力を差し替え可能にする (テストで再現するため)。
function loadChecklist(deps = {}) {
  const env = deps.env || process.env;
  const cwd = deps.cwd || process.cwd();
  const scriptDir = deps.scriptDir || __dirname;
  const readFile = deps.readFile || ((p) => fs.readFileSync(p, 'utf8'));
  const exists = deps.exists || ((p) => fs.existsSync(p));
  const warn = deps.warn || ((m) => process.stderr.write(m));
  const envPath = env.CROSS_REVIEW_CHECKLIST || '';
  const candidates = [];
  if (envPath) candidates.push(envPath);
  candidates.push(path.join(cwd, CHECKLIST_FILENAME));
  candidates.push(path.join(scriptDir, '..', CHECKLIST_FILENAME));
  const seen = new Set();
  for (const p of candidates) {
    if (seen.has(p)) continue; // cwd == リポ直下のときに同一パスを二重判定しない。
    seen.add(p);
    try {
      if (exists(p)) {
        const body = readFile(p).replace(/\s+$/, '');
        if (body.trim()) return body;
      }
    } catch {
      // 読めなければ次の候補 / フォールバックへ進む。
    }
    // 明示指定した CROSS_REVIEW_CHECKLIST が解決できなかったら、黙ってフォールバックせず警告する
    // (誤ったパス / 空ファイルで意図しない観点になる運用事故を検知しやすくするため)。
    if (envPath && p === envPath) {
      warn(`[cross-review] CROSS_REVIEW_CHECKLIST=${envPath} を読めません (存在しない/空/読取不可)。他の候補にフォールバックします。\n`);
    }
  }
  warn(`[cross-review] ${CHECKLIST_FILENAME} が見つかりません。汎用観点でレビューします。\n`);
  return GENERIC_CHECKLIST;
}

// `--instructions <path>` のファイル本文を読む。観点 (.cross-review.md) とは別系統で、
// 「レビュアーからの申し送り・重点指摘」をプロンプトへ追加で添えるためのもの。
// 読めなければ例外を投げ、呼び出し側 (runReview) がエラー終了させる。
// deps.readFile でテストから差し替え可能。
function loadInstructions(instructionsPath, deps = {}) {
  const readFile = deps.readFile || ((p) => fs.readFileSync(p, 'utf8'));
  return String(readFile(instructionsPath)).replace(/\s+$/, '');
}

// レビューのみ (既定) の追加指示。ファイルを変更させない。
const REVIEW_ONLY_INSTRUCTION = [
  '【モード: レビューのみ】',
  'ファイルは変更しないでください。指摘のみを重大度 (blocker / 要修正 / 提案) 付きで列挙し、',
  '問題が無ければその旨を明記してください。',
].join('\n');

// --fix の追加指示。検出事項を作業ツリーへ直接修正させる (相互レビューフロー 3B)。
const FIX_INSTRUCTION = [
  '【モード: レビュー + 修正】',
  '検出した問題は、作業ツリーのファイルを直接編集して修正してください。',
  '- 修正は差分に現れた変更へのフィードバックに限定し、無関係なリファクタはしない。',
  '- レビュー観点 (.cross-review.md) に挙げた禁則・不変条件を壊さない。',
  '- 仕様判断・設計選択などユーザの確認が要る事項は修正せず、指摘として残す。',
  '- 最後に「修正したファイルと内容・理由」「未修正で残した指摘」を日本語で要約する。',
  '構文チェック / lint / テスト / プロジェクト固有の整合性チェックは呼び出し側が後で実行する。',
].join('\n');

// `--instructions <path>` で渡された「レビュアーからの申し送り・重点指摘」をプロンプトへ
// 添えるときの見出し。観点 (.cross-review.md) を置き換えず、それに加える位置づけ。
// 主用途: 一方のレビュアーが出した指摘を他方に渡して `--fix` で直接修正させる。
const REVIEWER_NOTES_HEADER = [
  '【レビュアーからの申し送り・重点指摘】',
  '以下は、もう一方のレビュアー (Claude 等) が既に検出した重点事項です。',
  'レビュー時は最優先で検証し、--fix 時はこれらの修正を最優先で行ってください。',
  '（仕様判断・設計選択が要る事項は修正せず指摘として残すルールは従来どおり）',
].join('\n');

const USAGE = [
  'Claude ↔ Codex 相互レビュー CLI ブリッジ',
  '',
  '使い方: node tools/cross-review.js <codex|claude|subagent> [options]',
  '',
  'レビュアー:',
  '  codex      codex スタンドアロン CLI でレビュー (既定 read-only、--fix で workspace-write)',
  '  claude     claude スタンドアロン CLI でレビュー (claude -p)',
  '  subagent   外部 CLI を起動せず、レビュープロンプトを stdout に出力 (リモートコントロール用)。',
  '             codex/claude CLI を spawn できない環境向け。出力を Agent ツールの客観レビュー用',
  '             サブエージェントへ渡してレビューさせる。--fix も可 (FIX 指示付きで出力)。',
  '',
  'options:',
  '  --fix                 修正まで依頼 (codex: workspace-write で直接編集 / subagent: FIX 指示付きで出力。claude CLI 経路は非対応)',
  '  --uncommitted         未コミットの作業ツリー差分 (tracked + untracked) をレビュー',
  '  --base <ref>          比較先ブランチを指定 (既定: main)',
  '  --instructions <path> レビュアーからの申し送り・重点指摘ファイルをプロンプトへ添付',
  '                        (観点 .cross-review.md は置き換えず追加。--fix と併用で指摘を直接修正させる)',
  '  -h, --help            このヘルプを表示',
  '',
  'レビュー観点: リポジトリ直下の .cross-review.md を読み込みます',
  '  (環境変数 CROSS_REVIEW_CHECKLIST でパス指定可。スクリプト位置からも解決。無ければ汎用観点)。',
  '',
  '例:',
  '  npm run review:codex',
  '  npm run review:codex:fix',
  '  npm run review:claude -- --uncommitted',
  '  node tools/cross-review.js codex --base develop',
  '  node tools/cross-review.js codex --fix --uncommitted --instructions notes.md',
  '      (レビュー指摘 notes.md を渡し、未コミット差分を Codex に修正させる)',
  '  node tools/cross-review.js subagent --uncommitted',
  '      (リモートコントロール用: 未コミット差分のレビュープロンプトを stdout に出力し、',
  '       Agent ツールの客観サブエージェントへ渡す)',
].join('\n');

// process.argv.slice(2) を受け取り、レビュアーと差分スコープを解釈する。
function parseArgs(argv) {
  const args = argv.slice();
  const out = { reviewer: null, mode: 'base', baseRef: 'main', fix: false, instructionsPath: null, help: false, error: null };
  const rest = [];
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a === '-h' || a === '--help') {
      out.help = true;
    } else if (a === '--fix') {
      out.fix = true;
    } else if (a === '--uncommitted') {
      out.mode = 'uncommitted';
    } else if (a === '--instructions') {
      const v = args[i + 1];
      if (!v || v.startsWith('-')) {
        out.error = '--instructions にはファイルパスが必要です';
      } else {
        out.instructionsPath = v;
        i++;
      }
    } else if (a.startsWith('--instructions=')) {
      const v = a.slice('--instructions='.length);
      if (!v) {
        out.error = '--instructions にはファイルパスが必要です'; // `--instructions=` 空値は黙って素通りさせない。
      } else {
        out.instructionsPath = v;
      }
    } else if (a === '--base') {
      const v = args[i + 1];
      if (!v || v.startsWith('-')) {
        out.error = '--base にはブランチ名が必要です';
      } else {
        out.baseRef = v;
        i++;
      }
    } else if (a.startsWith('--base=')) {
      out.baseRef = a.slice('--base='.length);
    } else if (a.startsWith('-')) {
      out.error = `不明なオプション: ${a}`;
    } else {
      rest.push(a);
    }
  }
  if (!out.help && !out.error) {
    out.reviewer = rest[0] || null;
    // reviewer: codex / claude は外部スタンドアロン CLI を起動する。subagent は外部 CLI を起動せず、
    // 組み立てたレビュープロンプトを stdout に出すだけ (リモートコントロール環境で codex/claude CLI を
    // spawn できないとき、その出力を Claude が Agent ツールの客観レビュー用サブエージェントへ渡す)。
    if (out.reviewer !== 'codex' && out.reviewer !== 'claude' && out.reviewer !== 'subagent') {
      out.error = `レビュアーは codex / claude / subagent を指定してください (指定: ${out.reviewer || 'なし'})`;
    } else if (out.fix && out.reviewer === 'claude') {
      // --fix は作業ツリーを書き換える。codex は workspace-write、subagent は FIX 指示付きプロンプトを出して
      // 書込権限付きサブエージェントに直させる、で対応する。claude CLI 経路 (claude -p) のみ自動修正を未配線。
      out.error = '--fix は codex か subagent のみ対応です (claude CLI 経路の自動修正は未対応)';
    }
  }
  return out;
}

// codex exec に渡す引数。プロンプト (観点 + 差分 + モード別指示) は末尾 '-' で stdin から読ませる。
// レビューのみは read-only でファイルを保護し、--fix のときだけ workspace-write で
// 検出事項を作業ツリーへ直接修正させる。専用サブコマンド `review` は v0.137.0 で
// `--uncommitted`/`--base` が [PROMPT] と排他になり観点チェックリストを渡せないため使わない。
// `-c approval_policy=never`: codex exec は元々非対話 (既定 approval=never) だが、ユーザの
// config.toml が on-request 等でも Claude からの自走が承認待ちで止まらないよう明示的に固定する。
function codexExecArgs(opts) {
  return [
    'exec',
    '-s', opts.fix ? 'workspace-write' : 'read-only',
    '-c', 'approval_policy=never',
    '-',
  ];
}

// git を実行し stdout を返す。テストから差し替えられるよう実体を分離する。
// git diff --no-index は差分があると exit 1 を返すので allowDiffExit で許容する。
function defaultGitRunner(args, opts) {
  const allowDiffExit = !!(opts && opts.allowDiffExit);
  const res = spawnSync('git', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  const ok = res.status === 0 || (allowDiffExit && res.status === 1);
  if (!ok) {
    const detail = res.stderr || `exit ${res.status}`;
    throw new Error(`git ${args.join(' ')} に失敗しました: ${detail}`);
  }
  return res.stdout || '';
}

// claude へ渡す差分本文を組み立てる。gitRun は (args, { allowDiffExit }) => stdout の関数。
// uncommitted では Codex の --uncommitted と揃えるため、tracked 変更に加えて
// 未追跡 (untracked) ファイルも new file 差分として含める。
function collectReviewDiff(opts, gitRun) {
  if (opts.mode !== 'uncommitted') {
    return gitRun(['diff', `${opts.baseRef}...HEAD`], { allowDiffExit: true }).replace(/\n$/, '');
  }
  // --instructions の申し送りファイルが untracked のままリポジトリ内に置かれていても、
  // レビュー対象 (実コード差分) に紛れ込まないよう除外する。絶対パスで突き合わせる。
  // Windows はパスが大文字小文字を区別しないため、比較時のみ lowercase 正規化して取りこぼさない。
  const normCmp = (p) => (process.platform === 'win32' ? path.resolve(p).toLowerCase() : path.resolve(p));
  const excludeAbs = opts.instructionsPath ? normCmp(opts.instructionsPath) : null;
  const parts = [];
  const tracked = gitRun(['diff', 'HEAD'], { allowDiffExit: true });
  if (tracked.trim()) parts.push(tracked.replace(/\n$/, ''));
  const listed = gitRun(['ls-files', '--others', '--exclude-standard', '-z'], {});
  const untracked = listed.split('\0').filter(Boolean);
  for (const file of untracked) {
    if (excludeAbs && normCmp(file) === excludeAbs) continue; // 申し送りファイル自体は対象外。
    // /dev/null は git が全 OS で空ファイルとして解釈する。差分ありで exit 1 になる。
    const added = gitRun(['diff', '--no-index', '--', '/dev/null', file], { allowDiffExit: true });
    if (added.trim()) parts.push(added.replace(/\n$/, ''));
  }
  return parts.join('\n');
}

// レビュアーへ渡すプロンプト (観点 + スコープ + モード別指示 + 申し送り + 差分本文)。
// codex / claude / subagent 共通 (codex/claude は stdin、subagent は stdout へ出す)。
// opts.fix で「修正まで依頼」と「レビューのみ」を切り替える。
// checklist は loadChecklist() の戻り値 (未指定/空なら GENERIC_CHECKLIST を使う)。
// instructions は --instructions の本文 (任意)。観点とは別に「重点指摘」として添える。
function buildReviewPrompt(diffText, opts, checklist, instructions) {
  const reviewPoints = (checklist != null && String(checklist).trim())
    ? checklist
    : GENERIC_CHECKLIST;
  const scope = opts.mode === 'uncommitted'
    ? '未コミットの作業ツリー差分 (tracked: git diff HEAD ＋ untracked 新規ファイル)'
    : `現在のブランチと ${opts.baseRef} の差分 (git diff ${opts.baseRef}...HEAD)`;
  const parts = [
    reviewPoints,
    '',
    `レビュー対象: ${scope}`,
    '必要なら作業ディレクトリのファイルを読んで文脈を補ってください。',
    '',
    opts.fix ? FIX_INSTRUCTION : REVIEW_ONLY_INSTRUCTION,
  ];
  if (instructions != null && String(instructions).trim()) {
    parts.push('', REVIEWER_NOTES_HEADER, String(instructions).trim());
  }
  parts.push('', '--- DIFF START ---', diffText, '--- DIFF END ---');
  return parts.join('\n');
}

// reviewer / fix から「起動コマンド・引数・端末通知」を決める純粋関数。
// 主変更点 (どのレビュアーをどのサンドボックスで呼ぶか) を spawn 抜きで検証できるよう、
// runReview の配線部分を分離する。stdin に渡すプロンプトは prompt をそのまま使う。
function reviewerInvocation(opts) {
  if (opts.reviewer === 'codex') {
    return {
      cmd: 'codex',
      args: codexExecArgs(opts),
      notice: opts.fix
        ? 'Codex にレビュー + 検出事項の修正を依頼します (作業ツリーを編集します)...\n'
        : 'Codex でレビューを実行します...\n',
    };
  }
  if (opts.reviewer === 'subagent') {
    // 外部 CLI を起動しない (emit:true)。組み立てたレビュープロンプトを stdout に出すだけで、
    // 実際のレビューは呼び出し側 (Claude) が Agent ツールで起動する客観サブエージェントが行う。
    // リモートコントロール環境 (codex/claude CLI を spawn できない) のフォールバック。
    return {
      emit: true,
      notice: opts.fix
        ? 'リモートコントロール用: レビュー + 修正プロンプトを stdout に出力します (書込権限付きの客観サブエージェントへ渡してください)。\n'
        : 'リモートコントロール用: レビュープロンプトを stdout に出力します (客観サブエージェントへ渡してください)。\n',
    };
  }
  return { cmd: 'claude', args: ['-p'], notice: 'Claude でレビューを実行します...\n' };
}

function isPathLikeCommand(cmd) {
  return path.isAbsolute(cmd) || cmd.includes('/') || cmd.includes('\\');
}

function isDirectWindowsExecutable(cmd) {
  return ['.exe', '.com'].includes(path.extname(cmd).toLowerCase());
}

function isWindowsShellWrapper(cmd) {
  return ['.cmd', '.bat'].includes(path.extname(cmd).toLowerCase());
}

function defaultWindowsCommandLookup(cmd) {
  const res = spawnSync('where.exe', [cmd], { encoding: 'utf8' });
  if (res.status !== 0 || !res.stdout) return [];
  return res.stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
}

// Windows では PowerShell の Get-Command が .ps1 shim を返すことがある一方、
// where.exe では実行可能な .exe も見えることがある。直接起動できる .exe/.com を優先し、
// 見つからない場合だけ shell 経由にフォールバックする。これにより claude.exe 環境では
// shell:true と args の組み合わせに対する Node の警告を避けられる。
function resolveReviewerCommandForSpawn(cmd, deps = {}) {
  const platform = deps.platform || process.platform;
  if (platform !== 'win32') return { cmd, shell: false };
  if (isPathLikeCommand(cmd)) {
    return { cmd, shell: !isDirectWindowsExecutable(cmd) };
  }

  const lookup = deps.lookup || defaultWindowsCommandLookup;
  const candidates = lookup(cmd);
  const direct = candidates.find(isDirectWindowsExecutable);
  if (direct) return { cmd: direct, shell: false };

  const shellWrapper = candidates.find(isWindowsShellWrapper);
  if (shellWrapper) return { cmd: shellWrapper, shell: true };

  // .ps1 は cmd.exe では直接実行できないため、このフォールバックは環境依存。
  // npm グローバルインストールは通常 .cmd shim も作るので、実運用ではまずそこを使う。
  return { cmd: candidates[0] || cmd, shell: true };
}

// レビュアー CLI を起動し、stdin にプロンプトを流し込む。出力は端末へそのまま流す。
// Windows では起動直前に where.exe で実体を解決し、可能なら shell を使わずに起動する。
function spawnReviewer(cmd, args, stdinText) {
  const resolved = resolveReviewerCommandForSpawn(cmd);
  const child = spawn(resolved.cmd, args, {
    stdio: ['pipe', 'inherit', 'inherit'],
    // Windows では .cmd/.bat shim のことがあるため、その場合だけ shell 経由にする。
    shell: resolved.shell,
  });
  child.on('error', (err) => {
    if (err && err.code === 'ENOENT') {
      process.stderr.write(`${cmd} CLI が見つかりません。PATH に ${cmd} を通してください。\n`);
    } else {
      process.stderr.write(`${cmd} の起動に失敗しました: ${err && err.message}\n`);
    }
    process.exitCode = 1;
  });
  child.on('exit', (code) => {
    process.exitCode = code == null ? 1 : code;
  });
  if (child.stdin) {
    child.stdin.write(stdinText);
    child.stdin.end();
  }
  return child;
}

// deps で gitRun / spawnFn を差し替え可能にする (テストから stdin 本文まで検証するため)。
// 既定は実 git / 実 spawn。codex 経路でも「観点 + 差分本文 + モード指示」を stdin に渡すのが
// 中核なので、その配線を結合テストで固定できるようにする。
function runReview(opts, deps = {}) {
  const gitRun = deps.gitRun || defaultGitRunner;
  const spawnFn = deps.spawnFn || spawnReviewer;
  // 人向け通知は writeErr、機械が拾う本文 (subagent のプロンプト) は writeOut に分離する。
  // deps.out / deps.err で差し替え可能にし、subagent 経路の stdout 本文をテストから検証する。
  const writeOut = deps.out || ((s) => process.stdout.write(s));
  const writeErr = deps.err || ((s) => process.stderr.write(s));
  // 観点は deps.checklist 指定があれば優先、無ければ .cross-review.md / 汎用観点を解決する。
  const checklist = deps.checklist != null ? deps.checklist : loadChecklist(deps);
  // 申し送り (--instructions) は観点とは別系統。deps.instructions 指定があれば優先、
  // 無ければ opts.instructionsPath からファイルを読む (読めなければエラー終了)。
  let instructions = deps.instructions != null ? deps.instructions : null;
  if (instructions == null && opts.instructionsPath) {
    try {
      instructions = loadInstructions(opts.instructionsPath, deps);
    } catch (err) {
      writeErr(`--instructions のファイルを読めません: ${opts.instructionsPath} (${(err && err.message) || 'read error'})\n`);
      process.exitCode = 1;
      return null;
    }
  }
  // どのレビュアー (codex / claude / subagent) も自前で差分を取り出し、プロンプトへ同梱する (untracked も含める)。
  let diffText;
  try {
    diffText = collectReviewDiff(opts, gitRun).trim();
  } catch (err) {
    writeErr(`${(err && err.message) || 'git の実行に失敗しました'}\n`);
    process.exitCode = 1;
    return null;
  }
  if (!diffText) {
    // subagent は stdout を「サブエージェントへ渡すプロンプト」専用にするため、差分なしの通知も
    // stderr 側へ出す (stdout は空のまま保ち、空通知をプロンプトと誤認させない)。codex/claude は従来どおり stdout。
    (opts.reviewer === 'subagent' ? writeErr : writeOut)('レビュー対象の差分がありません。\n');
    return null;
  }
  const prompt = buildReviewPrompt(diffText, opts, checklist, instructions);
  const inv = reviewerInvocation(opts);
  if (inv.emit) {
    // subagent: 外部プロセスを起動せず、組み立てたプロンプト本文だけを stdout に出す。
    // 通知は stderr に分けて、stdout を「そのまま客観サブエージェントへ渡せるプロンプト」に保つ。
    writeErr(inv.notice);
    writeOut(prompt + '\n');
    return null;
  }
  writeOut(inv.notice);
  return spawnFn(inv.cmd, inv.args, prompt);
}

function main() {
  const opts = parseArgs(process.argv.slice(2));
  if (opts.help) {
    process.stdout.write(USAGE + '\n');
    return;
  }
  if (opts.error) {
    process.stderr.write(`${opts.error}\n\n${USAGE}\n`);
    process.exitCode = 2;
    return;
  }
  runReview(opts);
}

module.exports = {
  parseArgs,
  codexExecArgs,
  reviewerInvocation,
  collectReviewDiff,
  buildReviewPrompt,
  runReview,
  loadChecklist,
  loadInstructions,
  resolveReviewerCommandForSpawn,
  CHECKLIST_FILENAME,
  GENERIC_CHECKLIST,
  REVIEW_ONLY_INSTRUCTION,
  FIX_INSTRUCTION,
  REVIEWER_NOTES_HEADER,
};

if (require.main === module) main();
