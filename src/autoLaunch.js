'use strict';

const { app } = require('electron');

// Electron's `getLoginItemSettings({})` (no path / args) returns
// `openAtLogin: false` even when our Run registry entry is present, because on
// Windows it requires the queried path+args to exactly match what was stored.
// Reading the result naively made the popover toggle "snap back" to OFF every
// time the user flipped it: the IPC handler returned autoLaunchEnabled=false
// from the bogus reading and the renderer hard-set the checkbox to false.
//
// Fix: ask getLoginItemSettings with the same path+args we used when writing,
// and fall back to executableWillLaunchAtLogin / launchItems so we still
// report "enabled" if the user manually edited the args or upgraded versions.
//
// In addition, for electron-builder's portable target the extracted exe lives
// under `%TEMP%\<random>\AgentLimitChecker.exe` and the random dir is wiped on
// exit. Registering that path makes the Run entry dead the moment the app
// closes. The portable launcher exposes its *own* stable path via
// `PORTABLE_EXECUTABLE_FILE`, so prefer that when present.

function getRegistrablePath() {
  if (process.env.PORTABLE_EXECUTABLE_FILE) {
    return process.env.PORTABLE_EXECUTABLE_FILE;
  }
  return process.execPath;
}

function buildArgs() {
  const args = ['--hidden'];
  if (!app.isPackaged) {
    // In dev (`electron .`) the resolved executable is electron.exe and the
    // first positional argument must be the project path so Electron knows
    // which app to run.
    args.unshift(app.getAppPath());
  }
  return args;
}

function isEnabled() {
  try {
    const settings = app.getLoginItemSettings({
      path: getRegistrablePath(),
      args: buildArgs(),
    });
    if (settings.openAtLogin === true) return true;
    // executableWillLaunchAtLogin ignores `args` and just checks that the
    // launchItem matching this executable is present and not deactivated.
    if (settings.executableWillLaunchAtLogin === true) return true;
    if (Array.isArray(settings.launchItems)) {
      return settings.launchItems.some((item) => item && item.enabled);
    }
    return false;
  } catch (err) {
    console.error('[autoLaunch] isEnabled failed', err);
    return false;
  }
}

function setEnabled(enabled) {
  try {
    app.setLoginItemSettings({
      openAtLogin: !!enabled,
      path: getRegistrablePath(),
      args: buildArgs(),
    });
    return true;
  } catch (err) {
    console.error('[autoLaunch] setEnabled failed', err);
    return false;
  }
}

module.exports = {
  isEnabled,
  setEnabled,
  _private: { buildArgs, getRegistrablePath },
};
