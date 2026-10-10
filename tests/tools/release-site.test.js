'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const ROOT = path.resolve(__dirname, '..', '..');
const releaseSite = require('../../tools/release-site.js');
const VERSION = '4.0.0';
const RELEASED_AT = '2026-10-10';
const DOWNLOAD_URL = 'https://github.com/ktysne/agent-limit-checker/releases/download/v4.0.0/AgentLimitChecker-4.0.0-win-x64.zip';

async function withTempDir(action) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'agent-limit-checker-release-test-'));
  try {
    return await action(directory);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
}

function createReleaseFixture(directory) {
  const output = path.join(directory, 'release');
  const zipPath = path.join(output, releaseSite.zipFileName(VERSION));
  fs.mkdirSync(output, { recursive: true });
  fs.writeFileSync(zipPath, 'test zip bytes');
  const generated = releaseSite.generateFiles({
    version: VERSION,
    releasedAt: RELEASED_AT,
    out: output,
    zip: zipPath,
  });
  return { output, zipPath, ...generated };
}

test('発行版は X.Y.0 だけを受け付ける', () => {
  assert.equal(releaseSite.isValidVersion('4.0.0'), true);
  assert.equal(releaseSite.isValidVersion('4.0.1'), false);
  assert.equal(releaseSite.isValidVersion('4.1'), false);
  assert.equal(releaseSite.isValidReleasedAt(RELEASED_AT), true);
  assert.equal(releaseSite.isValidReleasedAt('2026-02-30'), false);
});

test('generate は 3 ページ、アイコン、update-v2.json だけを生成する', async () => {
  await withTempDir(directory => {
    const { output, zipPath, manifestPath, manifest } = createReleaseFixture(directory);
    assert.deepEqual(manifest, {
      schema: 2,
      latest: {
        version: VERSION,
        url: DOWNLOAD_URL,
        sha256: crypto.createHash('sha256').update('test zip bytes').digest('hex'),
        releasedAt: RELEASED_AT,
      },
    });
    assert.equal(fs.existsSync(manifestPath), true);
    assert.equal(fs.existsSync(path.join(output, 'update.json')), false);
    for (const name of ['index.html', 'manual.html', 'license.html', 'assets/app-icon-256.png']) {
      assert.equal(fs.existsSync(path.join(output, name)), true, name);
    }
    for (const name of ['index.html', 'manual.html', 'license.html']) {
      const html = fs.readFileSync(path.join(output, name), 'utf8');
      assert.equal(html.includes('{{'), false, name + ' has an unresolved placeholder');
      assert.match(html, /Agent Limit Checker/);
    }
    assert.equal(path.basename(zipPath), 'AgentLimitChecker-4.0.0-win-x64.zip');
  });
});

test('publishedVersionOf は update-v2.json の版を返す', async () => {
  const manifest = releaseSite.buildUpdateManifest(
    VERSION,
    RELEASED_AT,
    'a'.repeat(64),
  );
  const version = await releaseSite.publishedVersionOf(async url => ({
    status: 200,
    ok: true,
    json: async () => manifest,
    requestedUrl: String(url),
  }));
  assert.equal(version, VERSION);
});

test('公開 manifest の版と GitHub Release URL が一致しない場合は拒否する', async () => {
  const manifest = releaseSite.buildUpdateManifest(VERSION, RELEASED_AT, 'a'.repeat(64));
  manifest.latest.url = releaseSite.downloadUrlOf('3.9.0');
  await assert.rejects(releaseSite.publishedVersionOf(async () => ({
    status: 200,
    ok: true,
    json: async () => manifest,
  })), /形式が正しくありません/);
});

test('公開中の版と同じ版または古い版を拒否する', async () => {
  const manifest = releaseSite.buildUpdateManifest('4.1.0', RELEASED_AT, 'a'.repeat(64));
  const fetchImpl = async () => ({ status: 200, ok: true, json: async () => manifest });
  await assert.rejects(releaseSite.checkPublishedVersion('4.1.0', fetchImpl), /より大きくありません/);
  await assert.rejects(releaseSite.checkPublishedVersion('4.0.0', fetchImpl), /より大きくありません/);
  assert.equal(await releaseSite.checkPublishedVersion('4.2.0', fetchImpl), manifest.latest);
});

