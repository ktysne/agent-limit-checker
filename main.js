'use strict';

const { app, BrowserWindow, Tray, Menu, ipcMain, screen, dialog, nativeTheme } = require('electron');
const path = require('node:path');
const { spawn } = require('node:child_process');

const settingsStore = require('./src/settings');
const autoLaunch = require('./src/autoLaunch');
const claudeProvider = require('./src/claudeProvider');
const codexProvider = require('./src/codexProvider');
const { buildTrayImage } = require('./src/trayIcon');
const { resolveClaudeExecutable, resolveCodexExecutable } = require('./src/cliPaths');
const logger = require('./src/logger');

const SINGLE_INSTANCE_LOCK = app.requestSingleInstanceLock();
if (!SINGLE_INSTANCE_LOCK) {
  app.quit();
  process.exit(0);
}

// Popover dimensions are locked. We must NEVER feed BrowserWindow.getBounds()
// back into setBounds(): on Windows with display scaling != 100% Electron
// rounds in device pixels and the window shrinks 1-2px every cycle.
const POPOVER_WIDTH = 360;
// 560 (not 520) so the Claude section can comfortably show:
//   service header + plan label + 5h bar + weekly bar + Sonnet weekly bar
// plus the Codex section + settings + footer without overflow at 100% DPI.
const POPOVER_HEIGHT = 560;

let tray = null;
let popoverWindow = null;
let pollTimer = null;
let isPolling = false;
let fadeTimer = null;
let latestSnapshot = {
  claude: null, // {ok, data?, error?}
  codex: null,
  fetchedAt: 0,
};

function currentTheme() {
  return nativeTheme.shouldUseDarkColors ? 'dark' : 'light';
}

function popoverBackgroundColor() {
  return nativeTheme.shouldUseDarkColors ? '#1e1e1e' : '#f7f7f7';
}

function getSettings() {
  return settingsStore.load();
}

function saveSettings(partial) {
  return settingsStore.save(partial);
}

function createPopoverWindow() {
  if (popoverWindow) return popoverWindow;
  popoverWindow = new BrowserWindow({
    width: POPOVER_WIDTH,
    height: POPOVER_HEIGHT,
    minWidth: POPOVER_WIDTH,
    minHeight: POPOVER_HEIGHT,
    maxWidth: POPOVER_WIDTH,
    maxHeight: POPOVER_HEIGHT,
    useContentSize: true,
    show: false,
    frame: false,
    resizable: false,
    skipTaskbar: true,
    alwaysOnTop: true,
    transparent: false,
    backgroundColor: popoverBackgroundColor(),
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
      hidePopover();
    }
  });
  popoverWindow.webContents.on('before-input-event', (event, input) => {
    if (input.type === 'keyDown' && input.key === 'Escape') {
      event.preventDefault();
      hidePopover();
    }
  });
  popoverWindow.on('closed', () => {
    popoverWindow = null;
  });
  return popoverWindow;
}

function clearFadeTimer() {
  if (fadeTimer) {
    clearInterval(fadeTimer);
    fadeTimer = null;
  }
}

function fadeWindowTo(win, targetOpacity, done) {
  clearFadeTimer();
  const start = typeof win.getOpacity === 'function' ? win.getOpacity() : targetOpacity;
  const steps = 6;
  let step = 0;
  fadeTimer = global.setInterval(() => {
    if (!win || win.isDestroyed()) {
      clearFadeTimer();
      return;
    }
    step += 1;
    const next = start + ((targetOpacity - start) * step) / steps;
    win.setOpacity(Math.max(0, Math.min(1, next)));
    if (step >= steps) {
      clearFadeTimer();
      if (done) done();
    }
  }, 16);
}

function hidePopover() {
  const win = popoverWindow;
  if (!win || win.isDestroyed() || !win.isVisible()) return;
  fadeWindowTo(win, 0, () => {
    if (!win.isDestroyed()) {
      win.hide();
      win.setOpacity(1);
    }
  });
}

