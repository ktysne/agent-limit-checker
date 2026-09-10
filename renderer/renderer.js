'use strict';

const ERROR_HINTS = {
  claude_credentials_missing: '🔑 ボタンを押すと再ログインできます。完了すると自動で復帰します。',
  claude_unauthorized: 'OAuth トークンが無効です。🔑 ボタンから再ログインすると自動で復帰します。',
  claude_refresh_unconfigured: '自動 refresh は未設定です。🔑 ボタンから再ログインすると自動で復帰します。',
  claude_refresh_token_missing: 'refresh token がありません。🔑 ボタンから再ログインすると自動で復帰します。',
  codex_cli_missing: 'PowerShell で `npm i -g @openai/codex` を実行してください。',
  codex_rpc_error: '🔑 ボタンを押すと再ログインできます。完了すると自動で復帰します。',
  codex_home_missing: '🔑 ボタンで codex login を実行すると ~/.codex が作成され、完了すると自動で復帰します。',
};

const AUTO_REAUTH_CODES = new Set(['claude_unauthorized', 'claude_credentials_missing']);

function classify(util) {
  if (util == null) return 'ok';
  if (util < 0.7) return 'ok';
  if (util < 0.85) return 'warn';
  return 'crit';
}

function percent(util) {
  if (util == null) return '--%';
  if (util > 1) return '100%+';
  return `${Math.round(Math.max(0, util) * 100)}%`;
}

function resetText(resetsAtMs) {
  const now = Date.now();
  // Anthropic は「このウィンドウでまだ消費していない」とき resets_at を null で返す。
  // それは 0% / 未使用 を意味するので、進捗バーは出しつつタイマーだけ伏せる。
  if (resetsAtMs == null) return 'ウィンドウ未開始 (このウィンドウでまだ消費なし)';
  if (resetsAtMs <= now) return 'まもなくリセット';
  const diffSec = Math.round((resetsAtMs - now) / 1000);
  const days = Math.floor(diffSec / 86400);
  const hours = Math.floor((diffSec % 86400) / 3600);
  const mins = Math.floor((diffSec % 3600) / 60);
  let rel;
  if (days > 0) rel = `${days}日${hours}時間${mins}分`;
  else if (hours > 0) rel = `${hours}時間${mins}分`;
  else rel = `${mins}分`;
  const absolute = new Date(resetsAtMs).toLocaleString([], {
    hour: '2-digit',
    minute: '2-digit',
  });
  return `あと ${rel} (${absolute} リセット)`;
}

function renderBucket(label, limit, { compact = false } = {}) {
  const cls = compact ? 'bucket compact' : 'bucket';
  if (!limit) {
    return `<div class="${cls}"><div class="bucket-row"><span class="bucket-label">${label}</span><span class="bucket-value">N/A</span></div></div>`;
  }
  const colorCls = classify(limit.utilization);
  const pct = percent(limit.utilization);
  const width = Math.min(100, Math.max(0, (limit.utilization || 0) * 100));
  return `
    <div class="${cls}">
      <div class="bucket-row">
        <span class="bucket-label">${label}</span>
        <span class="bucket-value ${colorCls}">${pct}</span>
      </div>
      <div class="progress"><div class="progress-fill ${colorCls}" style="width:${width}%"></div></div>
      <div class="reset-text">${resetText(limit.resetsAt)}</div>
    </div>
  `;
}

// Format a credit balance for display. Two shapes, distinguished by `currency`:
//   - a currency code (Claude's `spend.balance`, e.g. "USD") → a money amount,
//     formatted like "$5.00".
//   - null (codex credits) → a plain credit *count*, not money. We render
//     "<balance> クレジット" to 2 decimals, rounding the 3rd decimal onward
//     (115.9354… → "115.94").
// The provider already normalizes `amount` (dollars for money, credit count for
// codex), so this is purely display.
function formatCredit(amount, currency) {
  const n = Number(amount);
  // Only a positive balance is worth a row. The provider already enforces this,
  // but guarding here means a stray null/0/negative amount hides the row rather
  // than printing a misleading "0".
  if (!Number.isFinite(n) || n <= 0) return null;
  // toFixed(2) rounds to the nearest hundredth (round half up), which is exactly
  // "小数第3位以下を四捨五入して2桁表示".
  if (!currency) return `${n.toFixed(2)} クレジット`;
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(n);
  } catch {
    return `${n.toFixed(2)} ${currency}`;
  }
}

