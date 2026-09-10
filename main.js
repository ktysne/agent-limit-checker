'use strict';

const { app, BrowserWindow, Tray, Menu, ipcMain, screen, dialog, nativeTheme } = require('electron');
const path = require('node:path');
const fsp = require('node:fs/promises');
const { spawn } = require('node:child_process');

const settingsStore = require('./src/settings');
const autoLaunch = require('./src/autoLaunch');
const claudeProvider = require('./src/claudeProvider');
const codexProvider = require('./src/codexProvider');
const {
  discoverCodexHomes, defaultCodexHome, accountDisplayName, codexLoginTargets, normalizeHomePath,
} = require('./src/codexHomes');
const { NtfyResetNotifier } = require('./src/ntfyNotifier');
const { buildTrayImage } = require('./src/trayIcon');
const { resolveClaudeExecutable, resolveCodexExecutable } = require('./src/cliPaths');
const { buildLoginPsCommand, buildSilentPsCommand } = require('./src/loginCommand');
const logger = require('./src/logger');

const SINGLE_INSTANCE_LOCK = app.requestSingleInstanceLock();
if (!SINGLE_INSTANCE_LOCK) {
  app.quit();
  process.exit(0);
}

// Popover WIDTH is locked; HEIGHT follows the rendered content. We must NEVER
// feed BrowserWindow.getBounds() back into setBounds(): on Windows with display
// scaling != 100% Electron rounds in device pixels and the window shrinks 1-2px
// every cycle (see NOTES section H). The height instead comes from the renderer
// over the 'content-height' IPC: it watches whether the content actually
// overflows the viewport and grows the request until it fits (see NOTES section
// I — setContentSize lands a few px short at fractional DPI, so a single
// measured value isn't enough). We just clamp and apply whatever it asks for;
// the renderer's loop converges, and we never round-trip getBounds().
const POPOVER_WIDTH = 360;
// First-paint height, used only until the renderer reports its measured content
// height. Sized to comfortably hold the tallest Claude layout (header + plan
// label + 5h + weekly + Sonnet weekly bars) so the very first frame never
// scrolls before the fit-to-content resize lands.
const POPOVER_DEFAULT_HEIGHT = 560;
// Clamp the renderer-reported height so a measurement glitch can never blow the
// window up or collapse it to nothing.
const POPOVER_MIN_HEIGHT = 200;
const POPOVER_HARD_MAX_HEIGHT = 900;
// Total vertical margin the popover leaves inside the work area: 4px at the top
// and 4px at the bottom, the same gaps positionWindowNearTray() clamps to.
const POPOVER_EDGE_MARGIN = 8;

// Where the Claude CLI persists its OAuth credentials. After an interactive
// `login`, the CLI rewrites the file below — we watch it so we can refresh
// (and surface) the restored state without the user reopening the app or
// pressing the reload button. The path must be the one claudeProvider reads,
// so it comes from the provider's own resolver (CLAUDE_CONFIG_DIR aware).
// Codex has one such file per account, so its path comes from the account
// being logged in (Account.authFile) instead.
const CREDENTIAL_FILES = {
  claude: claudeProvider.credentialsPath(),
};
const LOGIN_WATCH_INTERVAL_MS = 1_500;
const LOGIN_WATCH_TIMEOUT_MS = 5 * 60_000;

// --- Deferred Claude re-auth (design decision) ---------------------------
// A persistent Claude auth error can only be fixed by a full interactive
// `claude auth login`, which OPENS THE USER'S BROWSER. We must NEVER trigger
// that from the background polling loop (PR #18 did, and the browser popped
// open spontaneously — rejected). Instead the polling loop only RECORDS that a
// re-auth is pending (`claudeReauthPending`); the browser-opening login is
// started solely in response to the user opening the popover (tray click) or
// pressing the 🔑 button — that interaction is the consent moment. A cooldown
// keeps repeated opens from spamming the browser if the user keeps reopening
// the popover while the login is unfinished.
// Min gap between automatic login attempts. Intentionally LONGER than
// LOGIN_WATCH_TIMEOUT_MS (5 min): if the user abandons a login, the watcher
// dies first and the cooldown still holds the next popover-open back for a
// while, instead of re-opening the browser the moment the watcher gives up.
const AUTO_LOGIN_COOLDOWN_MS = 10 * 60_000;
const CLAUDE_AUTH_ERROR_CODES = new Set(['claude_unauthorized', 'claude_credentials_missing']);

