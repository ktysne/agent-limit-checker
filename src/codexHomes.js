'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

// Codex supports several accounts side by side by pointing CODEX_HOME at a
// different directory per account (`~/.codex`, `~/.codex-review`, …). We scan
// the user's home directory for those directories and treat each one as a
// separate account.
const CANDIDATE_PREFIX = '.codex';

// A candidate directory only counts as a Codex home when it holds one of these
// files, because both are written by the Codex CLI itself: `codex login`
// persists the credentials in auth.json, and the CLI reads/writes config.toml.
// Requiring one of them keeps unrelated dot-directories that merely share the
// prefix (empty leftovers, backups) out of the account list — spawning an
// app-server against one of those could only ever fail, so showing it as an
// account would be pure noise.
const HOME_MARKERS = ['auth.json', 'config.toml'];

// Windows paths are case-insensitive, so `C:\Users\me\.codex` and
// `C:\users\me\.CODEX` are the same home and must dedupe to one account.
// Everywhere else the path is taken as-is.
function normalizeHomePath(home) {
  const absolute = path.resolve(String(home));
  return process.platform === 'win32' ? absolute.toLowerCase() : absolute;
}

// The home the Codex CLI itself would use: CODEX_HOME when set, `~/.codex`
// otherwise. This is the account we fall back to whenever no specific home is
// named.
function defaultCodexHome({ homeDir = os.homedir(), env = process.env } = {}) {
  const configured = env && env.CODEX_HOME;
  if (configured) return String(configured);
  return path.join(homeDir, '.codex');
}

function isDirectory(target) {
  try {
    // statSync (not lstatSync): a symlink that points at a real Codex home is a
    // legitimate way to keep the account directory elsewhere.
    return fs.statSync(target).isDirectory();
  } catch {
    return false; // unreadable / missing — not a usable home
  }
}

function hasHomeMarker(home) {
  for (const marker of HOME_MARKERS) {
    try {
      if (fs.statSync(path.join(home, marker)).isFile()) return true;
    } catch {
      // missing / unreadable marker — keep looking
    }
  }
  return false;
}

function toAccount(home, defaultKey) {
  const resolved = path.resolve(home);
  const key = normalizeHomePath(resolved);
  return {
    id: key,
    label: path.basename(resolved),
    home: resolved,
    authFile: path.join(resolved, 'auth.json'),
    isDefault: key === defaultKey,
  };
}

// Every Codex account configured on this machine, most-default first.
//
// Invariants callers rely on:
//   * never throws — an unreadable home directory or entry is skipped, and an
//     empty array is a valid answer (the caller decides what to show then);
//   * each home appears at most once, even when CODEX_HOME points at a
//     directory that the `~/.codex*` scan already found;
//   * the default home, when present, is the first element.
function discoverCodexHomes({ homeDir = os.homedir(), env = process.env } = {}) {
  const candidates = [];
  try {
    for (const entry of fs.readdirSync(homeDir)) {
      if (!entry.startsWith(CANDIDATE_PREFIX)) continue;
      candidates.push(path.join(homeDir, entry));
    }
  } catch {
    // Home directory unreadable — CODEX_HOME below may still give us an account.
  }
  // CODEX_HOME may point outside the home directory, so it is a candidate in
  // its own right.
  const configured = env && env.CODEX_HOME;
  if (configured) candidates.push(String(configured));

  const defaultKey = normalizeHomePath(defaultCodexHome({ homeDir, env }));
  const byKey = new Map();
  for (const candidate of candidates) {
    const account = toAccount(candidate, defaultKey);
    if (byKey.has(account.id)) continue;
    if (!isDirectory(account.home)) continue;
    if (!hasHomeMarker(account.home)) continue;
    byKey.set(account.id, account);
  }

  return [...byKey.values()].sort((a, b) => {
    if (a.isDefault !== b.isDefault) return a.isDefault ? -1 : 1;
    return a.label.localeCompare(b.label);
  });
}

// The name the UI shows for one account.
//
// `names` is settings.codexAccountNames (`{ [label]: displayName }`). A name the
// user set wins outright and is used verbatim — it is not wrapped in
// "Codex (...)", because the point of renaming is to choose the whole heading.
// Without an override: with a single account the home directory name carries no
// information, so the UI keeps saying plain "Codex"; only a multi-account setup
// needs the label to tell the accounts apart.
function accountDisplayName(account, accounts, names) {
  const label = account && account.label;
  if (!label) return 'Codex';
  if (names && typeof names === 'object') {
    const custom = names[label];
    if (typeof custom === 'string' && custom.trim()) return custom;
  }
  if (!Array.isArray(accounts) || accounts.length <= 1) return 'Codex';
  return `Codex (${label})`;
}

// The homes a `codex login` action may target, in the order the UI should offer
// them: every discovered account first, then `defaultAccount` appended when none
// of them is the default home.
//
// The default home is appended only to the LOGIN list, never merged into the
// account list itself: a setup that deliberately keeps `~/.codex` unused would
// otherwise carry a permanent "home missing" entry in the usage sections. Login
// is the one action that must stay reachable even for a home that does not exist
// yet, because running it is what creates the home.
//
// The appended entry gets the default display name resolved against the whole
// target list, so alongside other accounts it reads "Codex (.codex)" instead of
// a bare "Codex" that no longer tells the homes apart.
function codexLoginTargets(accounts, defaultAccount) {
  const list = (Array.isArray(accounts) ? accounts : []).filter(Boolean);
  if (!defaultAccount) return list;
  if (list.some((account) => account.isDefault)) return list;
  const targets = [...list, defaultAccount];
  return targets.map((account) => (account === defaultAccount
    ? { ...account, displayName: accountDisplayName(account, targets, null) }
    : account));
}

module.exports = {
  discoverCodexHomes,
  defaultCodexHome,
  accountDisplayName,
  codexLoginTargets,
  normalizeHomePath,
};
