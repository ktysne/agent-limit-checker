#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');

const ROOT = path.resolve(__dirname, '..');
const SITE_DIR = path.join(ROOT, 'site');
const DEFAULT_OUT = 'build/release';
const SITE_BASE_URL = 'https://ktysne.info/agent-limit-checker';
const RELEASE_REPO = 'ktysne/agent-limit-checker';
const MANIFEST_NAME = 'update-v2.json';
const MANIFEST_SCHEMA = 2;
const PAGE_NAMES = ['index.html', 'manual.html', 'license.html'];
const ASSET_NAMES = ['app-icon-256.png'];
const FTP_PREFIX = 'AGENT_LIMIT_CHECKER_FTP_';

function isValidVersion(value) {
  return /^\d+\.\d+\.0$/.test(String(value ?? ''));
}

function compareVersions(left, right) {
  const a = left.split('.').map(BigInt);
  const b = right.split('.').map(BigInt);
  for (let index = 0; index < 3; index += 1) {
    if (a[index] < b[index]) return -1;
    if (a[index] > b[index]) return 1;
  }
  return 0;
}

function isValidReleasedAt(value) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(String(value ?? ''))) return false;
  const date = new Date(value + 'T00:00:00Z');
  return Number.isFinite(date.getTime()) && date.toISOString().slice(0, 10) === value;
}

function localDateString(date = new Date()) {
  return [
    date.getFullYear(),
    String(date.getMonth() + 1).padStart(2, '0'),
    String(date.getDate()).padStart(2, '0'),
  ].join('-');
}

function zipFileName(version) {
  return 'AgentLimitChecker-' + version + '-win-x64.zip';
}

function downloadUrlOf(version) {
  return 'https://github.com/' + RELEASE_REPO + '/releases/download/v' + version + '/' + zipFileName(version);
}

function sha256OfFile(filePath) {
  return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
}

function readTemplate(filePath) {
  return fs.readFileSync(filePath, 'utf8').replace(/^\uFEFF/, '');
}

function renderTemplate(template, values) {
  const rendered = template.replace(/\{\{([^{}]*)\}\}/g, (token, key) => {
    if (!Object.hasOwn(values, key)) throw new Error('未対応の差し込み欄です: ' + token);
    return values[key];
  });
  if (/\{\{|\}\}/.test(rendered)) throw new Error('差し込み欄が残っています。');
  return rendered;
}

function templateValues(version, releasedAt) {
  return {
    VERSION: version,
    DOWNLOAD_URL: downloadUrlOf(version),
    RELEASED_AT: releasedAt,
    ZIP_NAME: zipFileName(version),
    SITE_URL: SITE_BASE_URL + '/',
  };
}

function validateReleaseInputs(version, releasedAt) {
  if (!isValidVersion(version)) throw new Error('バージョンは X.Y.0 で指定してください: ' + version);
  if (!isValidReleasedAt(releasedAt)) throw new Error('公開日は YYYY-MM-DD で指定してください: ' + releasedAt);
}

function writeSite(output, values) {
  for (const name of PAGE_NAMES) {
    const templatePath = path.join(SITE_DIR, name.replace('.html', '.template.html'));
    fs.writeFileSync(path.join(output, name), renderTemplate(readTemplate(templatePath), values), 'utf8');
  }
  const outputAssets = path.join(output, 'assets');
  fs.mkdirSync(outputAssets, { recursive: true });
  for (const name of ASSET_NAMES) {
    fs.copyFileSync(path.join(SITE_DIR, 'assets', name), path.join(outputAssets, name));
  }
}

function generatePages({ version, out = DEFAULT_OUT, releasedAt = localDateString() }) {
  validateReleaseInputs(version, releasedAt);
  const output = path.resolve(ROOT, out);
  fs.mkdirSync(output, { recursive: true });
  writeSite(output, templateValues(version, releasedAt));
  return output;
}

