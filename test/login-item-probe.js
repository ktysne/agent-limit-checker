'use strict';

// Diagnostic probe: exercise app.setLoginItemSettings / getLoginItemSettings
// AND src/autoLaunch.js, reporting registry state at each step. Uses a unique
// appUserModelId so it doesn't fight the production app's single-instance
// lock.
//
//   npx electron test/login-item-probe.js

const { app } = require('electron');
const { execSync } = require('node:child_process');
const path = require('node:path');

const PROBE_AUMI = 'com.agent-limit-checker.probe';
app.setAppUserModelId(PROBE_AUMI);

const autoLaunch = require(path.join(__dirname, '..', 'src', 'autoLaunch'));

function regQuery() {
  try {
    const out = execSync(
      `reg query "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run" /v "${PROBE_AUMI}"`,
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
    );
    return out.trim().split('\n').pop().trim();
  } catch {
    return '(absent)';
  }
}

function snap(label) {
  const naive = app.getLoginItemSettings();
  const explicit = app.getLoginItemSettings({
    path: process.execPath,
    args: autoLaunch._private.buildArgs(),
  });
  console.log(`\n[${label}]`);
  console.log('  registry           :', regQuery());
  console.log('  autoLaunch.isEnabled():', autoLaunch.isEnabled());
  console.log('  naive openAtLogin  :', naive.openAtLogin);
  console.log('  naive willLaunch   :', naive.executableWillLaunchAtLogin);
  console.log('  explicit openAtLogin:', explicit.openAtLogin);
}

let failures = 0;
function expect(label, actual, expected) {
  const pass = actual === expected;
  if (!pass) {
    failures += 1;
    console.error(`  FAIL ${label}: expected ${expected}, got ${actual}`);
  }
}

app.whenReady().then(() => {
  // Make sure we start clean.
  autoLaunch.setEnabled(false);
  snap('initial (cleared)');
  expect('initial isEnabled', autoLaunch.isEnabled(), false);

  autoLaunch.setEnabled(true);
  snap('after setEnabled(true)');
  expect('isEnabled after ON', autoLaunch.isEnabled(), true);

  autoLaunch.setEnabled(false);
  snap('after setEnabled(false)');
  expect('isEnabled after OFF', autoLaunch.isEnabled(), false);

  autoLaunch.setEnabled(true);
  snap('after setEnabled(true) #2');
  expect('isEnabled after ON #2', autoLaunch.isEnabled(), true);

  // Cleanup so the probe leaves no trace.
  autoLaunch.setEnabled(false);
  snap('after final cleanup');
  expect('isEnabled after cleanup', autoLaunch.isEnabled(), false);

  if (failures > 0) {
    console.error(`\n${failures} expectation(s) failed`);
    app.exit(1);
    return;
  }
  console.log('\nPASS — toggle reads back correctly');
  app.exit(0);
});
