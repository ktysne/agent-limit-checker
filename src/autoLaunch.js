'use strict';

const { app } = require('electron');

function isEnabled() {
  try {
    return app.getLoginItemSettings().openAtLogin === true;
  } catch {
    return false;
  }
}

function setEnabled(enabled) {
  try {
    const args = ['--hidden'];
    if (!app.isPackaged) {
      args.unshift(app.getAppPath());
    }
    app.setLoginItemSettings({
      openAtLogin: !!enabled,
      path: process.execPath,
      args,
    });
    return true;
  } catch (err) {
    console.error('[autoLaunch] failed', err);
    return false;
  }
}

module.exports = { isEnabled, setEnabled };