function buildUpdateManifest(version, releasedAt, sha256) {
  return {
    schema: MANIFEST_SCHEMA,
    latest: {
      version,
      url: downloadUrlOf(version),
      sha256,
      releasedAt,
    },
  };
}

function generateFiles({ version, out = DEFAULT_OUT, zip, releasedAt = localDateString() }) {
  validateReleaseInputs(version, releasedAt);
  const output = path.resolve(ROOT, out);
  const zipPath = path.resolve(ROOT, zip || path.join(out, zipFileName(version)));
  if (!fs.existsSync(zipPath)) throw new Error('zip が見つかりません: ' + zipPath);
  const zipSize = fs.statSync(zipPath).size;
  if (zipSize === 0) throw new Error('zip のサイズが 0 バイトです。');

  fs.mkdirSync(output, { recursive: true });
  const manifest = buildUpdateManifest(version, releasedAt, sha256OfFile(zipPath));
  writeSite(output, templateValues(version, releasedAt));
  const manifestPath = path.join(output, MANIFEST_NAME);
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n', 'utf8');
  return { output, manifest, manifestPath };
}

function uploadItems(output) {
  return [
    ...ASSET_NAMES.map(name => ({ label: 'assets/' + name, localPath: path.join(output, 'assets', name), remoteSubDir: '' })),
    { label: 'manual.html', localPath: path.join(output, 'manual.html'), remoteSubDir: '' },
    { label: 'license.html', localPath: path.join(output, 'license.html'), remoteSubDir: '' },
    { label: 'index.html', localPath: path.join(output, 'index.html'), remoteSubDir: '' },
    { label: MANIFEST_NAME, localPath: path.join(output, MANIFEST_NAME), remoteSubDir: '' },
  ];
}

function verifyReleaseInputs(version, zipPath, output) {
  if (!isValidVersion(version)) throw new Error('バージョンは X.Y.0 で指定してください: ' + version);
  const missing = [zipPath, ...uploadItems(output).map(item => item.localPath)].filter(file => !fs.existsSync(file));
  if (missing.length) throw new Error('発行するファイルがありません: ' + missing.join(', '));
  const zipSize = fs.statSync(zipPath).size;
  if (zipSize === 0) throw new Error('zip のサイズが 0 バイトです。');

  const manifest = JSON.parse(fs.readFileSync(path.join(output, MANIFEST_NAME), 'utf8'));
  const expectedUrl = downloadUrlOf(version);
  const zipSha256 = sha256OfFile(zipPath);
  if (manifest.schema !== MANIFEST_SCHEMA
      || manifest.latest?.version !== version
      || manifest.latest?.url !== expectedUrl
      || manifest.latest?.sha256 !== zipSha256
      || !isValidReleasedAt(manifest.latest?.releasedAt)) {
    throw new Error(MANIFEST_NAME + ' が zip または指定した版と一致しません。');
  }
  return { zipSha256, manifest };
}

function validatePublishedManifest(manifest) {
  if (manifest?.schema !== MANIFEST_SCHEMA
      || !isValidVersion(manifest.latest?.version)
      || manifest.latest.url !== downloadUrlOf(manifest.latest.version)
      || !/^[0-9a-f]{64}$/.test(manifest.latest?.sha256 ?? '')
      || !isValidReleasedAt(manifest.latest?.releasedAt)) {
    throw new Error('公開中の update-v2.json の形式が正しくありません。');
  }
  return manifest.latest;
}

async function publishedManifest(fetchImpl = globalThis.fetch) {
  const response = await fetchImpl(SITE_BASE_URL + '/' + MANIFEST_NAME, { headers: { accept: 'application/json' } });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error('公開中の update-v2.json を取得できません (HTTP ' + response.status + ')');
  let manifest;
  try {
    manifest = await response.json();
  } catch {
    throw new Error('公開中の update-v2.json を JSON として読めません。');
  }
  return validatePublishedManifest(manifest);
}

