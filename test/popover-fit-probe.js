'use strict';

// Electron-process probe for the self-correcting fit-to-content popover sizing.
//   npx electron test/popover-fit-probe.js [forcedScaleFactor]
//
// Loads the real renderer + preload with the user's screenshot snapshot and the
// real sizing loop, responds to every 'content-height' report exactly like
// main.js (clamp + setContentSize), SHOWS the window so we measure at the real
// (or forced) display DPI, lets the loop converge, then asserts the content no
// longer overflows the viewport (no scrollbar) and fits snugly (no big gap).

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
    weeklySonnet: { utilization: 0.0, resetsAt: null }, // long "ウィンドウ未開始…" line
  } },
  codex: { ok: true, data: {
    plan: 'Plus',
    fiveHour: { utilization: 0.80, resetsAt: now + (3600 + 28 * 60) * 1000 },
    weekly: { utilization: 0.43, resetsAt: now + (5 * 86400 + 21 * 3600 + 39 * 60) * 1000 },
  } },
  fetchedAt: now, settings: { pollingIntervalSec: 600 }, autoLaunchEnabled: true,
  isPolling: false, theme: 'dark', appVersion: '0.0.0-probe',
};

let win = null;
let popoverHeight = 560;
let reportCount = 0;

ipcMain.handle('get-snapshot', () => snapshot);
// Mirror main.js applyContentHeight: clamp the reported height and resize.
ipcMain.on('content-height', (_evt, h) => {
  reportCount += 1;
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
  })));
})`);

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
  try {
    await win.loadFile(path.join(__dirname, '..', 'renderer', 'index.html'));
  } catch (e) { console.log('loadFile error (ignored):', e && e.message); }

  win.setPosition(200, 120);
  win.show();
  await wait(700); // let the self-correcting loop converge

  const m = await measure(win);
  const gap = m.innerHeight - m.container;
  console.log(`reports=${reportCount}  finalContentSize=${JSON.stringify(win.getContentSize())}`);
  console.log(`content=${m.container.toFixed(2)}  innerHeight=${m.innerHeight}  scrollPx=${m.scrollPx.toFixed(2)}  gap=${gap.toFixed(2)}`);

  const noScroll = m.scrollPx < 0.5;
  const snugFit = gap >= -0.5 && gap <= 4; // viewport just covers content
  const pass = noScroll && snugFit;
  console.log(`\n${pass ? 'PASS' : 'FAIL'}: ${noScroll ? 'no scrollbar' : 'STILL SCROLLS'}, ${snugFit ? 'snug fit' : 'bad gap'}`);
  win.destroy();
  app.exit(pass ? 0 : 1);
});
