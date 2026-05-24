'use strict';

// Electron-process smoke test. Run with:
//   npx electron test/popover-size.smoke.js
//
// Exercises the OLD positioning pattern (round-trip getBounds → setBounds) and
// the NEW pattern (setPosition + setContentSize) and prints the window size
// after each cycle. On a 125% / 150% DPI display the OLD pattern shrinks the
// window 1-2px per cycle; the NEW pattern stays nailed at 360x520.

const { app, BrowserWindow, screen } = require('electron');

const POPOVER_WIDTH = 360;
const POPOVER_HEIGHT = 520;
const CYCLES = 15;

function makeWindow(extra) {
  return new BrowserWindow({
    width: POPOVER_WIDTH,
    height: POPOVER_HEIGHT,
    show: false,
    frame: false,
    resizable: false,
    skipTaskbar: true,
    alwaysOnTop: true,
    transparent: false,
    ...extra,
    webPreferences: { sandbox: false, contextIsolation: true, nodeIntegration: false },
  });
}

app.disableHardwareAcceleration();

app.whenReady().then(async () => {
  const primary = screen.getPrimaryDisplay();
  console.log(`# environment: scaleFactor=${primary.scaleFactor} workArea=${JSON.stringify(primary.workArea)}`);

  // --- OLD pattern: getBounds() -> setBounds() round-trip ---------------------
  const oldWin = makeWindow({});
  const oldSizes = [];
  oldWin.show(); // need to be shown so getBounds returns actual rendered size
  for (let i = 0; i < CYCLES; i++) {
    const b = oldWin.getBounds();
    oldWin.setBounds({ x: 100, y: 100, width: b.width, height: b.height });
    const after = oldWin.getBounds();
    oldSizes.push({ cycle: i + 1, width: after.width, height: after.height });
  }
  oldWin.destroy();

  // --- NEW pattern: setPosition + setContentSize ------------------------------
  const newWin = makeWindow({
    minWidth: POPOVER_WIDTH,
    minHeight: POPOVER_HEIGHT,
    maxWidth: POPOVER_WIDTH,
    maxHeight: POPOVER_HEIGHT,
    useContentSize: true,
  });
  const newSizes = [];
  newWin.show();
  for (let i = 0; i < CYCLES; i++) {
    newWin.setPosition(100, 100);
    newWin.setContentSize(POPOVER_WIDTH, POPOVER_HEIGHT);
    const after = newWin.getBounds();
    newSizes.push({ cycle: i + 1, width: after.width, height: after.height });
  }
  newWin.destroy();

  console.log('\n## OLD pattern (production bug — getBounds → setBounds round-trip)');
  console.log('cycle | width | height');
  for (const s of oldSizes) console.log(`${String(s.cycle).padStart(5)} | ${String(s.width).padStart(5)} | ${String(s.height).padStart(6)}`);

  console.log('\n## NEW pattern (this PR — setPosition + setContentSize)');
  console.log('cycle | width | height');
  for (const s of newSizes) console.log(`${String(s.cycle).padStart(5)} | ${String(s.width).padStart(5)} | ${String(s.height).padStart(6)}`);

  // ---- Assertions ------------------------------------------------------------
  const oldDelta = {
    width: oldSizes[0].width - oldSizes[oldSizes.length - 1].width,
    height: oldSizes[0].height - oldSizes[oldSizes.length - 1].height,
  };
  const newDelta = {
    width: newSizes[0].width - newSizes[newSizes.length - 1].width,
    height: newSizes[0].height - newSizes[newSizes.length - 1].height,
  };
  console.log(`\n## summary`);
  console.log(`OLD shrink over ${CYCLES} cycles: width=${oldDelta.width}px  height=${oldDelta.height}px`);
  console.log(`NEW shrink over ${CYCLES} cycles: width=${newDelta.width}px  height=${newDelta.height}px`);

  const newStable = newSizes.every((s) => s.width === newSizes[0].width && s.height === newSizes[0].height);
  console.log(`NEW stable across all cycles: ${newStable ? 'YES' : 'NO'}`);

  if (!newStable) {
    console.error('\nFAIL: new pattern is not stable — fix did not hold');
    app.exit(1);
    return;
  }
  console.log('\nPASS');
  app.exit(0);
});
