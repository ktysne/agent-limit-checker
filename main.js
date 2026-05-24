'use strict';

const { app, BrowserWindow, Tray, Menu, ipcMain, screen, shell, nativeImage } = require('electron');
const path = require('node:path');
const { spawn } = require('node:child_process');

const settingsStore = require('./src/settings');
const autoLaunch = require('./src/autoLaunch');
const claudeProvider = require('./src/claudeProvider');
const codexProvider = require('./src/codexProvider');
const { buildTrayImage } = require('./src/trayIcon');

const SINGLE_INSTANCE_LOCK = app.requestSingleInstanceLock();
if (!SINGLE_INSTANCE_LOCK) {
  app.quit();
  process.exit(0);
}

let tray = null;
let popoverWindow = null;
let pollTimer = null;
let isPolling = false;
let latestSnapshot = {
  claude: null, // {ok, data?, error?}
  codex: null,
  fetchedAt: 0,
};

function getSettings() {
  return settingsStore.load();
}

function saveSettings(partial) {
  return settingsStore.save(partial);
}

function createPopoverWindow() {
  if (popoverWindow) return popoverWindow;
  popoverWindow = new BrowserWindow({
    width: 360,
    height: 520,
    show: false,
    frame: false,
    resizable: false,
    skipTaskbar: true,
    alwaysOnTop: true,
    transparent: false,
    backgroundColor: '#1e1e1e',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });
  popoverWindow.setMenuBarVisibility(false);
  popoverWindow.loadFile(path.join(__dirname, 'renderer', 'index.html'));
  popoverWindow.on('blur', () => {
    if (popoverWindow && !popoverWindow.webContents.isDevToolsOpened()) {
      popoverWindow.hide();
    }
  });
  popoverWindow.on('closed', () => {
    popoverWindow = null;
  });
  return popoverWindow;
}

function positionWindowNearTray() {
  if (!popoverWindow || !tray) return;
  const trayBounds = tray.getBounds();
  const winBounds = popoverWindow.getBounds();
  const display = screen.getDisplayNearestPoint({ x: trayBounds.x, y: trayBounds.y });
  const workArea = display.workArea;
  let x = Math.round(trayBounds.x + trayBounds.width / 2 - winBounds.width / 2);
  let y = Math.round(trayBounds.y - winBounds.height - 8);
  if (y < workArea.y) {
    y = trayBounds.y + trayBounds.height + 8;
  }
  x = Math.max(workArea.x + 4, Math.min(workArea.x + workArea.width - winBounds.width - 4, x));
  y = Math.max(workArea.y + 4, Math.min(workArea.y + workArea.height - winBounds.height - 4, y));
  popoverWindow.setBounds({ x, y, width: winBounds.width, height: winBounds.height });
}

function togglePopover() {
  const win = createPopoverWindow();
  if (win.isVisible()) {
    win.hide();
    return;
  }
  positionWindowNearTray();
  win.show();
  win.focus();
  sendSnapshotToRenderer();
}

function utilizationFromSnapshot(svc) {
  if (!svc || !svc.ok) return null;
  if (!svc.data || !svc.data.fiveHour) return null;
  return svc.data.fiveHour.utilization;
}

function percentLabel(util) {
  if (util == null || Number.isNaN(util)) return '--%';
  if (util > 1.0) return '100%+';
  return `${Math.round(Math.max(0, util) * 100)}%`;
}

function updateTray() {
  if (!tray) return;
  const c = utilizationFromSnapshot(latestSnapshot.claude);
  const x = utilizationFromSnapshot(latestSnapshot.codex);
  try {
    tray.setImage(buildTrayImage(c, x));
  } catch (err) {
    console.error('[tray] setImage failed', err);
  }
  const tooltipLines = [
    'Agent Limit Checker',
    `Claude: ${percentLabel(c)}`,
    `Codex:  ${percentLabel(x)}`,
  ];
  tray.setToolTip(tooltipLines.join('\n'));
  rebuildTrayMenu();
}

function rebuildTrayMenu() {
  if (!tray) return;
  const c = utilizationFromSnapshot(latestSnapshot.claude);
  const x = utilizationFromSnapshot(latestSnapshot.codex);
  const menu = Menu.buildFromTemplate([
    { label: `Claude 5h: ${percentLabel(c)}`, enabled: false },
    { label: `Codex 5h:  ${percentLabel(x)}`, enabled: false },
    { type: 'separator' },
    { label: '詳細を表示', click: () => togglePopover() },
    { label: '今すぐ更新', click: () => { void refreshNow(); } },
    { type: 'separator' },
    {
      label: 'claude login (新しいターミナルで実行)',
      click: () => openLoginTerminal('claude'),
    },
    {
      label: 'codex login (新しいターミナルで実行)',
      click: () => openLoginTerminal('codex'),
    },
    { type: 'separator' },
    {
      label: 'ログイン時に自動起動',
      type: 'checkbox',
      checked: autoLaunch.isEnabled(),
      click: (menuItem) => {
        autoLaunch.setEnabled(menuItem.checked);
        saveSettings({ autoLaunch: menuItem.checked });
        sendSnapshotToRenderer();
      },
    },
    {
      label: '更新間隔',
      submenu: [
        { label: '30秒', type: 'radio', checked: getSettings().pollingIntervalSec === 30, click: () => setInterval(30) },
        { label: '1分',  type: 'radio', checked: getSettings().pollingIntervalSec === 60, click: () => setInterval(60) },
        { label: '2分',  type: 'radio', checked: getSettings().pollingIntervalSec === 120, click: () => setInterval(120) },
        { label: '5分',  type: 'radio', checked: getSettings().pollingIntervalSec === 300, click: () => setInterval(300) },
        { label: '10分', type: 'radio', checked: getSettings().pollingIntervalSec === 600, click: () => setInterval(600) },
      ],
    },
    { type: 'separator' },
    { label: '終了', click: () => quitApp() },
  ]);
  tray.setContextMenu(menu);
}

