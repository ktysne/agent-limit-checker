'use strict';

const test = require('node:test');
const { after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
  discoverCodexHomes, defaultCodexHome, accountDisplayName, codexLoginTargets, normalizeHomePath,
} = require('../src/codexHomes');

const tempDirs = [];

function makeTempDir(prefix) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  tempDirs.push(dir);
  return dir;
}

function writeFile(...segments) {
  const file = path.join(...segments);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, '');
  return file;
}

// A home directory holding every shape the scan has to decide about.
function makeHomeDir() {
  const homeDir = makeTempDir('codex-homes-');
  writeFile(homeDir, '.codex', 'auth.json');         // logged-in default home
  writeFile(homeDir, '.codex-review', 'config.toml'); // configured, not logged in
  fs.mkdirSync(path.join(homeDir, '.codex-empty'));   // prefix only, no marker
  writeFile(homeDir, '.codexfile');                   // a file, not a directory
  writeFile(homeDir, 'other', 'auth.json');           // marker, wrong prefix
  return homeDir;
}

after(() => {
  for (const dir of tempDirs) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('discoverCodexHomes takes only ~/.codex* directories that carry a Codex marker', () => {
  const homeDir = makeHomeDir();
  const accounts = discoverCodexHomes({ homeDir, env: {} });

  assert.deepEqual(accounts.map((a) => a.label), ['.codex', '.codex-review']);
  assert.equal(accounts[0].home, path.join(homeDir, '.codex'));
  assert.equal(accounts[0].authFile, path.join(homeDir, '.codex', 'auth.json'));
  assert.equal(accounts[0].id, normalizeHomePath(accounts[0].home));
});

test('discoverCodexHomes puts the default home first and marks it', () => {
  const homeDir = makeHomeDir();
  const accounts = discoverCodexHomes({ homeDir, env: {} });

  assert.equal(accounts[0].label, '.codex');
  assert.equal(accounts[0].isDefault, true);
  assert.equal(accounts[1].isDefault, false);
});

test('discoverCodexHomes dedupes a CODEX_HOME that the ~/.codex* scan already found', () => {
  const homeDir = makeHomeDir();
  const accounts = discoverCodexHomes({
    homeDir,
    env: { CODEX_HOME: path.join(homeDir, '.codex') },
  });

  assert.equal(accounts.filter((a) => a.label === '.codex').length, 1);
  assert.deepEqual(accounts.map((a) => a.label), ['.codex', '.codex-review']);
});

test('discoverCodexHomes includes a CODEX_HOME outside the home directory as the default', () => {
  const homeDir = makeHomeDir();
  const outside = makeTempDir('codex-elsewhere-');
  writeFile(outside, 'auth.json');

  const accounts = discoverCodexHomes({ homeDir, env: { CODEX_HOME: outside } });

  const external = accounts.find((a) => a.home === path.resolve(outside));
  assert.ok(external, 'the CODEX_HOME directory must be listed');
  assert.equal(external.isDefault, true);
  assert.equal(accounts[0].home, path.resolve(outside)); // default comes first
  // ~/.codex is still an account, just no longer the default one.
  assert.equal(accounts.find((a) => a.label === '.codex').isDefault, false);
});

test('discoverCodexHomes returns nothing when no candidate carries a marker', () => {
  const homeDir = makeTempDir('codex-bare-');
  fs.mkdirSync(path.join(homeDir, '.codex-empty'));

  assert.deepEqual(discoverCodexHomes({ homeDir, env: {} }), []);
});

test('discoverCodexHomes never throws on an unreadable home directory', () => {
  const homeDir = path.join(os.tmpdir(), 'codex-does-not-exist-12345');
  assert.deepEqual(discoverCodexHomes({ homeDir, env: {} }), []);
});

test('defaultCodexHome is ~/.codex unless CODEX_HOME says otherwise', () => {
  const homeDir = makeTempDir('codex-default-');
  assert.equal(defaultCodexHome({ homeDir, env: {} }), path.join(homeDir, '.codex'));
  assert.equal(
    defaultCodexHome({ homeDir, env: { CODEX_HOME: path.join(homeDir, '.codex-review') } }),
    path.join(homeDir, '.codex-review'),
  );
});

test('accountDisplayName labels the home only when there is more than one account', () => {
  const one = [{ label: '.codex' }];
  const two = [{ label: '.codex' }, { label: '.codex-review' }];

  assert.equal(accountDisplayName(one[0], one), 'Codex');
  assert.equal(accountDisplayName(two[0], two), 'Codex (.codex)');
  assert.equal(accountDisplayName(two[1], two), 'Codex (.codex-review)');
});

test('accountDisplayName uses the name the user set, verbatim', () => {
  const one = [{ label: '.codex' }];
  const two = [{ label: '.codex' }, { label: '.codex-review' }];
  const names = { '.codex': 'Codex Main', '.codex-review': 'レビュー用' };

  assert.equal(accountDisplayName(one[0], one, names), 'Codex Main');
  assert.equal(accountDisplayName(two[0], two, names), 'Codex Main');
  assert.equal(accountDisplayName(two[1], two, names), 'レビュー用');
});

test('accountDisplayName falls back to the default name when no name is set', () => {
  const two = [{ label: '.codex' }, { label: '.codex-review' }];

  assert.equal(accountDisplayName(two[1], two, { '.codex': 'Codex Main' }), 'Codex (.codex-review)');
  assert.equal(accountDisplayName(two[0], two, {}), 'Codex (.codex)');
  assert.equal(accountDisplayName(two[0], two, null), 'Codex (.codex)');
});

test('accountDisplayName ignores an empty or non-string name', () => {
  const one = [{ label: '.codex' }];

  assert.equal(accountDisplayName(one[0], one, { '.codex': '' }), 'Codex');
  assert.equal(accountDisplayName(one[0], one, { '.codex': '   ' }), 'Codex');
  assert.equal(accountDisplayName(one[0], one, { '.codex': 42 }), 'Codex');
});

const DEFAULT_TARGET = {
  id: 'c:/users/me/.codex', label: '.codex', home: 'C:/Users/me/.codex', isDefault: true,
};

test('codexLoginTargets keeps the account list when the default home is in it', () => {
  const accounts = [
    { id: 'c:/users/me/.codex', label: '.codex', isDefault: true },
    { id: 'c:/users/me/.codex-review', label: '.codex-review', isDefault: false },
  ];

  assert.deepEqual(codexLoginTargets(accounts, DEFAULT_TARGET), accounts);
});

test('codexLoginTargets appends the default home when no account is the default', () => {
  const accounts = [{ id: 'c:/users/me/.codex-review', label: '.codex-review', isDefault: false }];

  const targets = codexLoginTargets(accounts, DEFAULT_TARGET);

  assert.equal(targets.length, 2);
  assert.equal(targets[0], accounts[0]);
  assert.equal(targets[1].id, DEFAULT_TARGET.id);
  assert.equal(targets[1].home, DEFAULT_TARGET.home);
  // Alongside another account a bare "Codex" would not say which home it is.
  assert.equal(targets[1].displayName, 'Codex (.codex)');
  // The caller's default account object must not be mutated.
  assert.equal(DEFAULT_TARGET.displayName, undefined);
});

test('codexLoginTargets falls back to the default home alone for an empty list', () => {
  const targets = codexLoginTargets([], DEFAULT_TARGET);

  assert.equal(targets.length, 1);
  assert.equal(targets[0].id, DEFAULT_TARGET.id);
  assert.equal(targets[0].displayName, 'Codex');
  assert.deepEqual(codexLoginTargets(null, DEFAULT_TARGET), targets);
});
