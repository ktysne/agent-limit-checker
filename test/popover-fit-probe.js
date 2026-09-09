'use strict';

// Electron-process probe for the self-correcting fit-to-content popover sizing.
//   npx electron test/popover-fit-probe.js [forcedScaleFactor]
//
// Loads the real renderer + preload with the user's screenshot snapshot and the
// real sizing loop, responds to every 'content-height' report exactly like
// main.js (clamp + setContentSize + 'content-clamped'), SHOWS the window so we
// measure at the real (or forced) display DPI, and lets the loop converge.
//
// Case "fit"     : content below the ceiling → no scrollbar, snug fit.
// Case "clamped" : 3 Codex accounts + the settings panel open → content past
//                  the ceiling, so `html.clamped` turns on, the content is
//                  scrollable, and the height requests stop instead of climbing
//                  forever. Closing the panel again must lift the clamp.
//
// PROBE_DEBUG=1 prints every reported height, for diagnosing the loop.

const path = require('node:path');
const { app, BrowserWindow, ipcMain, screen } = require('electron');

const POPOVER_WIDTH = 360;
const POPOVER_MIN_HEIGHT = 200;
const POPOVER_HARD_MAX_HEIGHT = 900;

const forced = Number(process.argv[2]);
if (Number.isFinite(forced) && forced > 0) {
  app.commandLine.appendSwitch('force-device-scale-factor', String(forced));
}
app.disableHardwareAcceleration();

const now = Date.now();

function codexAccount(index) {
  const label = index === 0 ? '.codex' : `.codex-${index}`;
  return {
    id: `c:/users/me/${label}`,
    label,
    home: `C:/Users/me/${label}`,
    isDefault: index === 0,
    displayName: index === 0 ? 'Codex' : `Codex ${index}`,
    customName: null,
    defaultName: index === 0 ? 'Codex' : `Codex ${index}`,
    ok: true,
    data: {
      plan: 'Plus',
      fiveHour: { utilization: 0.80, resetsAt: now + (3600 + 28 * 60) * 1000 },
      weekly: { utilization: 0.43, resetsAt: now + (5 * 86400 + 21 * 3600 + 39 * 60) * 1000 },
    },
  };
}

function makeSnapshot(codexCount) {
  return {
    claude: { ok: true, data: {
      plan: 'Max 5x',
      fiveHour: { utilization: 0.83, resetsAt: now + 41 * 60 * 1000 },
      weekly: { utilization: 0.18, resetsAt: now + (16 * 3600 + 60) * 1000 },
      weeklyScoped: [{ label: 'Fable', utilization: 0.0, resetsAt: null }], // long "ウィンドウ未開始…" line
    } },
    codexAccounts: Array.from({ length: codexCount }, (_, i) => codexAccount(i)),
    fetchedAt: now,
    settings: { pollingIntervalSec: 600, ntfy: {} },
    autoLaunchEnabled: true,
    isPolling: false,
    theme: 'dark',
    appVersion: '0.0.0-probe',
  };
}

// Per-case state shared with the IPC handlers below (only one case runs at a time).
let win = null;
let snapshot = makeSnapshot(1);
let maxHeight = POPOVER_HARD_MAX_HEIGHT;
let popoverHeight = 560;
let contentClamped = false;
let reportCount = 0;

ipcMain.handle('get-snapshot', () => snapshot);
ipcMain.handle('set-ntfy-settings', () => snapshot);
ipcMain.handle('set-codex-account-name', () => snapshot);
// Mirror main.js applyContentHeight: clamp the reported height, tell the
// renderer whether it was clamped, and resize.
ipcMain.on('content-height', (_evt, h) => {
  reportCount += 1;
  const raw = Math.round(Number(h));
  if (process.env.PROBE_DEBUG) console.log(`  report#${reportCount} ${raw}`);
  if (!Number.isFinite(raw)) return;
  const clamped = Math.max(POPOVER_MIN_HEIGHT, Math.min(maxHeight, raw));
  const overCeiling = raw > maxHeight;
  if (overCeiling !== contentClamped) {
    contentClamped = overCeiling;
    if (win && !win.isDestroyed()) win.webContents.send('content-clamped', contentClamped);
  }
  if (clamped === popoverHeight) return;
  popoverHeight = clamped;
  if (win && !win.isDestroyed()) win.setContentSize(POPOVER_WIDTH, popoverHeight);
});

// Cases run one window at a time, so the app must survive the gap between
// destroying one and creating the next.
app.on('window-all-closed', (e) => { if (e && e.preventDefault) e.preventDefault(); });

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const measure = (w) => w.webContents.executeJavaScript(`new Promise((res) => {
  requestAnimationFrame(() => requestAnimationFrame(() => res({
    container: document.querySelector('.container').getBoundingClientRect().height,
    innerHeight: window.innerHeight,
    scrollPx: Math.max(0, document.documentElement.scrollHeight - window.innerHeight),
    clampedClass: document.documentElement.classList.contains('clamped'),
  })));
})`);