function positionWindowNearTray() {
  if (!popoverWindow || !tray) return;
  const trayBounds = tray.getBounds();
  const display = screen.getDisplayNearestPoint({ x: trayBounds.x, y: trayBounds.y });
  const workArea = display.workArea;
  let x = Math.round(trayBounds.x + trayBounds.width / 2 - POPOVER_WIDTH / 2);
  let y = Math.round(trayBounds.y - POPOVER_HEIGHT - 8);
  if (y < workArea.y) {
    y = trayBounds.y + trayBounds.height + 8;
  }
  x = Math.max(workArea.x + 4, Math.min(workArea.x + workArea.width - POPOVER_WIDTH - 4, x));
  y = Math.max(workArea.y + 4, Math.min(workArea.y + workArea.height - POPOVER_HEIGHT - 4, y));
  // setPosition only — do NOT round-trip getBounds() through setBounds() on
  // fractional DPI displays. Also re-assert the content size every show so
  // that we recover if a previous bad cycle already shrank us.
  popoverWindow.setPosition(x, y);
  popoverWindow.setContentSize(POPOVER_WIDTH, POPOVER_HEIGHT);
}

function togglePopover() {
  const win = createPopoverWindow();
  if (win.isVisible()) {
    hidePopover();
    return;
  }
  positionWindowNearTray();
  win.setBackgroundColor(popoverBackgroundColor());
  win.setOpacity(0);
  win.show();
  win.focus();
  fadeWindowTo(win, 1);
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

function errorSummary(error) {
  const code = error && error.code;
  if (code === 'claude_credentials_missing') return 'login required';
  if (code === 'claude_unauthorized') return 'login required';
  if (code === 'claude_rate_limited') return 'rate limited';
  if (code === 'codex_cli_missing') return 'CLI missing';
  if (code === 'codex_rpc_error') return 'login required';
  if (code === 'codex_timeout' || code === 'claude_timeout') return 'timeout';
  if (code === 'claude_network') return 'network error';
  return 'error';
}

function serviceStatusLabel(name, svc) {
  if (!svc) return `${name}: 取得中`;
  if (!svc.ok) return `${name}: ${errorSummary(svc.error)}`;
  return `${name}: ${percentLabel(utilizationFromSnapshot(svc))}`;
}

function currentTrayScaleFactor() {
  try {
    if (tray) {
      const b = tray.getBounds();
      const display = screen.getDisplayNearestPoint({ x: b.x, y: b.y });
      return display.scaleFactor || 1;
    }
    return screen.getPrimaryDisplay().scaleFactor || 1;
  } catch {
    return 1;
  }
}

function updateTray() {
  if (!tray) return;
  const c = utilizationFromSnapshot(latestSnapshot.claude);
  const x = utilizationFromSnapshot(latestSnapshot.codex);
  try {
    tray.setImage(buildTrayImage(c, x, {
      scaleFactor: currentTrayScaleFactor(),
      claudeError: !!(latestSnapshot.claude && !latestSnapshot.claude.ok),
      codexError: !!(latestSnapshot.codex && !latestSnapshot.codex.ok),
    }));
  } catch (err) {
    logger.error('[tray] setImage failed', err);
  }
  const tooltipLines = [
    'Agent Limit Checker',
    serviceStatusLabel('Claude', latestSnapshot.claude),
    serviceStatusLabel('Codex', latestSnapshot.codex),
  ];
  tray.setToolTip(tooltipLines.join('\n'));
  rebuildTrayMenu();
}

function rebuildTrayMenu() {
  if (!tray) return;
  const c = utilizationFromSnapshot(latestSnapshot.claude);
  const x = utilizationFromSnapshot(latestSnapshot.codex);
  const menu = Menu.buildFromTemplate([
    { label: `Claude 5h: ${latestSnapshot.claude && !latestSnapshot.claude.ok ? errorSummary(latestSnapshot.claude.error) : percentLabel(c)}`, enabled: false },
    { label: `Codex 5h:  ${latestSnapshot.codex && !latestSnapshot.codex.ok ? errorSummary(latestSnapshot.codex.error) : percentLabel(x)}`, enabled: false },
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
        { label: '30秒', type: 'radio', checked: getSettings().pollingIntervalSec === 30, click: () => setPollingInterval(30) },
        { label: '1分',  type: 'radio', checked: getSettings().pollingIntervalSec === 60, click: () => setPollingInterval(60) },
        { label: '2分',  type: 'radio', checked: getSettings().pollingIntervalSec === 120, click: () => setPollingInterval(120) },
        { label: '5分',  type: 'radio', checked: getSettings().pollingIntervalSec === 300, click: () => setPollingInterval(300) },
        { label: '10分', type: 'radio', checked: getSettings().pollingIntervalSec === 600, click: () => setPollingInterval(600) },
      ],
    },
    { type: 'separator' },
    { label: '終了', click: () => quitApp() },
  ]);
  tray.setContextMenu(menu);
}