test('設定ファイルが無い場合は AGENT_LIMIT_CHECKER_FTP_* を使う', () => {
  const config = releaseSite.buildConfigFromEnv({
    AGENT_LIMIT_CHECKER_FTP_HOST: 'ftp.example.test',
    AGENT_LIMIT_CHECKER_FTP_USER: 'user',
    AGENT_LIMIT_CHECKER_FTP_PASSWORD: 'secret',
    AGENT_LIMIT_CHECKER_FTP_REMOTE_ROOT: '/ktysne.info/agent-limit-checker',
  });
  assert.equal(config.secure, true);
  assert.equal(config.port, 21);
  assert.equal(releaseSite.remoteRootOf(config), '/ktysne.info/agent-limit-checker');
  assert.throws(() => releaseSite.remoteRootOf({ remoteRoot: '/root/../outside' }), /絶対パス/);
});

test('設定ファイルの JSON 構文エラーに入力内容を含めない', async () => {
  await withTempDir(async directory => {
    const configPath = path.join(directory, 'deploy.config.json');
    fs.writeFileSync(configPath, '{"password": s3cr3t-dummy}', 'utf8');

    assert.throws(() => releaseSite.loadConfig(configPath, {}), error => {
      assert.match(error.message, /JSON 構文が正しくありません/);
      assert.equal(error.message.includes('s3cr3t-dummy'), false);
      return true;
    });
  });
});

test('upload --dry-run は送信予定を表示し、外部サービスに接続しない', async () => {
  await withTempDir(async directory => {
    const { output, zipPath } = createReleaseFixture(directory);
    const lines = [];
    const oldLog = console.log;
    console.log = line => lines.push(line);
    try {
      const result = await releaseSite.cmdUpload({
      version: VERSION,
      out: output,
      zip: zipPath,
      dryRun: true,
      config: path.join(directory, 'missing.json'),
    }, {
      env: {},
      fetchManifest: async () => { throw new Error('network access is not allowed'); },
      gh: async () => { throw new Error('GitHub access is not allowed'); },
      createFtpClient: () => { throw new Error('FTP access is not allowed'); },
      });
      assert.equal(result.dryRun, true);
      assert.deepEqual(result.items.map(item => item.label), [
        'assets/app-icon-256.png',
        'manual.html',
        'license.html',
        'index.html',
        'update-v2.json',
      ]);
      assert.ok(lines.some(line => line.includes('update-v2.json')));
      assert.ok(lines.some(line => line.includes('接続しません')));
      assert.match(result.configProblem, /FTPS の接続情報がありません/);
      assert.ok(lines.some(line => line.startsWith('要確認: tools/deploy.config.json')));
    } finally {
      console.log = oldLog;
    }
  });
});

test('upload --dry-run は接続情報を読めれば configProblem を返さない', async () => {
  await withTempDir(async directory => {
    const { output, zipPath } = createReleaseFixture(directory);
    const oldLog = console.log;
    console.log = () => {};
    try {
      const result = await releaseSite.cmdUpload({
        version: VERSION,
        out: output,
        zip: zipPath,
        dryRun: true,
        config: path.join(directory, 'missing.json'),
      }, {
        env: {
          AGENT_LIMIT_CHECKER_FTP_HOST: 'ftp.example.invalid',
          AGENT_LIMIT_CHECKER_FTP_USER: 'dummy-user',
          AGENT_LIMIT_CHECKER_FTP_PASSWORD: 'dummy-password',
          AGENT_LIMIT_CHECKER_FTP_REMOTE_ROOT: '/agent-limit-checker',
        },
      });
      assert.equal(result.configProblem, null);
    } finally {
      console.log = oldLog;
    }
  });
});

test('Release 作成には既存タグの確認を必須にする', async () => {
  const calls = [];
  const sha256 = 'a'.repeat(64);
  const gh = async args => {
    calls.push(args);
    return {};
  };
  const result = await releaseSite.ensureReleaseAndVerifyZip({
    version: VERSION,
    zipPath: 'AgentLimitChecker-4.0.0-win-x64.zip',
    zipSha256: sha256,
  }, {
    gh,
    releaseState: { tag: 'v4.0.0', fileName: releaseSite.zipFileName(VERSION), existing: false },
    download: async () => sha256,
    sleep: async () => {},
  });
  const create = calls.find(args => args[0] === 'release' && args[1] === 'create');
  assert.ok(create);
  assert.ok(create.includes('--verify-tag'));
  assert.equal(result.created, true);
});