async function publishedVersionOf(fetchImpl = globalThis.fetch) {
  return (await publishedManifest(fetchImpl))?.version ?? null;
}

async function checkPublishedVersion(version, fetchImpl = globalThis.fetch) {
  const latest = await publishedManifest(fetchImpl);
  if (latest && compareVersions(version, latest.version) <= 0) {
    throw new Error('新しいバージョン ' + version + ' は公開中のバージョン ' + latest.version + ' より大きくありません。');
  }
  return latest;
}

function buildConfigFromEnv(env) {
  const required = ['HOST', 'USER', 'PASSWORD', 'REMOTE_ROOT'];
  const missing = required
    .map(name => FTP_PREFIX + name)
    .filter(name => !env[name]);
  if (missing.length) throw new Error('FTPS の接続情報がありません: ' + missing.join(', '));

  const port = env[FTP_PREFIX + 'PORT'] ? Number(env[FTP_PREFIX + 'PORT']) : 21;
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(FTP_PREFIX + 'PORT は 1〜65535 の整数で指定してください。');
  }
  const secureText = env[FTP_PREFIX + 'SECURE'];
  const secure = !(secureText === 'false' || secureText === '0');
  let secureOptions;
  if (env[FTP_PREFIX + 'SECURE_OPTIONS']) {
    try {
      secureOptions = JSON.parse(env[FTP_PREFIX + 'SECURE_OPTIONS']);
    } catch {
      throw new Error(FTP_PREFIX + 'SECURE_OPTIONS を JSON として読めません。');
    }
  }
  return {
    host: env[FTP_PREFIX + 'HOST'],
    port,
    user: env[FTP_PREFIX + 'USER'],
    password: env[FTP_PREFIX + 'PASSWORD'],
    secure,
    remoteRoot: env[FTP_PREFIX + 'REMOTE_ROOT'],
    secureOptions,
  };
}

function loadConfig(configPath = path.join(ROOT, 'tools', 'deploy.config.json'), env = process.env) {
  if (fs.existsSync(configPath)) {
    let config;
    try {
      config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
    } catch (error) {
      throw new Error('tools/deploy.config.json を JSON として読めません: ' + error.message);
    }
    const merged = {
      host: config.host,
      port: config.port ?? 21,
      user: config.user,
      password: config.password,
      secure: config.secure !== false,
      remoteRoot: config.remoteRoot,
      secureOptions: config.secureOptions ?? undefined,
    };
    if (merged.host && merged.user && merged.password && merged.remoteRoot) return merged;
  }
  return buildConfigFromEnv(env);
}

function remoteRootOf(config) {
  const root = String(config.remoteRoot ?? '').replaceAll('\\', '/').replace(/\/+/g, '/').replace(/\/$/, '');
  if (!root.startsWith('/') || root === '/' || root.split('/').some(part => part === '.' || part === '..')) {
    throw new Error('remoteRoot はルートからの絶対パスで指定してください。');
  }
  return root;
}

function runGh(args, execFileAsync = promisify(execFile)) {
  return execFileAsync('gh', args, { encoding: 'utf8', windowsHide: true, maxBuffer: 10 * 1024 * 1024 })
    .catch(error => { throw new Error(error.stderr?.trim() || error.message); });
}

function parseGhJson(result, description) {
  try {
    return JSON.parse(typeof result === 'string' ? result : result.stdout);
  } catch {
    throw new Error(description + ' の結果を JSON として読めません。');
  }
}

async function hashRemoteFile(url, fetchImpl = globalThis.fetch) {
  const response = await fetchImpl(url, { redirect: 'follow' });
  if (!response.ok || !response.body) {
    throw new Error('公開 URL から zip を取得できません (HTTP ' + response.status + ')');
  }
  const hash = crypto.createHash('sha256');
  for await (const chunk of response.body) hash.update(chunk);
  return hash.digest('hex');
}

