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
};

const ALLOWED_INTERVALS = [30, 60, 120, 300, 600];

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

function normalizeSettings(value) {
  const raw = value && typeof value === 'object' ? value : {};
  const normalized = {
    ...DEFAULTS,
    ...raw,
    ntfy: normalizeNtfy({
      ...DEFAULTS.ntfy,
      ...(raw.ntfy && typeof raw.ntfy === 'object' ? raw.ntfy : {}),
    }),
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
  _private: { normalizeNtfy, normalizeSettings },
};