// Show the available credit balance for a service, or nothing when there is no
// balance to show. The provider returns `credits: null` for credit-less
// accounts (Anthropic's `spend.balance: null`, or codex's `hasCredits: false`),
// so the row is hidden entirely rather than showing $0 — per the "残高がない
// 場合は項目ごと非表示" requirement.
function renderCredits(credits) {
  if (!credits) return '';
  const value = credits.unlimited ? '無制限' : formatCredit(credits.amount, credits.currency);
  if (!value) return '';
  return `
    <div class="credit-row">
      <span class="bucket-label">クレジット残高</span>
      <span class="credit-value">${escapeHtml(value)}</span>
    </div>
  `;
}

// Fill one service section's body. `body` is the .service-body element, so the
// same renderer serves the static Claude section and every generated Codex one.
function renderService(body, svc, loginInProgress) {
  if (!body) return;
  if (!svc) {
    body.innerHTML = '<div class="loading">取得中…</div>';
    return;
  }
  if (!svc.ok) {
    const err = svc.error || {};
    if (loginInProgress && AUTO_REAUTH_CODES.has(err.code)) {
      body.innerHTML = `
        <div class="error-box">
          <div class="error-title">⟳ 再認証中…</div>
          <div class="error-message">ブラウザで Anthropic の承認画面が開きます。完了すると自動で復帰します。</div>
        </div>
      `;
      return;
    }
    const hint = ERROR_HINTS[err.code] ? `<div class="error-message">${escapeHtml(ERROR_HINTS[err.code])}</div>` : '';
    body.innerHTML = `
      <div class="error-box">
        <div class="error-title">⚠ 取得失敗</div>
        <div class="error-message">${escapeHtml(err.message || '原因不明')}</div>
        ${hint}
      </div>
    `;
    return;
  }
  const usage = svc.data || {};
  let html = '';
  if (usage.plan) {
    html += `<div class="plan-label">Plan: ${escapeHtml(usage.plan)}</div>`;
  }
  // Render a meter only for windows the API actually reports. A provider can
  // drop a window entirely — e.g. Codex temporarily removed its 5-hour limit,
  // so `fiveHour` comes back null and we hide the bucket instead of showing N/A.
  if (usage.fiveHour) html += renderBucket('5時間', usage.fiveHour);
  if (usage.weekly) html += renderBucket('週次', usage.weekly, { compact: true });
  // Per-model weekly caps (e.g. Fable) come through as an array; render one
  // meter each, labelled by the model name the API reports. `label` is
  // API-provided so it must be escaped before going into the bucket markup.
  if (Array.isArray(usage.weeklyScoped)) {
    for (const scoped of usage.weeklyScoped) {
      html += renderBucket(`週次 (${escapeHtml(scoped.label)})`, scoped, { compact: true });
    }
  }
  // Available credit balance, only when the account actually has one.
  html += renderCredits(usage.credits);
  body.innerHTML = html;
}

// Identity of the Codex sections currently in the DOM, in order. Rebuilding the
// sections makes the popover height jump, so we only do it when what the markup
// depends on — the account set and the names shown in the headers — changed.
let codexSectionKeys = [];

// The heading of one section, and the home directory name under it. The heading
// is the name resolved by the main process, so a user-set name replaces
// "Codex (.codex-sub)" outright. The home name is then shown as a sub-label,
// because a custom heading no longer says which home it belongs to; with a
// default heading the home name is already in it (or there is only one account),
// so the sub-label would just repeat it.
function codexSectionHtml(account) {
  const id = escapeHtml(account.id);
  const name = escapeHtml(account.displayName || 'Codex');
  const sub = account.customName
    ? `<span class="service-sub">${escapeHtml(account.label)}</span>`
    : '';
  return `
    <section class="service" data-account-id="${id}">
      <div class="service-head">
        <div class="service-title">
          <span class="brand-dot brand-codex"></span>
          <h2>${name}</h2>
          ${sub}
        </div>
        <button class="icon-btn" data-login="codex" data-account-id="${id}" title="${name} の codex login を実行">🔑</button>
      </div>
      <div class="service-body">
        <div class="loading">取得中…</div>
      </div>
    </section>
  `;
}

