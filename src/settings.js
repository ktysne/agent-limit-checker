'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { app } = require('electron');

const DEFAULTS = {
  pollingIntervalSec: 300,
  autoLaunch: false,
  ntfy: {
    topicUrl: '',
    accessToken: '',
    notifyFiveHour: false,
    notifyWeekly: false,
  },
  // Per-account display name overrides for Codex, keyed by the account's home
  // directory name (Account.label, e.g. ".codex-sub"). The label — not the
  // absolute path — is the key because it is what the user reads in the UI and
  // because it survives a moved home directory or a renamed Windows profile.
  codexAccountNames: {},
};

const ALLOWED_INTERVALS = [30, 60, 120, 300, 600];

// A display name has to stay short enough to fit the popover header and the
// tray tooltip, so anything longer is cut rather than allowed to wrap.
const MAX_ACCOUNT_NAME_LENGTH = 40;

let cache = null;
let settingsPath = null;

function cleanString(value) {
  if (typeof value !== 'string') return '';
  return value.trim();
}

function normalizeNtfy(value) {
  const raw = value && typeof value === 'object' ? value : {};
  return {
    topicUrl: cleanString(raw.topicUrl),
    accessToken: cleanString(raw.accessToken),
    notifyFiveHour: !!raw.notifyFiveHour,
    notifyWeekly: !!raw.notifyWeekly,
  };
}

// Only `{ label: displayName }` pairs of non-empty strings survive; every other
// shape is dropped. An entry whose name is empty after trimming is discarded
// rather than stored, so "no override" has exactly one representation (the key
// is absent) and the account falls back to its default name.
function normalizeCodexAccountNames(value) {
  // An array is rejected along with every other non-map value: its indices are
  // not home directory names, so taking its entries would invent keys.
  const raw = value && typeof value === 'object' && !Array.isArray(value) ? value : {};
  const names = {};
  for (const [key, name] of Object.entries(raw)) {
    if (typeof key !== 'string' || typeof name !== 'string') continue;
    const label = key.trim();
    if (!label) continue;
    const displayName = name.trim().slice(0, MAX_ACCOUNT_NAME_LENGTH);
    if (!displayName) continue;
    names[label] = displayName;
  }
  return names;
}

function normalizeSettings(value) {
  const raw = value && typeof value === 'object' ? value : {};
  const normalized = {
    ...DEFAULTS,
    ...raw,
    ntfy: normalizeNtfy({
      ...DEFAULTS.ntfy,
      ...(raw.ntfy && typeof raw.ntfy === 'object' ? raw.ntfy : {}),
    }),
    codexAccountNames: normalizeCodexAccountNames(raw.codexAccountNames),
  };
  if (!ALLOWED_INTERVALS.includes(normalized.pollingIntervalSec)) {
    normalized.pollingIntervalSec = DEFAULTS.pollingIntervalSec;
  }
  normalized.autoLaunch = !!normalized.autoLaunch;
  return normalized;
}

function getPath() {
  if (settingsPath) return settingsPath;
  settingsPath = path.join(app.getPath('userData'), 'settings.json');
  return settingsPath;
}

function load() {
  if (cache) return cache;
  const p = getPath();
  try {
    const raw = fs.readFileSync(p, 'utf8');
    const parsed = JSON.parse(raw);
    cache = normalizeSettings(parsed);
  } catch {
    cache = normalizeSettings(DEFAULTS);
  }
  return cache;
}

function save(partial) {
  const current = load();
  const incoming = partial && typeof partial === 'object' ? partial : {};
  const merged = { ...current, ...incoming };
  if (Object.hasOwn(incoming, 'ntfy')) {
    merged.ntfy = {
      ...current.ntfy,
      ...(incoming.ntfy && typeof incoming.ntfy === 'object' ? incoming.ntfy : {}),
    };
  }
  // Merged, not replaced, so a caller can rename one account without having to
  // resend every other account's name. normalizeCodexAccountNames then drops the
  // entries whose name came in empty, which is how a rename is undone.
  if (Object.hasOwn(incoming, 'codexAccountNames')) {
    merged.codexAccountNames = {
      ...current.codexAccountNames,
      ...(incoming.codexAccountNames && typeof incoming.codexAccountNames === 'object'
        ? incoming.codexAccountNames
        : {}),
    };
  }
  cache = normalizeSettings(merged);
  const p = getPath();
  try {
    fs.mkdirSync(path.dirname(p), { recursive: true });
    fs.writeFileSync(p, JSON.stringify(cache, null, 2), 'utf8');
  } catch (err) {
    console.error('[settings] failed to write', err);
  }
  return cache;
}

module.exports = {
  load,
  save,
  ALLOWED_INTERVALS,
  DEFAULTS,
  _private: { normalizeNtfy, normalizeCodexAccountNames, normalizeSettings },
};
