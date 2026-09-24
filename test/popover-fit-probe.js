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
// Case "ceiling-raised": clamped, then the ceiling grows (the popover moved to a
//                  taller display). Re-applying the last reported height must
//                  lift the clamp and hand the window back to the fit loop —
//                  the clamped renderer reports nothing, so only the retained
//                  height can drive this.
//
// Every phase also asserts that the height requests SETTLE: after the loop has
// had time to converge, an idle wait must add no further requests. The
// fractional-DPI shortfall of setContentSize wobbles by a px or two between
// nearby heights, which once made the loop alternate between two requests
// forever (829 ↔ 838 at 125% with three Codex accounts) — a regression that
// only shows up in this idle-count check.
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
      resetCredits: { availableCount: 2, nextExpiresAt: now + 14 * 86400 * 1000 },
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
      cloudCredit: {
        limit: 250,
        used: 1.77,
        remaining: 248.23,
        utilization: 1.77 / 250,
        expiresAt: now + 40 * 86400 * 1000,
        locked: null,
      },
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
// The last raw height the renderer reported, mirroring main.js: the renderer
// goes quiet while clamped, so re-applying a changed ceiling needs this record.
let lastReportedHeight = 560;
let contentClamped = false;
let reportCount = 0;

ipcMain.handle('get-snapshot', () => snapshot);
ipcMain.handle('set-ntfy-settings', () => snapshot);
ipcMain.handle('set-codex-account-name', () => snapshot);

// Mirror main.js reapplyPopoverHeight: re-derive the window height from the last
// reported content height against the ceiling that applies now, and mirror the
// clamp state to the renderer.
function reapplyHeight() {
  const clamped = Math.max(POPOVER_MIN_HEIGHT, Math.min(maxHeight, lastReportedHeight));
  const overCeiling = lastReportedHeight > maxHeight;
  if (overCeiling !== contentClamped) {
    contentClamped = overCeiling;
    if (win && !win.isDestroyed()) win.webContents.send('content-clamped', contentClamped);
  }
  if (clamped === popoverHeight) return;
  popoverHeight = clamped;
  if (win && !win.isDestroyed()) win.setContentSize(POPOVER_WIDTH, popoverHeight);
}

// Mirror main.js applyContentHeight: record the raw height, then re-apply it.
ipcMain.on('content-height', (_evt, h) => {
  reportCount += 1;
  const raw = Math.round(Number(h));
  if (process.env.PROBE_DEBUG) console.log(`  report#${reportCount} ${raw}`);
  if (!Number.isFinite(raw)) return;
  lastReportedHeight = raw;
  reapplyHeight();
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

async function runCase({
  name, codexCount, ceiling, openSettings, closeSettingsAfter, raiseCeilingTo,
}) {
  snapshot = makeSnapshot(codexCount);
  maxHeight = ceiling;
  popoverHeight = 560;
  lastReportedHeight = 560;
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
  // Measure the phase that follows a transition: let the loop converge, then
  // record how many requests the transition cost and whether an idle wait adds
  // any more (it must not — see the header comment on the 829 ↔ 838 loop).
  const settleAfter = async (before) => {
    await wait(700);
    const settled = reportCount;
    const m2 = await measure(win);
    await wait(400);
    return {
      ...m2,
      gap: m2.innerHeight - m2.container,
      contentSize: win.getContentSize(),
      reportsForTransition: settled - before,
      reportsAfterIdle: reportCount - settled,
    };
  };
  if (raiseCeilingTo) {
    // The popover moved to a display with a taller work area. main.js re-applies
    // the retained height on the next show; do the same here.
    const before = reportCount;
    maxHeight = raiseCeilingTo;
    win.setPosition(200, 0); // an unclamped window can outgrow the probe's start y
    reapplyHeight();
    result.after = await settleAfter(before);
  }
  if (closeSettingsAfter) {
    // Shrinking back under the ceiling must lift the clamp and hand the window
    // back to the normal fit-to-content loop.
    const before = reportCount;
    await win.webContents.executeJavaScript("document.getElementById('settings-toggle').click()");
    result.after = await settleAfter(before);
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

  // 2. Content over the ceiling (3 Codex accounts + settings panel): the
  //    window stops at the ceiling and the renderer scrolls.
  // パネルを閉じた 3 アカウント構成は収まり、開くと超える高さにする。
  const ceiling = 1100;
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
  console.log(`\n[unclamped] reports=${back.reportsForTransition} (+${back.reportsAfterIdle} idle)  contentSize=${JSON.stringify(back.contentSize)}`);
  console.log(`[unclamped] content=${back.container.toFixed(2)} innerHeight=${back.innerHeight} scrollPx=${back.scrollPx.toFixed(2)} gap=${back.gap.toFixed(2)} clampedClass=${back.clampedClass}`);
  pass = check('html.clamped cleared', !back.clampedClass) && pass;
  pass = check('no scrollbar again', back.scrollPx < 0.5) && pass;
  pass = check('snug fit again', back.gap >= -0.5 && back.gap <= 4) && pass;
  // Three Codex accounts with the panel closed is the state that used to
  // alternate between two heights forever at 125%.
  pass = check('requests bounded while shrinking', back.reportsForTransition <= 10) && pass;
  pass = check('requests settled', back.reportsAfterIdle === 0) && pass;

  // 4. The ceiling grows under a clamped popover (it moved to a taller display):
  //    the retained height must be re-applied, lifting the clamp.
  const raised = await runCase({
    name: 'ceiling-raised', codexCount: 3, ceiling, openSettings: true, raiseCeilingTo: 10_000,
  });
  const grown = raised.after;
  console.log(`\n[ceiling-raised] ceiling ${ceiling} → 10000 reports=${grown.reportsForTransition} (+${grown.reportsAfterIdle} idle)  contentSize=${JSON.stringify(grown.contentSize)}`);
  console.log(`[ceiling-raised] content=${grown.container.toFixed(2)} innerHeight=${grown.innerHeight} scrollPx=${grown.scrollPx.toFixed(2)} gap=${grown.gap.toFixed(2)} clampedClass=${grown.clampedClass}`);
  pass = check('was clamped before the ceiling rose', raised.clampedClass) && pass;
  pass = check('html.clamped cleared', !grown.clampedClass) && pass;
  pass = check('no scrollbar after the ceiling rose', grown.scrollPx < 0.5) && pass;
  pass = check('window grew past the old ceiling', grown.contentSize[1] > ceiling) && pass;
  pass = check('snug fit after the ceiling rose', grown.gap >= -0.5 && grown.gap <= 4) && pass;
  pass = check('requests bounded after the ceiling rose', grown.reportsForTransition <= 10) && pass;
  pass = check('requests settled', grown.reportsAfterIdle === 0) && pass;

  console.log(`\n${pass ? 'PASS' : 'FAIL'}`);
  app.exit(pass ? 0 : 1);
});
