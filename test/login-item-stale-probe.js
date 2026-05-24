'use strict';

// Secondary probe: how does autoLaunch.isEnabled() behave when the registry
// has a Run entry pointing at a stale (different) executable path? This is
// the situation a portable-build user hits after the temp extraction dir gets
// regenerated on relaunch.
//
//   npx electron test/login-item-stale-probe.js

const { app } = require('electron');
const { execSync } = require('node:child_process');
const path = require('node:path');

const PROBE_AUMI = 'com.agent-limit-checker.staleprobe';
app.setAppUserModelId(PROBE_AUMI);
const autoLaunch = require(path.join(__dirname, '..', 'src', 'autoLaunch'));

function regWriteStale() {
  const stalePath = 'E:\\\\Temp\\\\old-extraction-dir\\\\AgentLimitChecker.exe';
  const value = `"${stalePath}" --hidden`;
  execSync(
    `reg add "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run" /v "${PROBE_AUMI}" /t REG_SZ /d "${value.replace(/"/g, '\\"')}" /f`,
    { stdio: ['ignore', 'pipe', 'pipe'] },
  );
}

function regDelete() {
  try {
    execSync(
      `reg delete "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run" /v "${PROBE_AUMI}" /f`,
      { stdio: ['ignore', 'pipe', 'pipe'] },
    );
  } catch { /* ignore */ }
}

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

app.whenReady().then(() => {
  // Make sure no leftover Run entry exists.
  regDelete();

  // Write a stale entry pointing at a fake path (not process.execPath).
  regWriteStale();
  console.log('[after stale write]');
  console.log('  registry:', regQuery());

  const naive = app.getLoginItemSettings();
  const explicit = app.getLoginItemSettings({
    path: process.execPath,
    args: autoLaunch._private.buildArgs(),
  });

  console.log('  naive launchItems:', JSON.stringify(naive.launchItems, null, 2));
  console.log('  naive openAtLogin:', naive.openAtLogin);
  console.log('  naive willLaunch :', naive.executableWillLaunchAtLogin);
  console.log('  explicit openAtLogin:', explicit.openAtLogin);
  console.log('  autoLaunch.isEnabled():', autoLaunch.isEnabled());

  regDelete();
  app.exit(0);
});
