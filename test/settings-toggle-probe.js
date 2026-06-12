'use strict';

// Electron-process probe for the settings-panel toggle + fit-to-content resize.
//   npx electron test/settings-toggle-probe.js [forcedScaleFactor]
//
// Loads the real renderer + preload, mirrors main.js's 'content-height'
// handling, then drives the ⚙ toggle and asserts:
//   1. the panel is hidden (display:none) by default,
//   2. opening it shows the panel and GROWS the window with no scrollbar,
//   3. closing it hides the panel and SHRINKS the window back, snug fit.

const path = require('node:path');
const { app, BrowserWindow, ipcMain, screen } = require('electron');

const POPOVER_WIDTH = 360;
const POPOVER_MIN_HEIGHT = 200;
const POPOVER_MAX_HEIGHT = 900;

const forced = Number(process.argv[2]);
if (Number.isFinite(forced) && forced > 0) {
  app.commandLine.appendSwitch('force-device-scale-factor', String(forced));
}
app.disableHardwareAcceleration();

const now = Date.now();
const snapshot = {
  claude: { ok: true, data: {
    plan: 'Max 5x',
    fiveHour: { utilization: 0.83, resetsAt: now + 41 * 60 * 1000 },
    weekly: { utilization: 0.18, resetsAt: now + (16 * 3600 + 60) * 1000 },
    weeklySonnet: { utilization: 0.0, resetsAt: null },
  } },
  codex: { ok: true, data: {
    plan: 'Plus',
    fiveHour: { utilization: 0.80, resetsAt: now + (3600 + 28 * 60) * 1000 },
    weekly: { utilization: 0.43, resetsAt: now + (5 * 86400 + 21 * 3600 + 39 * 60) * 1000 },
  } },
  fetchedAt: now,
  settings: { pollingIntervalSec: 600, ntfy: { topicUrl: '', accessToken: '', notifyFiveHour: false, notifyWeekly: false } },
  autoLaunchEnabled: true,
  isPolling: false, theme: 'dark', appVersion: '0.0.0-probe',
};

let win = null;
let popoverHeight = 560;

ipcMain.handle('get-snapshot', () => snapshot);
ipcMain.on('content-height', (_evt, h) => {
  const clamped = Math.max(POPOVER_MIN_HEIGHT, Math.min(POPOVER_MAX_HEIGHT, Math.round(Number(h))));
  if (!Number.isFinite(clamped) || clamped === popoverHeight) return;
  popoverHeight = clamped;
  if (win && !win.isDestroyed()) win.setContentSize(POPOVER_WIDTH, popoverHeight);
});

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const measure = (w) => w.webContents.executeJavaScript(`new Promise((res) => {
  requestAnimationFrame(() => requestAnimationFrame(() => res({
    container: document.querySelector('.container').getBoundingClientRect().height,
    innerHeight: window.innerHeight,
    scrollPx: Math.max(0, document.documentElement.scrollHeight - window.innerHeight),
    panelHidden: document.getElementById('settings-panel').hidden,
    panelDisplay: getComputedStyle(document.getElementById('settings-panel')).display,
    expanded: document.getElementById('settings-toggle').getAttribute('aria-expanded'),
  })));
})`);
const clickToggle = (w) => w.webContents.executeJavaScript(
  `document.getElementById('settings-toggle').click()`,
);

function judge(label, m, { wantPanelOpen }) {
  const gap = m.innerHeight - m.container;
  const noScroll = m.scrollPx < 0.5;
  const snugFit = gap >= -0.5 && gap <= 4;
  const panelOk = wantPanelOpen
    ? (!m.panelHidden && m.panelDisplay !== 'none' && m.expanded === 'true')
    : (m.panelHidden && m.panelDisplay === 'none' && m.expanded === 'false');
  const pass = noScroll && snugFit && panelOk;
  console.log(`[${label}] content=${m.container.toFixed(2)} innerHeight=${m.innerHeight} scrollPx=${m.scrollPx.toFixed(2)} gap=${gap.toFixed(2)} hidden=${m.panelHidden} display=${m.panelDisplay} aria-expanded=${m.expanded}`);
  console.log(`[${label}] ${pass ? 'PASS' : 'FAIL'} (${noScroll ? 'no scroll' : 'SCROLLS'}, ${snugFit ? 'snug' : 'bad gap'}, panel ${panelOk ? 'ok' : 'WRONG STATE'})`);
  return pass;
}

app.whenReady().then(async () => {
  const sf = screen.getPrimaryDisplay().scaleFactor;
  console.log(`# scaleFactor=${sf}${Number.isFinite(forced) && forced > 0 ? ' (forced)' : ''}`);

  win = new BrowserWindow({
    width: POPOVER_WIDTH, height: popoverHeight,
    minWidth: POPOVER_WIDTH, maxWidth: POPOVER_WIDTH,
    useContentSize: true, show: false, frame: false, resizable: false,
    transparent: false, backgroundColor: '#1e1e1e',
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload.js'),
      contextIsolation: true, nodeIntegration: false, sandbox: false,
    },
  });
  await win.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'));
  win.setPosition(200, 120);
  win.show();
  await wait(700);

  let allPass = true;
  const closedDefault = await measure(win);
  const closedHeight = closedDefault.innerHeight;
  allPass = judge('default closed', closedDefault, { wantPanelOpen: false }) && allPass;

  await clickToggle(win);
  await wait(700);
  const opened = await measure(win);
  allPass = judge('opened', opened, { wantPanelOpen: true }) && allPass;
  if (!(opened.innerHeight > closedHeight + 50)) {
    console.log(`[opened] FAIL: window did not grow (closed=${closedHeight}, open=${opened.innerHeight})`);
    allPass = false;
  }

  await clickToggle(win);
  await wait(700);
  const closedAgain = await measure(win);
  allPass = judge('closed again', closedAgain, { wantPanelOpen: false }) && allPass;
  if (Math.abs(closedAgain.innerHeight - closedHeight) > 4) {
    console.log(`[closed again] FAIL: window did not shrink back (was=${closedHeight}, now=${closedAgain.innerHeight})`);
    allPass = false;
  }

  console.log(`\n${allPass ? 'PASS' : 'FAIL'}`);
  win.destroy();
  app.exit(allPass ? 0 : 1);
});