function setPollingInterval(seconds) {
  saveSettings({ pollingIntervalSec: seconds });
  restartPolling();
  sendSnapshotToRenderer();
}

function openLoginTerminal(target) {
  // Spawn a new Windows Terminal / cmd window running the command so users can interact.
  const exe = target === 'claude' ? resolveClaudeExecutable() : resolveCodexExecutable();
  if (!exe) {
    const name = target === 'claude' ? 'Claude Code' : 'Codex';
    const envName = target === 'claude' ? 'CLAUDE_PATH' : 'CODEX_PATH';
    const message = `${name} CLI が見つかりません。PATH に追加するか ${envName} に実行ファイルパスを設定してください。`;
    logger.warn('[login] executable missing', target);
    dialog.showErrorBox(`${name} CLI が見つかりません`, message);
    return false;
  }
  try {
    const lowered = exe.toLowerCase();
    if (process.platform === 'win32' && lowered.endsWith('.ps1')) {
      spawn('powershell.exe', ['-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', exe, 'login'], {
        detached: true,
        stdio: 'ignore',
        windowsHide: false,
      }).unref();
    } else if (process.platform === 'win32') {
      spawn('cmd.exe', ['/c', 'start', '""', 'cmd.exe', '/k', `"${exe}" login`], {
        detached: true,
        stdio: 'ignore',
        windowsHide: false,
      }).unref();
    } else {
      spawn(exe, ['login'], {
        detached: true,
        stdio: 'ignore',
      }).unref();
    }
    return true;
  } catch (err) {
    logger.error('[login] failed to spawn', err);
    return false;
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
    theme: currentTheme(),
    appVersion: app.getVersion(),
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
  logger.error('[poll] provider failed', err.code || 'unknown', err.message || String(err));
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
    setPollingInterval(seconds);
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
  return openLoginTerminal(target === 'codex' ? 'codex' : 'claude');
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
  logger.init(app.getPath('logs'));
  logger.info(`[app] ready v${app.getVersion()}`);

  // Hide from taskbar
  if (process.platform === 'win32') {
    app.setAppUserModelId('com.agent-limit-checker.app');
  }

  if (getSettings().autoLaunch) {
    autoLaunch.setEnabled(true);
  }

  // Initial tray icon with no data → gray donuts.
  tray = new Tray(buildTrayImage(null, null, { scaleFactor: currentTrayScaleFactor() }));
  tray.setToolTip('Agent Limit Checker (起動中…)');
  tray.on('click', () => togglePopover());
  tray.on('double-click', () => togglePopover());
  rebuildTrayMenu();

  createPopoverWindow();

  nativeTheme.on('updated', () => {
    if (popoverWindow && !popoverWindow.isDestroyed()) {
      popoverWindow.setBackgroundColor(popoverBackgroundColor());
    }
    sendSnapshotToRenderer();
  });

  await refreshNow();
  restartPolling();
});

app.on('before-quit', () => {
  try { codexProvider.shutdown(); } catch { /* ignore */ }
});
