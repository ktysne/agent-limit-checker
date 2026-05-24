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
    app.setLoginItemSettings({
      openAtLogin: !!enabled,
      args: ['--hidden'],
    });
    return true;
  } catch (err) {
    console.error('[autoLaunch] failed', err);
    return false;
  }
}

module.exports = { isEnabled, setEnabled };