// One section per Codex account. Before the first poll lands the snapshot has
// no accounts yet; we still show a single section so the popover looks the same
// as it does once the data arrives (and the 🔑 button already works — with no
// id the main process falls back to the default home).
function renderCodexAccounts(accounts, loginInProgress) {
  const host = document.getElementById('codex-services');
  if (!host) return;
  const list = Array.isArray(accounts) && accounts.length > 0
    ? accounts
    : [{ id: '', label: '', pending: true }];
  const keys = list.map((account) => `${account.id}\n${account.displayName || ''}\n${account.customName || ''}`);
  if (keys.length !== codexSectionKeys.length || keys.some((key, i) => key !== codexSectionKeys[i])) {
    host.innerHTML = list.map((account) => codexSectionHtml(account)).join('');
    codexSectionKeys = keys;
  }
  const progress = loginInProgress && typeof loginInProgress === 'object' ? loginInProgress : {};
  list.forEach((account, index) => {
    const section = host.children[index];
    if (!section) return;
    renderService(
      section.querySelector('.service-body'),
      account.pending ? null : account,
      !!progress[account.id],
    );
  });
}

// `codex login` into the default home stays reachable from the popover even when
// only alternative homes exist: without this the 🔑 buttons all point at the
// discovered homes and `~/.codex` could never be created from the UI. Hidden as
// soon as the default home shows up in the account list, where it has its own
// 🔑 button.
function renderCodexDefaultLogin(accounts, defaultAccount) {
  const host = document.getElementById('codex-default-login');
  const btn = document.getElementById('codex-default-login-btn');
  if (!host || !btn) return;
  const list = Array.isArray(accounts) ? accounts : [];
  const id = defaultAccount && defaultAccount.id ? String(defaultAccount.id) : '';
  const show = list.length > 0 && !!id
    && !list.some((account) => account && account.isDefault);
  host.hidden = !show;
  if (!show) {
    btn.removeAttribute('data-account-id');
    btn.textContent = '';
    return;
  }
  btn.setAttribute('data-account-id', id);
  // textContent, not innerHTML: the label carries a home directory name.
  btn.textContent = `既定ホーム (${defaultAccount.label || '.codex'}) にログイン`;
}

function escapeHtml(s) {
  return String(s || '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;',
  })[c]);
}