async function verifyPublishedZip(url, expectedSha256, {
  download = hashRemoteFile,
  sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)),
} = {}) {
  let lastError;
  for (let attempt = 1; attempt <= 6; attempt += 1) {
    try {
      const actual = await download(url);
      if (actual !== expectedSha256) throw new Error('公開 URL の SHA-256 が手元の zip と一致しません。');
      return actual;
    } catch (error) {
      lastError = error;
      if (attempt < 6) await sleep(5000);
    }
  }
  throw new Error('公開 zip の SHA-256 を 6 回照合できませんでした: ' + (lastError?.message ?? lastError));
}

async function withNamedZip(zipPath, fileName, action) {
  if (path.basename(zipPath) === fileName) return action(zipPath);
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'agent-limit-checker-release-'));
  try {
    const namedPath = path.join(tempDir, fileName);
    fs.copyFileSync(zipPath, namedPath);
    return await action(namedPath);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
}

async function inspectRelease(version, gh) {
  const tag = 'v' + version;
  const fileName = zipFileName(version);
  await gh(['auth', 'status']);
  const releases = parseGhJson(await gh([
    'release', 'list', '--repo', RELEASE_REPO, '--limit', '1000', '--json', 'tagName,isDraft',
  ]), 'Release 一覧');
  if (!Array.isArray(releases)) throw new Error('Release 一覧の形式が正しくありません。');
  const matching = releases.find(release => release.tagName === tag);
  if (!matching) return { tag, fileName, existing: false };
  if (matching.isDraft) {
    throw new Error(tag + ' の Release が下書きのままです。gh release delete ' + tag + ' --repo ' + RELEASE_REPO + ' --yes で削除してください。');
  }
  const release = parseGhJson(await gh([
    'release', 'view', tag, '--repo', RELEASE_REPO, '--json', 'assets',
  ]), tag + ' Release');
  if (!Array.isArray(release.assets) || !release.assets.some(asset => asset.name === fileName)) {
    throw new Error(tag + ' Release に ' + fileName + ' がありません。既存 Release は変更しません。');
  }
  return { tag, fileName, existing: true };
}

async function downloadedReleaseSha256(releaseState, gh) {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'agent-limit-checker-release-asset-'));
  try {
    await gh([
      'release', 'download', releaseState.tag, '--repo', RELEASE_REPO, '--pattern', releaseState.fileName, '--dir', tempDir,
    ]);
    const filePath = path.join(tempDir, releaseState.fileName);
    if (!fs.existsSync(filePath)) throw new Error(releaseState.tag + ' Release の zip を取得できません。');
    return sha256OfFile(filePath);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
}

async function ensureReleaseAndVerifyZip({ version, zipPath, zipSha256 }, dependencies = {}) {
  const gh = dependencies.gh ?? (args => runGh(args));
  const releaseState = dependencies.releaseState ?? await inspectRelease(version, gh);
  if (releaseState.existing) {
    const existingSha = await downloadedReleaseSha256(releaseState, gh);
    if (existingSha !== zipSha256) {
      throw new Error(releaseState.tag + ' Release の zip は手元のファイルと SHA-256 が異なります。');
    }
  } else {
    await withNamedZip(zipPath, releaseState.fileName, assetPath => gh([
      'release', 'create', releaseState.tag, assetPath,
      '--repo', RELEASE_REPO,
      '--title', 'Agent Limit Checker ' + releaseState.tag,
      '--notes', '配布 zip: ' + releaseState.fileName + '\n\nこの zip には AgentLimitChecker.exe、manual.html、license.html が含まれます。',
      '--verify-tag',
    ]));
  }
  await verifyPublishedZip(downloadUrlOf(version), zipSha256, dependencies);
  return { tag: releaseState.tag, created: !releaseState.existing };
}

async function listCurrentDirectory(client) {
  const entries = await client.list();
  return new Map(entries.map(entry => [entry.name, entry]));
}