let tray = null;
let popoverWindow = null;
// Current popover content height (CSS px). Starts at the first-paint default
// and tracks whatever the renderer last measured.
let popoverHeight = POPOVER_DEFAULT_HEIGHT;
// The last raw (unclamped) content height the renderer reported. Kept so the
// window can be re-derived against a ceiling that changed after the report —
// the renderer stops reporting while clamped, so this is the only record of how
// tall the content really is.
let lastReportedHeight = POPOVER_DEFAULT_HEIGHT;
// True while the content is taller than the window we can give it. Mirrored to
// the renderer (see setContentClamped) and reset whenever the document is
// reloaded, because a fresh document starts unclamped.
let contentClamped = false;
let pollTimer = null;
let isPolling = false;
let fadeTimer = null;
let ntfyResetNotifier = null;
// Watcher key -> { timer, deadline } while we wait for an interactive login to
// land. The key is 'claude' or `codex:<account id>`, so two Codex accounts can
// be logging in at the same time without one cancelling the other.
const loginWatchers = new Map();
// Set true by the polling loop when it sees a persistent Claude auth error;
// consumed (and the browser login started) only when the user opens the popover.
let claudeReauthPending = false;
let lastAutoLoginAt = 0;
let latestSnapshot = {
  claude: null, // {ok, data?, error?}
  codexAccounts: [], // Account + {ok, data?, error?}, one per Codex home
  fetchedAt: 0,
};
// Codex homes seen by the previous poll, so a home that disappeared (its
// directory was removed) gets its app-server stopped instead of lingering.
let knownCodexHomes = [];

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

function updateNtfyNotifications() {
  if (ntfyResetNotifier) {
    ntfyResetNotifier.update(latestSnapshot);
  }
}

function ntfyPatchFromRenderer(value) {
  const raw = value && typeof value === 'object' ? value : {};
  const patch = {};
  for (const key of ['topicUrl', 'accessToken']) {
    if (Object.hasOwn(raw, key)) patch[key] = typeof raw[key] === 'string' ? raw[key] : '';
  }
  for (const key of ['notifyFiveHour', 'notifyWeekly']) {
    if (Object.hasOwn(raw, key)) patch[key] = !!raw[key];
  }
  return patch;
}