async function runCase({ name, codexCount, ceiling, openSettings, closeSettingsAfter }) {
  snapshot = makeSnapshot(codexCount);
  maxHeight = ceiling;
  popoverHeight = 560;
  contentClamped = false;
  reportCount = 0;

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
  const reportsBeforeSettings = reportCount;
  if (openSettings) {
    await win.webContents.executeJavaScript("document.getElementById('settings-toggle').click()");
    await wait(700);
  }

  const reportsAfterSettle = reportCount;
  const m = await measure(win);
  await wait(400); // nothing should keep asking for height once settled
  const result = {
    name,
    ...m,
    gap: m.innerHeight - m.container,
    contentSize: win.getContentSize(),
    reports: reportsAfterSettle,
    reportsWhileGrowing: reportsAfterSettle - reportsBeforeSettings,
    reportsAfterIdle: reportCount,
    after: null,
  };
  if (closeSettingsAfter) {
    // Shrinking back under the ceiling must lift the clamp and hand the window
    // back to the normal fit-to-content loop.
    await win.webContents.executeJavaScript("document.getElementById('settings-toggle').click()");
    await wait(700);
    const m2 = await measure(win);
    result.after = { ...m2, gap: m2.innerHeight - m2.container, contentSize: win.getContentSize() };
  }
  win.destroy();
  win = null;
  return result;
}

function check(label, ok) {
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${label}`);
  return ok;
}

app.whenReady().then(async () => {
  const sf = screen.getPrimaryDisplay().scaleFactor;
  console.log(`# scaleFactor=${sf}${Number.isFinite(forced) && forced > 0 ? ' (forced)' : ''}`);

  let pass = true;

  // 1. Content fits under the ceiling: the window converges onto it exactly.
  const fit = await runCase({ name: 'fit', codexCount: 1, ceiling: POPOVER_HARD_MAX_HEIGHT, openSettings: false });
  console.log(`\n[fit] reports=${fit.reports} (+${fit.reportsAfterIdle - fit.reports} idle)  contentSize=${JSON.stringify(fit.contentSize)}`);
  console.log(`[fit] content=${fit.container.toFixed(2)} innerHeight=${fit.innerHeight} scrollPx=${fit.scrollPx.toFixed(2)} gap=${fit.gap.toFixed(2)}`);
  pass = check('no scrollbar', fit.scrollPx < 0.5) && pass;
  pass = check('snug fit', fit.gap >= -0.5 && fit.gap <= 4) && pass;
  pass = check('html.clamped not set', !fit.clampedClass) && pass;
  pass = check('requests settled', fit.reportsAfterIdle === fit.reports) && pass;

  // 2. Content over the ceiling (3 Codex accounts + settings panel, small
  //    ceiling): the window stops at the ceiling and the renderer scrolls.
  const ceiling = POPOVER_HARD_MAX_HEIGHT;
  const clamped = await runCase({
    name: 'clamped', codexCount: 3, ceiling, openSettings: true, closeSettingsAfter: true,
  });
  console.log(`\n[clamped] ceiling=${ceiling} reports=${clamped.reports} (${clamped.reportsWhileGrowing} while opening the panel, +${clamped.reportsAfterIdle - clamped.reports} idle)  contentSize=${JSON.stringify(clamped.contentSize)}`);
  console.log(`[clamped] content=${clamped.container.toFixed(2)} innerHeight=${clamped.innerHeight} scrollPx=${clamped.scrollPx.toFixed(2)} clampedClass=${clamped.clampedClass}`);
  pass = check('content exceeds the ceiling', clamped.container > ceiling) && pass;
  pass = check('window held at the ceiling', clamped.contentSize[1] <= ceiling) && pass;
  pass = check('html.clamped set', clamped.clampedClass) && pass;
  pass = check('scrollable', clamped.scrollPx > 0.5) && pass;
  // Growing past the ceiling must cost a handful of requests, not an unbounded
  // climb, and the loop must go quiet once the clamp is in effect.
  pass = check('requests bounded while growing', clamped.reportsWhileGrowing <= 10) && pass;
  pass = check('requests settled', clamped.reportsAfterIdle === clamped.reports) && pass;

  // 3. Closing the settings panel brings the content back under the ceiling.
  const back = clamped.after;
  console.log(`\n[unclamped] content=${back.container.toFixed(2)} innerHeight=${back.innerHeight} scrollPx=${back.scrollPx.toFixed(2)} gap=${back.gap.toFixed(2)} clampedClass=${back.clampedClass}`);
  pass = check('html.clamped cleared', !back.clampedClass) && pass;
  pass = check('no scrollbar again', back.scrollPx < 0.5) && pass;
  pass = check('snug fit again', back.gap >= -0.5 && back.gap <= 4) && pass;

  console.log(`\n${pass ? 'PASS' : 'FAIL'}`);
  app.exit(pass ? 0 : 1);
});