async function replaceRemoteFile(client, temporaryName, finalName, finalExists) {
  try {
    await client.rename(temporaryName, finalName);
    return;
  } catch (renameError) {
    if (!finalExists) throw renameError;
  }

  const previousName = finalName + '.previous';
  try { await client.remove(previousName); } catch {}
  await client.rename(finalName, previousName);
  try {
    await client.rename(temporaryName, finalName);
  } catch (error) {
    try {
      await client.rename(previousName, finalName);
    } catch (restoreError) {
      throw new Error(finalName + ' を置き換えられず、元のファイルも戻せませんでした: ' + restoreError.message);
    }
    throw new Error(finalName + ' を置き換えられなかったため、元のファイルへ戻しました: ' + error.message);
  }
  try { await client.remove(previousName); } catch {}
}

async function uploadRemoteItem(client, item) {
  const temporaryName = item.remoteName + '.uploading';
  await client.uploadFrom(item.localPath, temporaryName);
  const entries = await listCurrentDirectory(client);
  if (entries.get(temporaryName)?.size !== item.localSize) {
    throw new Error(temporaryName + ' のサイズがローカルと一致しないため公開しません。');
  }
  await replaceRemoteFile(client, temporaryName, item.remoteName, entries.has(item.remoteName));
}

async function uploadSite(items, config, dependencies = {}) {
  const client = dependencies.client ?? new (dependencies.Client ?? require('basic-ftp').Client)(30_000);
  const remoteRoot = remoteRootOf(config);
  const accessOptions = {
    host: config.host,
    port: config.port,
    user: config.user,
    password: config.password,
    secure: config.secure,
  };
  if (config.secureOptions) accessOptions.secureOptions = config.secureOptions;

  try {
    await client.access(accessOptions);
    await client.cd('/');
    await client.ensureDir(remoteRoot);
    for (const item of items) {
      item.remoteName = path.basename(item.localPath);
      item.localSize = fs.statSync(item.localPath).size;
      await uploadRemoteItem(client, item);
      console.log('  送信しました: ' + item.label);
    }
  } finally {
    client.close();
  }
}

async function cmdGenerate(opt) {
  const { output, manifest } = generateFiles(opt);
  console.log('[release-site] v' + opt.version + ' の配布ファイルを生成しました。');
  for (const name of [...PAGE_NAMES, ...ASSET_NAMES.map(name => path.join('assets', name)), MANIFEST_NAME]) {
    const filePath = path.join(output, name);
    console.log('  ' + path.relative(ROOT, filePath) + ' (' + fs.statSync(filePath).size + ' bytes)');
  }
  console.log('  ダウンロード URL: ' + manifest.latest.url);
}

function cmdGeneratePages(opt) {
  const output = generatePages(opt);
  console.log('[release-site] v' + opt.version + ' のページを生成しました。');
  for (const name of [...PAGE_NAMES, ...ASSET_NAMES.map(name => path.join('assets', name))]) {
    console.log('  ' + path.relative(ROOT, path.join(output, name)));
  }
}

async function cmdCheckVersion(opt, dependencies = {}) {
  if (!isValidVersion(opt.version)) throw new Error('バージョンは X.Y.0 で指定してください: ' + opt.version);
  const latest = await checkPublishedVersion(opt.version, dependencies.fetchImpl ?? globalThis.fetch);
  if (latest) console.log('公開中: ' + latest.version + ' / 発行予定: ' + opt.version);
  else console.log('初回リリース: ' + opt.version);
}

function formatSize(bytes) {
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
  return (bytes / 1024 / 1024).toFixed(2) + ' MB';
}