function setInterval(seconds) {
  saveSettings({ pollingIntervalSec: seconds });
  restartPolling();
  sendSnapshotToRenderer();
}

function openLoginTerminal(target) {
  // Spawn a new Windows Terminal / cmd window running the command so users can interact.
  const cmd = target === 'claude' ? 'claude login' : 'codex login';
  try {
    // start.exe opens a detached console; using cmd /k keeps it open after exit so users can read errors.
    spawn('cmd.exe', ['/c', 'start', '""', 'cmd.exe', '/k', cmd], {
      detached: true,
      stdio: 'ignore',
      windowsHide: false,
    }).unref();
  } catch (err) {
    console.error('[login] failed to spawn', err);
  }
}

function buildSnapshotForRenderer() {
  return {
    claude: latestSnapshot.claude,
    codex: latestSnapshot.codex,
    fetchedAt: latestSnapshot.fetchedAt,
    settings: getSettings(),
    autoLaunchEnabled: autoLaunch.isEnabled(),
    isPolling,
  };
}

function sendSnapshotToRenderer() {
  if (popoverWindow && !popoverWindow.isDestroyed()) {
    popoverWindow.webContents.send('snapshot', buildSnapshotForRenderer());
  }
}

async function refreshNow() {
  if (isPolling) return;
  isPolling = true;
  sendSnapshotToRenderer();

  const [claudeRes, codexRes] = await Promise.allSettled([
    claudeProvider.fetch(),
    codexProvider.fetch(),
  ]);

  latestSnapshot = {
    claude: settled(claudeRes),
    codex: settled(codexRes),
    fetchedAt: Date.now(),
  };
  isPolling = false;
  updateTray();
  sendSnapshotToRenderer();
}

function settled(res) {
  if (res.status === 'fulfilled') {
    return { ok: true, data: res.value };
  }
  const err = res.reason || {};
  return {
    ok: false,
    error: {
      code: err.code || 'unknown',
      message: err.message || String(err),
      retryAfter: err.retryAfter || null,
    },
  };
}

function restartPolling() {
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
  const seconds = getSettings().pollingIntervalSec;
  pollTimer = global.setInterval(() => {
    void refreshNow();
  }, seconds * 1000);
}

function quitApp() {
  if (pollTimer) clearInterval(pollTimer);
  try { codexProvider.shutdown(); } catch { /* ignore */ }
  try { claudeProvider.shutdown(); } catch { /* ignore */ }
  if (tray) {
    tray.destroy();
    tray = null;
  }
  app.exit(0);
}

ipcMain.handle('get-snapshot', () => buildSnapshotForRenderer());
ipcMain.handle('refresh', async () => {
  await refreshNow();
  return buildSnapshotForRenderer();
});
ipcMain.handle('set-interval', (_evt, seconds) => {
  if (settingsStore.ALLOWED_INTERVALS.includes(seconds)) {
    setInterval(seconds);
  }
  return buildSnapshotForRenderer();
});
ipcMain.handle('set-auto-launch', (_evt, enabled) => {
  autoLaunch.setEnabled(!!enabled);
  saveSettings({ autoLaunch: !!enabled });
  rebuildTrayMenu();
  return buildSnapshotForRenderer();
});
ipcMain.handle('open-login', (_evt, target) => {
  openLoginTerminal(target === 'codex' ? 'codex' : 'claude');
  return true;
});
ipcMain.handle('quit', () => { quitApp(); });

app.on('second-instance', () => {
  togglePopover();
});

app.on('window-all-closed', (e) => {
  // Stay alive in tray.
  e.preventDefault && e.preventDefault();
});

app.whenReady().then(async () => {
  // Hide from taskbar
  if (process.platform === 'win32') {
    app.setAppUserModelId('com.agent-limit-checker.app');
  }

  // Initial tray icon with no data → gray donuts.
  tray = new Tray(buildTrayImage(null, null));
  tray.setToolTip('Agent Limit Checker (起動中…)');
  tray.on('click', () => togglePopover());
  tray.on('double-click', () => togglePopover());
  rebuildTrayMenu();

  createPopoverWindow();

  await refreshNow();
  restartPolling();
});

app.on('before-quit', () => {
  try { codexProvider.shutdown(); } catch { /* ignore */ }
});