function createPopoverWindow() {
  if (popoverWindow) return popoverWindow;
  popoverWindow = new BrowserWindow({
    width: POPOVER_WIDTH,
    height: popoverHeight,
    // Width stays pinned; height is driven by setContentSize from the measured
    // content (no min/max height lock, or it would clamp the fit-to-content
    // resize). resizable:false still blocks any user drag-resize.
    minWidth: POPOVER_WIDTH,
    maxWidth: POPOVER_WIDTH,
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
  popoverWindow.webContents.on('did-start-loading', () => { contentClamped = false; });
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

// The display the tray icon lives on, or null when it cannot be determined.
function trayDisplay() {
  try {
    if (tray) {
      const b = tray.getBounds();
      return screen.getDisplayNearestPoint({ x: b.x, y: b.y });
    }
    return screen.getPrimaryDisplay();
  } catch {
    return null;
  }
}

// Ceiling for the popover height. A fixed 900px does not fit every screen — a
// 1080p display at 150% scaling leaves a work area of about 690px — so the
// ceiling follows the display the popover is anchored to and the window can
// never run off it. Falls back to the hard ceiling when the display is unknown.
function popoverMaxHeight() {
  const display = trayDisplay();
  const available = display && display.workArea ? display.workArea.height : NaN;
  if (!Number.isFinite(available)) return POPOVER_HARD_MAX_HEIGHT;
  return Math.max(
    POPOVER_MIN_HEIGHT,
    Math.min(POPOVER_HARD_MAX_HEIGHT, available - POPOVER_EDGE_MARGIN),
  );
}

function positionWindowNearTray() {
  if (!popoverWindow || !tray) return;
  const trayBounds = tray.getBounds();
  const display = screen.getDisplayNearestPoint({ x: trayBounds.x, y: trayBounds.y });
  const workArea = display.workArea;
  // The ceiling belongs to the display the popover lands on, which may not be
  // the one the height was measured against, so re-derive it here: the window
  // must fit the work area it lands on, and must grow back to the full content
  // once it lands on a work area with room for it.
  reapplyPopoverHeight();
  let x = Math.round(trayBounds.x + trayBounds.width / 2 - POPOVER_WIDTH / 2);
  let y = Math.round(trayBounds.y - popoverHeight - 8);
  if (y < workArea.y) {
    y = trayBounds.y + trayBounds.height + 8;
  }
  x = Math.max(workArea.x + 4, Math.min(workArea.x + workArea.width - POPOVER_WIDTH - 4, x));
  y = Math.max(workArea.y + 4, Math.min(workArea.y + workArea.height - popoverHeight - 4, y));
  // setPosition only — do NOT round-trip getBounds() through setBounds() on
  // fractional DPI displays. Also re-assert the content size every show so the
  // window stays pinned to the latest measured height.
  popoverWindow.setPosition(x, y);
  popoverWindow.setContentSize(POPOVER_WIDTH, popoverHeight);
}

// Tell the renderer whether its requested height was cut down to the display.
// The renderer hides overflow by default, so this is the only signal that lets
// it show a scrollbar — and stop asking for a height it can never get.
function setContentClamped(clamped) {
  if (clamped === contentClamped) return;
  contentClamped = clamped;
  if (popoverWindow && !popoverWindow.isDestroyed()) {
    popoverWindow.webContents.send('content-clamped', clamped);
  }
}

// Re-derive popoverHeight from the last reported content height against the
// ceiling that applies right now, and mirror the resulting clamp state to the
// renderer. Returns true when the height changed, so callers know whether the
// window still has to be resized. Idempotent: calling it twice in a row is a
// no-op, which is what lets positionWindowNearTray() run it on every show.
function reapplyPopoverHeight() {
  const max = popoverMaxHeight();
  const next = Math.max(POPOVER_MIN_HEIGHT, Math.min(max, lastReportedHeight));
  setContentClamped(lastReportedHeight > max);
  if (next === popoverHeight) return false;
  popoverHeight = next;
  logger.info('[popover] fit to content height', next);
  return true;
}

// The renderer measured its content box and told us how tall it is. Resize the
// window to match so it fits exactly — no scrollbar, no leftover padding.
function applyContentHeight(rawHeight) {
  const h = Math.round(Number(rawHeight));
  if (!Number.isFinite(h)) return;
  lastReportedHeight = h;
  if (!reapplyPopoverHeight()) return; // no change → nothing to do
  if (!popoverWindow || popoverWindow.isDestroyed()) return;
  if (popoverWindow.isVisible()) {
    // Re-anchor to the tray so the popover grows upward from it; this also
    // re-asserts the new content size for us.
    positionWindowNearTray();
  } else {
    popoverWindow.setContentSize(POPOVER_WIDTH, popoverHeight);
  }
}

function showPopover() {
  const win = createPopoverWindow();
  if (win.isVisible()) {
    // The tray may have moved to another display while the popover stayed open,
    // so re-derive the height against the ceiling that applies now.
    if (reapplyPopoverHeight()) positionWindowNearTray();
    // User looking at the app is the consent moment for the deferred browser
    // login; start it before the snapshot send so the renderer immediately
    // shows the in-progress state.
    maybeStartPendingReauth();
    sendSnapshotToRenderer();
    return;
  }
  positionWindowNearTray();
  win.setBackgroundColor(popoverBackgroundColor());
  win.setOpacity(0);
  win.show();
  win.focus();
  fadeWindowTo(win, 1);
  maybeStartPendingReauth();
  sendSnapshotToRenderer();
}

function togglePopover() {
  const win = createPopoverWindow();
  if (win.isVisible()) {
    hidePopover();
    return;
  }
  showPopover();
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
  if (code === 'codex_home_missing') return 'login required';
  if (code === 'codex_timeout' || code === 'claude_timeout') return 'timeout';
  if (code === 'claude_network') return 'network error';
  return 'error';
}

function serviceStatusLabel(name, svc) {
  if (!svc) return `${name}: 取得中`;
  if (!svc.ok) return `${name}: ${errorSummary(svc.error)}`;
  return `${name}: ${percentLabel(utilizationFromSnapshot(svc))}`;
}

function codexAccounts() {
  return Array.isArray(latestSnapshot.codexAccounts) ? latestSnapshot.codexAccounts : [];
}

// Attach the name every surface should show for an account. The tray, the
// popover and the ntfy notifier all read `displayName` instead of resolving the
// name again, so a rename reaches them from one place. `customName` is the raw
// setting (null when the account uses its default name), which the settings
// panel needs to leave its input empty rather than pre-filling the default.
//
// Invariant: latestSnapshot.codexAccounts is always decorated — refreshNow and
// every settings change run the list through here.
function decorateCodexAccounts(accounts) {
  const list = Array.isArray(accounts) ? accounts : [];
  const names = getSettings().codexAccountNames || {};
  return list.map((account) => {
    const custom = Object.hasOwn(names, account.label) && typeof names[account.label] === 'string'
      ? names[account.label]
      : null;
    return {
      ...account,
      displayName: accountDisplayName(account, list, names),
      customName: custom || null,
      // The name the account would carry with no override. The settings panel
      // shows it as the input placeholder; resolving it here keeps the naming
      // rule in codexHomes.js alone.
      defaultName: accountDisplayName(account, list, null),
    };
  });
}

function redecorateCodexAccounts() {
  latestSnapshot = {
    ...latestSnapshot,
    codexAccounts: decorateCodexAccounts(latestSnapshot.codexAccounts),
  };
}

// The tray has room for one Codex donut, so it shows the account closest to its
// limit — that is the one the user needs to know about.
function codexTrayUtilization() {
  let worst = null;
  for (const account of codexAccounts()) {
    const util = utilizationFromSnapshot(account);
    if (util == null) continue;
    if (worst == null || util > worst) worst = util;
  }
  return worst;
}

function codexHasError() {
  return codexAccounts().some((account) => !account.ok);
}

function currentTrayScaleFactor() {
  const display = trayDisplay();
  return (display && display.scaleFactor) || 1;
}

function updateTray() {
  if (!tray) return;
  const c = utilizationFromSnapshot(latestSnapshot.claude);
  const x = codexTrayUtilization();
  try {
    tray.setImage(buildTrayImage(c, x, {
      scaleFactor: currentTrayScaleFactor(),
      claudeError: !!(latestSnapshot.claude && !latestSnapshot.claude.ok),
      codexError: codexHasError(),
    }));
  } catch (err) {
    logger.error('[tray] setImage failed', err);
  }
  const accounts = codexAccounts();
  const tooltipLines = [
    'Agent Limit Checker',
    serviceStatusLabel('Claude', latestSnapshot.claude),
  ];
  if (accounts.length === 0) {
    tooltipLines.push(serviceStatusLabel('Codex', null));
  } else {
    for (const account of accounts) {
      tooltipLines.push(serviceStatusLabel(account.displayName, account));
    }
  }
  tray.setToolTip(tooltipLines.join('\n'));
  rebuildTrayMenu();
}

// `<name> 5h:` padded to a common width so the percentages line up under each
// other in the menu.
function usageMenuItem(name, svc) {
  const value = svc && !svc.ok ? errorSummary(svc.error) : percentLabel(utilizationFromSnapshot(svc));
  return { label: `${`${name} 5h:`.padEnd(11)}${value}`, enabled: false };
}

// The menu line for one login target. The default home appended by
// codexLoginTargets() has not been discovered, so it is labelled as creating the
// home rather than logging into an existing one; with a single target the home
// name carries no information and the plain wording is kept.
function codexLoginMenuLabel(account, targets, discoveredIds) {
  if (targets.length <= 1) return 'codex login (新しいターミナルで実行)';
  if (!discoveredIds.has(account.id)) return `codex login ${account.label} (既定ホームを作成)`;
  return `codex login ${account.displayName} (新しいターミナルで実行)`;
}

function rebuildTrayMenu() {
  if (!tray) return;
  const accounts = codexAccounts();
  const codexUsageItems = accounts.length === 0
    ? [usageMenuItem('Codex', null)]
    : accounts.map((account) => usageMenuItem(account.displayName, account));
  const loginTargets = codexLoginTargets(accounts, fallbackCodexAccount());
  const discoveredIds = new Set(accounts.map((account) => account.id));
  const codexLoginItems = loginTargets.map((account) => ({
    label: codexLoginMenuLabel(account, loginTargets, discoveredIds),
    click: () => openLoginTerminal('codex', account.id),
  }));
  const menu = Menu.buildFromTemplate([
    usageMenuItem('Claude', latestSnapshot.claude),
    ...codexUsageItems,
    { type: 'separator' },
    { label: '詳細を表示', click: () => togglePopover() },
    { label: '今すぐ更新', click: () => { void refreshNow(); } },
    { type: 'separator' },
    {
      label: 'claude login (新しいターミナルで実行)',
      click: () => openLoginTerminal('claude'),
    },
    ...codexLoginItems,
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
  updateNtfyNotifications();
  sendSnapshotToRenderer();
}

function loginArgsFor(target) {
  // The Claude CLI has no top-level `login` subcommand — passing `login` to
  // `claude` makes the CLI treat it as the first prompt and open an
  // interactive chat session. The actual OAuth flow lives under `auth login`.
  // Codex CLI on the other hand DOES have a top-level `login` subcommand.
  return target === 'claude' ? ['auth', 'login'] : ['login'];
}

// An Account-shaped record for a home we have not discovered (yet). Used both
// for the "no Codex home at all" snapshot entry and as the login fallback, so
// pressing 🔑 before any home exists still logs into `~/.codex`.
function fallbackCodexAccount() {
  const home = defaultCodexHome();
  return {
    id: normalizeHomePath(home),
    label: path.basename(home),
    home,
    authFile: path.join(home, 'auth.json'),
    isDefault: true,
  };
}

// Resolve the account a login request refers to. The id comes from the
// renderer, so it is only ever matched against the accounts we discovered
// ourselves — never used as a path — and anything unrecognized falls back to
// the default home.
function resolveCodexLoginAccount(accountId) {
  const accounts = codexAccounts();
  const fallback = fallbackCodexAccount();
  if (typeof accountId === 'string' && accountId) {
    const found = accounts.find((account) => account.id === accountId);
    if (found) return found;
    // The default home is a legitimate target even before it exists — logging
    // into it is what creates it — so it is not an unknown id.
    if (accountId !== fallback.id) {
      logger.warn('[login] unknown codex account id; using the default home');
    }
  }
  return accounts.find((account) => account.id === fallback.id) || fallback;
}

function openLoginTerminal(target, accountId) {
  // Spawn a new console window that runs the OAuth flow interactively.
  const exe = target === 'claude' ? resolveClaudeExecutable() : resolveCodexExecutable();
  if (!exe) {
    const name = target === 'claude' ? 'Claude Code' : 'Codex';
    const envName = target === 'claude' ? 'CLAUDE_PATH' : 'CODEX_PATH';
    const message = `${name} CLI が見つかりません。PATH に追加するか ${envName} に実行ファイルパスを設定してください。`;
    logger.warn('[login] executable missing', target);
    dialog.showErrorBox(`${name} CLI が見つかりません`, message);
    return false;
  }
  const cliArgs = loginArgsFor(target);
  // Codex writes the credentials into $CODEX_HOME, so the login has to run
  // against the home of the account the user asked for.
  const account = target === 'codex' ? resolveCodexLoginAccount(accountId) : null;
  try {
    if (process.platform === 'win32') {
      // One unified path for .exe / .ps1 / .cmd / .bat: PowerShell's call
      // operator `& 'path' args` launches all of them, and single-quoted
      // strings are literal so a path that contains spaces or quotes can
      // never be mis-parsed as a command name (the old `cmd /k` failure).
      const psCommand = buildLoginPsCommand(
        exe,
        cliArgs,
        account ? { env: { CODEX_HOME: account.home } } : undefined,
      );
      spawn(
        'cmd.exe',
        [
          '/c', 'start', '""',
          'powershell.exe',
          '-NoProfile', '-ExecutionPolicy', 'Bypass',
          '-Command', psCommand,
        ],
        { detached: true, stdio: 'ignore', windowsHide: false },
      ).unref();
    } else {
      spawn(exe, cliArgs, {
        detached: true,
        stdio: 'ignore',
        env: account ? { ...process.env, CODEX_HOME: account.home } : process.env,
      }).unref();
    }
    // Watch the credential file so the app recovers on its own once the
    // login lands — no reopen, no manual reload.
    watchForLoginCompletion(target, account);
    return true;
  } catch (err) {
    logger.error('[login] failed to spawn', err);
    return false;
  }
}

// Like openLoginTerminal but with no visible console window. This is the
// background re-auth path: it still opens the browser (the OAuth flow), but it
// must only ever be invoked when the user has just opened the popover (see the
// design note near AUTO_LOGIN_COOLDOWN_MS) — never from the polling loop.
function openLoginSilent(target) {
  const exe = target === 'claude' ? resolveClaudeExecutable() : resolveCodexExecutable();
  if (!exe) {
    logger.warn('[login] silent login executable missing', target);
    return false;
  }
  const cliArgs = loginArgsFor(target);
  try {
    if (process.platform === 'win32') {
      // Spawn powershell.exe directly (no `cmd /c start`), hidden. `invoke` is
      // just the `& 'exe' 'arg'...` call-operator string with the same
      // single-quote escaping as the visible login path.
      const invoke = buildSilentPsCommand(exe, cliArgs);
      spawn(
        'powershell.exe',
        [
          '-NoProfile', '-ExecutionPolicy', 'Bypass',
          '-WindowStyle', 'Hidden',
          '-Command', invoke,
        ],
        { detached: true, stdio: 'ignore', windowsHide: true },
      ).unref();
    } else {
      spawn(exe, cliArgs, { detached: true, stdio: 'ignore' }).unref();
    }
    watchForLoginCompletion(target, target === 'codex' ? resolveCodexLoginAccount() : null);
    logger.info('[login] silent login started', target);
    return true;
  } catch (err) {
    logger.error('[login] failed to spawn silent login', err);
    return false;
  }
}

// Start the deferred Claude re-auth, but only when one is actually pending, no
// login is already in flight, and we are past the cooldown. Called when the
// user opens the popover — the consent moment for opening the browser.
function maybeStartPendingReauth() {
  if (!claudeReauthPending) return;
  if (loginWatchers.has('claude')) return;
  if (Date.now() - lastAutoLoginAt <= AUTO_LOGIN_COOLDOWN_MS) return;
  lastAutoLoginAt = Date.now();
  openLoginSilent('claude');
}

async function fileSignature(filePath) {
  try {
    const st = await fsp.stat(filePath);
    return `${st.mtimeMs}:${st.size}`;
  } catch {
    return null; // missing / unreadable
  }
}

function stopLoginWatcher(key) {
  const w = loginWatchers.get(key);
  if (w && w.timer) clearTimeout(w.timer);
  loginWatchers.delete(key);
}

// One watcher per login target. Codex is keyed by account so two accounts can
// be logged into at the same time; a second click on the same account restarts
// that account's watcher only.
function loginWatcherKey(target, account) {
  return target === 'codex' ? `codex:${account.id}` : target;
}

// Poll the target's credential file until it changes (login wrote new
// tokens), then auto-refresh and surface the popover. Gives up after a few
// minutes so an abandoned login doesn't leave a timer running forever.
async function watchForLoginCompletion(target, account) {
  const file = target === 'codex' ? (account && account.authFile) : CREDENTIAL_FILES[target];
  if (!file) return;
  const key = loginWatcherKey(target, account);
  stopLoginWatcher(key); // a fresh click restarts the window
  const baseline = await fileSignature(file);
  const deadline = Date.now() + LOGIN_WATCH_TIMEOUT_MS;
  // `self` lets a tick tell whether it has been superseded by a later click
  // (or cancelled on quit) across its own `await`s.
  const self = { timer: null, deadline };
  loginWatchers.set(key, self);

  const tick = async () => {
    if (loginWatchers.get(key) !== self) return; // superseded / cancelled
    const sig = await fileSignature(file);
    if (loginWatchers.get(key) !== self) return; // re-check after the await
    if (sig != null && sig !== baseline) {
      stopLoginWatcher(key);
      logger.info('[login] credentials updated — auto-refreshing', key);
      if (target === 'codex') {
        // The long-lived `codex app-server` for this account cached the
        // logged-out state; drop it so the next fetch respawns with the new
        // credentials. Other accounts' servers stay up.
        try { codexProvider.shutdown(account.home); } catch { /* ignore */ }
      }
      await refreshNow();
      showPopover();
      return;
    }
    if (Date.now() >= deadline) {
      stopLoginWatcher(key);
      logger.info('[login] watch timed out', key);
      return;
    }
    self.timer = setTimeout(tick, LOGIN_WATCH_INTERVAL_MS);
  };

  self.timer = setTimeout(tick, LOGIN_WATCH_INTERVAL_MS);
}

// Which logins the renderer should show as in progress. Codex is a map keyed
// by account id, because each account has its own 🔑 button.
function loginInProgressForRenderer() {
  const codex = {};
  for (const key of loginWatchers.keys()) {
    if (key.startsWith('codex:')) codex[key.slice('codex:'.length)] = true;
  }
  return { claude: loginWatchers.has('claude'), codex };
}

function buildSnapshotForRenderer() {
  const defaultAccount = fallbackCodexAccount();
  return {
    claude: latestSnapshot.claude,
    codexAccounts: latestSnapshot.codexAccounts,
    // The default home, whether or not it was discovered. The popover offers a
    // login into it when no discovered account is the default one, so `~/.codex`
    // can still be created from the UI.
    codexDefaultAccount: { id: defaultAccount.id, label: defaultAccount.label },
    fetchedAt: latestSnapshot.fetchedAt,
    settings: getSettings(),
    autoLaunchEnabled: autoLaunch.isEnabled(),
    isPolling,
    theme: currentTheme(),
    appVersion: app.getVersion(),
    loginInProgress: loginInProgressForRenderer(),
  };
}

function sendSnapshotToRenderer() {
  if (popoverWindow && !popoverWindow.isDestroyed()) {
    popoverWindow.webContents.send('snapshot', buildSnapshotForRenderer());
  }
}

// The account list shown when no Codex home exists at all. Keeping one entry
// means the popover always has a Codex section with a 🔑 button, so the user can
// create `~/.codex` by logging in from there.
function missingCodexHomeAccounts() {
  return [{
    ...fallbackCodexAccount(),
    ok: false,
    error: {
      code: 'codex_home_missing',
      message: 'Codex のホームディレクトリが見つかりません (~/.codex*)',
      retryAfter: null,
    },
  }];
}

async function refreshNow() {
  if (isPolling) return;
  isPolling = true;
  sendSnapshotToRenderer();

  // Rediscovered every poll: a `codex login` can create a home, and a removed
  // directory must drop out of the list.
  const accounts = discoverCodexHomes();
  const [claudeRes, ...codexResults] = await Promise.allSettled([
    claudeProvider.fetch(),
    ...accounts.map((account) => codexProvider.fetch(account.home)),
  ]);

  const homes = accounts.map((account) => account.home);
  for (const home of knownCodexHomes) {
    if (homes.includes(home)) continue;
    // The home is gone; stop the app-server that was still bound to it.
    try { void codexProvider.shutdown(home); } catch { /* ignore */ }
  }
  knownCodexHomes = homes;

  latestSnapshot = {
    claude: settled(claudeRes),
    codexAccounts: decorateCodexAccounts(accounts.length > 0
      ? accounts.map((account, index) => ({ ...account, ...settled(codexResults[index]) }))
      : missingCodexHomeAccounts()),
    fetchedAt: Date.now(),
  };
  // Record (don't act on) a persistent Claude auth error. The browser-opening
  // login is deferred to maybeStartPendingReauth() when the user opens the
  // popover. A successful poll clears this by setting it false.
  claudeReauthPending = CLAUDE_AUTH_ERROR_CODES.has(latestSnapshot.claude?.error?.code);
  isPolling = false;
  updateNtfyNotifications();
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
  if (ntfyResetNotifier) ntfyResetNotifier.dispose();
  for (const key of [...loginWatchers.keys()]) stopLoginWatcher(key);
  try { void codexProvider.shutdown(); } catch { /* ignore */ }
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
ipcMain.handle('set-ntfy-settings', (_evt, partial) => {
  saveSettings({ ntfy: ntfyPatchFromRenderer(partial) });
  updateNtfyNotifications();
  return buildSnapshotForRenderer();
});
// Rename one Codex account. `label` comes from the renderer, so it is only
// accepted when it matches a home we discovered ourselves — it is a settings key
// here, never a path. An empty name clears the override and restores the
// default name.
ipcMain.handle('set-codex-account-name', (_evt, label, name) => {
  const known = codexAccounts().some((account) => account.label === label);
  if (!known) {
    logger.warn('[settings] unknown codex account label; rename ignored');
    return buildSnapshotForRenderer();
  }
  saveSettings({ codexAccountNames: { [label]: typeof name === 'string' ? name : '' } });
  redecorateCodexAccounts();
  updateNtfyNotifications();
  updateTray();
  sendSnapshotToRenderer();
  return buildSnapshotForRenderer();
});
ipcMain.handle('open-login', (_evt, target, accountId) => {
  return openLoginTerminal(
    target === 'codex' ? 'codex' : 'claude',
    typeof accountId === 'string' ? accountId : undefined,
  );
});
ipcMain.handle('quit', () => { quitApp(); });
ipcMain.on('content-height', (_evt, height) => applyContentHeight(height));

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
  ntfyResetNotifier = new NtfyResetNotifier({ getSettings, logger });

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
  if (ntfyResetNotifier) ntfyResetNotifier.dispose();
  try { void codexProvider.shutdown(); } catch { /* ignore */ }
});