async function cmdUpload(opt, dependencies = {}) {
  const output = path.resolve(ROOT, opt.out ?? DEFAULT_OUT);
  const zipPath = path.resolve(ROOT, opt.zip || path.join(opt.out ?? DEFAULT_OUT, zipFileName(opt.version)));
  const { zipSha256 } = verifyReleaseInputs(opt.version, zipPath, output);
  const items = uploadItems(output);
  if (opt.dryRun) {
    let remoteRoot = null;
    try {
      remoteRoot = remoteRootOf(loadConfig(opt.config, dependencies.env ?? process.env));
    } catch {}
    console.log('FTPS 送信計画 (remoteRoot: ' + (remoteRoot ?? '(未設定)') + '):');
    for (const item of items) {
      const size = fs.statSync(item.localPath).size;
      console.log('  ' + item.label + ' (' + formatSize(size) + ')');
    }
    console.log('  GitHub Release: v' + opt.version + ' / ' + zipFileName(opt.version));
    console.log('--dry-run のため、GitHub、配布サイト、FTP サーバへ接続しません。');
    return { dryRun: true, items };
  }

  await checkPublishedVersion(opt.version, dependencies.fetchManifest ?? globalThis.fetch);
  const config = dependencies.config ?? loadConfig(opt.config, dependencies.env ?? process.env);
  const gh = dependencies.gh ?? (args => runGh(args));
  const releaseState = await inspectRelease(opt.version, gh);
  await ensureReleaseAndVerifyZip({ version: opt.version, zipPath, zipSha256 }, {
    gh,
    releaseState,
    ...(dependencies.download ? { download: dependencies.download } : {}),
    ...(dependencies.sleep ? { sleep: dependencies.sleep } : {}),
  });
  await uploadSite(items, config, dependencies);
  console.log('  update-v2.json を最後に公開しました。');
  return { dryRun: false, items };
}

function parseArgs(argv) {
  const command = argv[0];
  const options = {};
  for (let index = 1; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--dry-run') {
      options.dryRun = true;
      continue;
    }
    if (!['--version', '--released-at', '--out', '--zip', '--config'].includes(arg)) {
      throw new Error('不明な引数です: ' + arg);
    }
    const value = argv[index + 1];
    if (!value || value.startsWith('--')) throw new Error(arg + ' の値がありません。');
    options[arg.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value;
    index += 1;
  }
  return { command, ...options };
}

function printUsage() {
  console.log([
    '使い方:',
    '  node tools/release-site.js generate --version X.Y.0 [--released-at YYYY-MM-DD] [--out build/release] [--zip path]',
    '  node tools/release-site.js generate-pages --version X.Y.0 [--released-at YYYY-MM-DD] [--out artifacts/site-stage]',
    '  node tools/release-site.js check-version --version X.Y.0',
    '  node tools/release-site.js published-version',
    '  node tools/release-site.js upload --version X.Y.0 [--out build/release] [--zip path] [--dry-run]',
  ].join('\n'));
}

async function main(argv = process.argv.slice(2)) {
  const opt = parseArgs(argv);
  if (opt.command === 'generate') return cmdGenerate(opt);
  if (opt.command === 'generate-pages') return cmdGeneratePages(opt);
  if (opt.command === 'check-version') return cmdCheckVersion(opt);
  if (opt.command === 'published-version') {
    console.log((await publishedVersionOf()) ?? '');
    return;
  }
  if (opt.command === 'upload') return cmdUpload(opt);
  printUsage();
  process.exitCode = 1;
}

if (require.main === module) {
  main().catch(error => {
    console.error('[release-site] ' + error.message);
    process.exitCode = 1;
  });
}

module.exports = {
  SITE_BASE_URL,
  RELEASE_REPO,
  MANIFEST_NAME,
  zipFileName,
  downloadUrlOf,
  isValidVersion,
  isValidReleasedAt,
  compareVersions,
  buildUpdateManifest,
  generatePages,
  generateFiles,
  verifyReleaseInputs,
  uploadItems,
  buildConfigFromEnv,
  loadConfig,
  remoteRootOf,
  publishedManifest,
  publishedVersionOf,
  checkPublishedVersion,
  verifyPublishedZip,
  inspectRelease,
  ensureReleaseAndVerifyZip,
  replaceRemoteFile,
  uploadRemoteItem,
  uploadSite,
  cmdUpload,
  parseArgs,
  main,
};