function renderFooter(fetchedAt, version) {
  const el = document.getElementById('last-updated');
  if (!el) return;
  const parts = [];
  if (version) parts.push(`v${version}`);
  if (fetchedAt) {
    const t = new Date(fetchedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    parts.push(`最終更新: ${t}`);
  }
  el.textContent = parts.join(' · ');
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme === 'light' ? 'light' : 'dark';
}

function setSettingsPanelOpen(open) {
  const panel = document.getElementById('settings-panel');
  const toggle = document.getElementById('settings-toggle');
  if (!panel || !toggle) return;
  panel.hidden = !open;
  toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  syncWindowHeight();
}

function setInputValue(id, value) {
  const el = document.getElementById(id);
  if (!el || document.activeElement === el) return;
  const next = String(value || '');
  if (el.value !== next) el.value = next;
}

function setChecked(id, value) {
  const el = document.getElementById(id);
  if (el) el.checked = !!value;
}

// Home directory names of the display-name inputs currently in the DOM, in
// order. Rebuilding them drops what the user is typing, so it only happens when
// the account set itself changed.
let codexNameFieldLabels = [];

function codexNameFieldHtml(account, index) {
  const label = escapeHtml(account.label);
  return `
    <label class="setting-field" for="codex-name-${index}">
      <span>${label}</span>
      <input type="text" id="codex-name-${index}" data-account-label="${label}" maxlength="40"
             placeholder="${escapeHtml(account.defaultName || 'Codex')}" spellcheck="false" autocomplete="off">
    </label>
  `;
}

// One rename input per discovered Codex account. The whole group is hidden
// while no account is known (before the first poll lands), so the panel never
// shows an empty fieldset.
function renderCodexNameSettings(accounts) {
  const host = document.getElementById('codex-name-fields');
  const group = document.getElementById('codex-name-group');
  if (!host) return;
  const list = (Array.isArray(accounts) ? accounts : []).filter((account) => account && account.label);
  if (group) group.hidden = list.length === 0;
  const labels = list.map((account) => String(account.label));
  if (labels.length !== codexNameFieldLabels.length
      || labels.some((label, i) => label !== codexNameFieldLabels[i])) {
    host.innerHTML = list.map((account, index) => codexNameFieldHtml(account, index)).join('');
    codexNameFieldLabels = labels;
  }
  list.forEach((account, index) => {
    setInputValue(`codex-name-${index}`, account.customName || '');
  });
}

async function saveCodexAccountName(label, name) {
  if (!window.api || typeof window.api.setCodexAccountName !== 'function') return;
  const snap = await window.api.setCodexAccountName(label, name);
  applySnapshot(snap);
}

function renderNtfyStatus(config) {
  const el = document.getElementById('ntfy-status');
  if (!el) return;
  const topicUrl = config && config.topicUrl;
  const anyEnabled = !!(config && (config.notifyFiveHour || config.notifyWeekly));
  if (!anyEnabled) {
    el.textContent = '通知は未選択です。Topic URL は推測されにくいものを使ってください。';
  } else if (!topicUrl) {
    el.textContent = '通知を送るには ntfy の Topic URL が必要です。';
  } else {
    el.textContent = '通知予約が有効です。リセット時刻に ntfy へ送信します。';
  }
}

function renderNtfySettings(config = {}) {
  setInputValue('ntfy-topic-url', config.topicUrl);
  setInputValue('ntfy-access-token', config.accessToken);
  setChecked('ntfy-notify-five-hour', config.notifyFiveHour);
  setChecked('ntfy-notify-weekly', config.notifyWeekly);
  renderNtfyStatus(config);
}

async function saveNtfySettings(partial) {
  if (!window.api || typeof window.api.setNtfySettings !== 'function') return;
  const snap = await window.api.setNtfySettings(partial);
  applySnapshot(snap);
}

// Drive the window height from the actual rendered content so it fits with no
// scrollbar and no empty gap. We can't just request `contentHeight`: on
// fractional-DPI displays (125% / 150% / …) Electron's setContentSize lands a
// few px short of what we ask, and the shortfall is not even constant — it
// wobbles by a px or two between nearby heights as the device-pixel rounding
// falls differently. So this is a self-correcting loop: it measures the
// shortfall the window actually has (requested − viewport) and asks for
// `content + shortfall`, growing on any overflow and shrinking only when the
// gap is clearly bigger than the wobble. Because it observes the real
// shortfall it needs no per-DPI magic numbers.
let requestedHeight = 0;
// True while the main process is holding the window at the display limit. The
// content cannot fit, so `html.clamped` turns scrolling on and the loop below
// stops asking for more height (every request would be clamped right back).
let heightClamped = false;

// Upper bound on the DPI shortfall we believe. Measured values run from 5px
// (200%) to 11px (100%); a reading outside [0, MAX] means the viewport is not
// the result of our last request — the resize is still in flight — and acting
// on it would send a wildly wrong height.
const MAX_DPI_SHORTFALL = 24;
// Empty space below the content that is worth a shrink request. Must exceed
// the wobble of the shortfall between two nearby heights (≤2px at 125%) plus
// the sub-pixel drift of the content (<1px), otherwise a grow of exactly the
// observed overflow can land with a "gap" that triggers a shrink, which lands
// short again, and the loop never settles. Anything under this stays as an
// invisible sliver of padding.
const SHRINK_SLACK = 4;
// A window resize reaches us as a burst of resize events whose intermediate
// viewports are not the final one (the frame is accounted for a beat later),
// so a pass only runs once the viewport has held still for this long.
const SETTLE_MS = 50;
// A request that changes the height by 1px can land on the very same viewport
// as the previous one (the device-pixel rounding swallows it), in which case no
// resize event fires and nothing would re-run the loop. After every request a
// one-shot recheck runs a pass that acts regardless of plausibility — by then
// the resize has long landed — so the next pass can push further.
const RECHECK_DELAY_MS = 120;
let settleTimer = 0;
let recheckTimer = 0;
let forcePass = false;

function evaluateWindowHeight() {
  const force = forcePass;
  forcePass = false;
  if (!window.api || typeof window.api.reportContentHeight !== 'function') return;
  const el = document.querySelector('.container');
  if (!el) return;
  // `.container` is the only rendered box (body has no margin/padding), so its
  // border-box height is exactly the viewport height we need to show.
  const content = Math.ceil(el.getBoundingClientRect().height);
  const viewport = window.innerHeight;
  const overflow = Math.max(0, document.documentElement.scrollHeight - viewport);
  // Still overflowing at the display limit: the user scrolls instead, and we
  // send nothing so the loop cannot spin against a window that cannot grow.
  if (heightClamped && overflow > 0) return;
  // How far short of our last request the window really landed (once it has).
  // While clamped the window is known to sit at the ceiling, far below the
  // request, so that reading is the real one and the plausibility gate is off.
  const rawShortfall = requestedHeight - viewport;
  const landed = heightClamped || force
    || (rawShortfall >= 0 && rawShortfall <= MAX_DPI_SHORTFALL);

  let target = requestedHeight;
  if (requestedHeight === 0) {
    target = content;                    // first paint: no shortfall measured yet
  } else if (!landed) {
    return;                              // resize still in flight; the recheck re-runs us
  } else if (overflow > 0 || viewport - content >= SHRINK_SLACK) {
    // Requesting `content + shortfall` is what makes the viewport come out at
    // `content`. The cap only matters when the gate was bypassed (clamped, or
    // the forced recheck) and the reading may not reflect a landed request.
    const shortfall = Math.min(MAX_DPI_SHORTFALL, Math.max(0, rawShortfall));
    target = content + shortfall;
    // Growing must always make progress: when the shortfall wobbles up by a px
    // the exact request is what we already asked for, and once landed the
    // observed overflow is exactly how much further the window has to go.
    if (overflow > 0) target = Math.max(target, requestedHeight + overflow);
  }
  target = Math.ceil(Math.max(1, target));
  if (target !== requestedHeight) {
    requestedHeight = target;
    window.api.reportContentHeight(target);
    clearTimeout(recheckTimer);
    recheckTimer = setTimeout(() => { forcePass = true; evaluateWindowHeight(); }, RECHECK_DELAY_MS);
  }
}

// Entry point for every trigger. The first paint is measured right away (the
// window is still at its placeholder size and nothing is in flight); every
// later trigger waits for the viewport to settle so a burst of resize events
// is evaluated once, on its final value.
function syncWindowHeight() {
  if (requestedHeight === 0) {
    evaluateWindowHeight();
    return;
  }
  clearTimeout(settleTimer);
  settleTimer = setTimeout(evaluateWindowHeight, SETTLE_MS);
}

// Re-sync whenever the content reflows (data arrives, an error box appears, …)
// or the viewport changes (our own resize lands, or the window moves to a
// display with a different scale factor). Growing by the observed shortfall and
// shrinking only past SHRINK_SLACK converges in a couple of frames without
// oscillating.
function watchContentHeight() {
  const el = document.querySelector('.container');
  if (!el) return;
  if (window.api && typeof window.api.onContentClamped === 'function') {
    window.api.onContentClamped((clamped) => {
      heightClamped = clamped;
      document.documentElement.classList.toggle('clamped', clamped);
      // The scrollbar appearing (or leaving) reflows the content, and once the
      // clamp lifts the normal fit-to-content loop has to pick up again.
      syncWindowHeight();
    });
  }
  if (typeof ResizeObserver === 'function') {
    new ResizeObserver(() => syncWindowHeight()).observe(el);
  }
  window.addEventListener('resize', syncWindowHeight);
  syncWindowHeight();
}

function applySnapshot(payload) {
  if (!payload) return;
  applyTheme(payload.theme);
  const loginInProgress = payload.loginInProgress || {};
  renderService(document.querySelector('[data-body="claude"]'), payload.claude, loginInProgress.claude);
  renderCodexAccounts(payload.codexAccounts, loginInProgress.codex);
  renderCodexDefaultLogin(payload.codexAccounts, payload.codexDefaultAccount);
  renderCodexNameSettings(payload.codexAccounts);
  renderFooter(payload.fetchedAt, payload.appVersion);
  if (payload.settings) {
    const sel = document.getElementById('interval-select');
    if (sel && String(payload.settings.pollingIntervalSec) !== sel.value) {
      sel.value = String(payload.settings.pollingIntervalSec);
    }
    renderNtfySettings(payload.settings.ntfy || {});
  }
  const al = document.getElementById('auto-launch');
  if (al) {
    al.checked = !!payload.autoLaunchEnabled;
  }
}

async function init() {
  const snapshot = await window.api.getSnapshot();
  applySnapshot(snapshot);
  window.api.onSnapshot(applySnapshot);

  document.getElementById('settings-toggle').addEventListener('click', () => {
    const panel = document.getElementById('settings-panel');
    setSettingsPanelOpen(!!(panel && panel.hidden));
  });

  document.getElementById('refresh-btn').addEventListener('click', async () => {
    const snap = await window.api.refresh();
    applySnapshot(snap);
  });

  document.getElementById('interval-select').addEventListener('change', async (evt) => {
    const value = Number(evt.target.value);
    const snap = await window.api.setInterval(value);
    applySnapshot(snap);
  });

  document.getElementById('auto-launch').addEventListener('change', async (evt) => {
    const snap = await window.api.setAutoLaunch(evt.target.checked);
    applySnapshot(snap);
  });

  document.getElementById('ntfy-form').addEventListener('submit', (evt) => {
    evt.preventDefault();
  });

  // The rename inputs are rebuilt whenever the account set changes, so the
  // handler lives on the stable wrapper instead of the inputs.
  document.getElementById('codex-name-fields').addEventListener('change', (evt) => {
    const input = evt.target.closest('input[data-account-label]');
    if (!input) return;
    void saveCodexAccountName(input.getAttribute('data-account-label') || '', input.value);
  });

  document.getElementById('ntfy-topic-url').addEventListener('change', (evt) => {
    void saveNtfySettings({ topicUrl: evt.target.value });
  });

  document.getElementById('ntfy-access-token').addEventListener('change', (evt) => {
    void saveNtfySettings({ accessToken: evt.target.value });
  });

  document.getElementById('ntfy-notify-five-hour').addEventListener('change', (evt) => {
    void saveNtfySettings({ notifyFiveHour: evt.target.checked });
  });

  document.getElementById('ntfy-notify-weekly').addEventListener('change', (evt) => {
    void saveNtfySettings({ notifyWeekly: evt.target.checked });
  });

  document.querySelectorAll('[data-login="claude"]').forEach((btn) => {
    btn.addEventListener('click', () => {
      window.api.openLogin('claude');
    });
  });

  // The Codex sections are rebuilt whenever the account set changes, so the
  // 🔑 handler lives on the stable wrapper instead of the buttons.
  document.getElementById('codex-services').addEventListener('click', (evt) => {
    const btn = evt.target.closest('[data-login="codex"]');
    if (!btn) return;
    const accountId = btn.getAttribute('data-account-id') || '';
    window.api.openLogin('codex', accountId || undefined);
  });

  document.getElementById('codex-default-login-btn').addEventListener('click', (evt) => {
    const accountId = evt.currentTarget.getAttribute('data-account-id') || '';
    if (!accountId) return;
    window.api.openLogin('codex', accountId);
  });

  document.getElementById('quit-btn').addEventListener('click', () => {
    window.api.quit();
  });

  watchContentHeight();
}

window.addEventListener('DOMContentLoaded', init);
