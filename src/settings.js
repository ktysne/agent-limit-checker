'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { app } = require('electron');

const DEFAULTS = {
  pollingIntervalSec: 300,
  autoLaunch: false,
};

const ALLOWED_INTERVALS = [30, 60, 120, 300, 600];

let cache = null;
let settingsPath = null;

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
    cache = { ...DEFAULTS, ...parsed };
  } catch {
    cache = { ...DEFAULTS };
  }
  if (!ALLOWED_INTERVALS.includes(cache.pollingIntervalSec)) {
    cache.pollingIntervalSec = DEFAULTS.pollingIntervalSec;
  }
  return cache;
}

function save(partial) {
  const merged = { ...load(), ...partial };
  if (!ALLOWED_INTERVALS.includes(merged.pollingIntervalSec)) {
    merged.pollingIntervalSec = DEFAULTS.pollingIntervalSec;
  }
  cache = merged;
  const p = getPath();
  try {
    fs.mkdirSync(path.dirname(p), { recursive: true });
    fs.writeFileSync(p, JSON.stringify(merged, null, 2), 'utf8');
  } catch (err) {
    console.error('[settings] failed to write', err);
  }
  return cache;
}

module.exports = { load, save, ALLOWED_INTERVALS, DEFAULTS };