test('FTP は update-v2.json を最後に送信する', async () => {
  await withTempDir(async directory => {
    const { output } = createReleaseFixture(directory);
    const files = new Map();
    const sent = [];
    const remoteRoot = '/ktysne.info/agent-limit-checker';
    let currentDirectory = '/';
    const resolveRemotePath = remotePath => path.posix.resolve(currentDirectory, remotePath);
    const client = {
      async access() {},
      async cd(remotePath) { currentDirectory = resolveRemotePath(remotePath); },
      async ensureDir(remotePath) { currentDirectory = resolveRemotePath(remotePath); },
      async uploadFrom(localPath, remoteName) {
        const remotePath = path.posix.join(currentDirectory, remoteName);
        sent.push({ directory: currentDirectory, name: remoteName });
        files.set(remotePath, { size: fs.statSync(localPath).size });
      },
      async list() {
        return [...files]
          .filter(([remotePath]) => path.posix.dirname(remotePath) === currentDirectory)
          .map(([remotePath, value]) => ({ name: path.posix.basename(remotePath), ...value }));
      },
      async rename(from, to) {
        const fromPath = path.posix.join(currentDirectory, from);
        const toPath = path.posix.join(currentDirectory, to);
        const value = files.get(fromPath);
        if (!value) throw new Error('missing temporary file');
        files.delete(fromPath);
        files.set(toPath, value);
      },
      async remove(name) { files.delete(path.posix.join(currentDirectory, name)); },
      close() {},
    };
    await releaseSite.uploadSite(releaseSite.uploadItems(output), {
      host: 'ftp.example.test',
      port: 21,
      user: 'user',
      password: 'secret',
      secure: true,
      remoteRoot,
    }, { client });
    assert.deepEqual(sent, [
      { directory: path.posix.join(remoteRoot, 'assets'), name: 'app-icon-256.png.uploading' },
      { directory: remoteRoot, name: 'manual.html.uploading' },
      { directory: remoteRoot, name: 'license.html.uploading' },
      { directory: remoteRoot, name: 'index.html.uploading' },
      { directory: remoteRoot, name: 'update-v2.json.uploading' },
    ]);
    assert.equal(files.has(path.posix.join(remoteRoot, 'assets', 'app-icon-256.png')), true);
    assert.equal(files.has(path.posix.join(remoteRoot, 'update-v2.json')), true);
    assert.equal(currentDirectory, remoteRoot);
  });
});

test('CLI は legacy manifest の引数を受け付けない', () => {
  assert.throws(() => releaseSite.parseArgs(['generate', '--version', VERSION, '--legacy-site']), /不明な引数/);
});

test('build-package.bat は検査後に発行し、タグを push してから Release を作る', () => {
  const batch = fs.readFileSync(path.join(ROOT, 'build-package.bat'), 'utf8');
  const order = [
    'git status --porcelain',
    'git show-ref --verify',
    'release-site.js check-version',
    'dotnet test AgentLimitChecker.slnx',
    'node --test tests/tools/*.test.js',
    'dotnet publish dotnet\\AgentLimitChecker.App',
    'ZipFile]::OpenRead',
    'release-site.js generate --version',
    'AGENT_LIMIT_CHECKER_TEST_MANIFEST_PATH=%CD%',
    'AGENT_LIMIT_CHECKER_TEST_MANIFEST_VERSION=%CURVER%',
    '--no-restore --filter "FullyQualifiedName~Parse_ReleaseSiteGeneratedManifest_IsAccepted"',
    'git tag -a',
    'git push origin "refs/tags/%TAG%"',
    'release-site.js upload',
  ].map(fragment => batch.indexOf(fragment));
  assert.ok(order.every(index => index >= 0));
  assert.deepEqual(order, [...order].sort((left, right) => left - right));
  assert.match(batch, /--blame-hang-timeout 60s/);
  assert.match(batch, /X\.Y\.0/);
  assert.match(batch, /Add-Type -AssemblyName System\.IO\.Compression\.FileSystem -ErrorAction Stop; \$archive=/);
});
