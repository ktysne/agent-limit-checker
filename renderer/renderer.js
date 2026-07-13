'use strict';

const ERROR_HINTS = {
  claude_credentials_missing: '🔑 ボタンを押すと再ログインできます。完了すると自動で復帰します。',
  claude_unauthorized: 'OAuth トークンが無効です。🔑 ボタンから再ログインすると自動で復帰します。',
  claude_refresh_unconfigured: '自動 refresh は未設定です。🔑 ボタンから再ログインすると自動で復帰します。',
  claude_refresh_token_missing: 'refresh token がありません。🔑 ボタンから再ログインすると自動で復帰します。',
  codex_cli_missing: 'PowerShell で `npm i -g @openai/codex` を実行してください。',
  codex_rpc_error: '🔑 ボタンを押すと再ログインできます。完了すると自動で復帰します。',
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

function renderService(target, svc, loginInProgress) {
  const body = document.querySelector(`[data-body="${target}"]`);
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
  body.innerHTML = html;
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
// few px short of what we ask, so a naive request still leaves the content
// overflowing. Instead this is a self-correcting loop — it watches whether the
// content actually overflows the viewport and grows the request until it
// doesn't, then settles. Because it observes real overflow it needs no
// per-DPI magic numbers.
let requestedHeight = 0;

function syncWindowHeight() {
  if (!window.api || typeof window.api.reportContentHeight !== 'function') return;
  const el = document.querySelector('.container');
  if (!el) return;
  // `.container` is the only rendered box (body has no margin/padding), so its
  // border-box height is exactly the viewport height we need to show.
  const content = Math.ceil(el.getBoundingClientRect().height);
  const viewport = window.innerHeight;
  const overflow = Math.max(0, document.documentElement.scrollHeight - viewport);

  let target = requestedHeight;
  if (content > requestedHeight && content > viewport) {
    target = content;                    // first paint, or content grew past us
  } else if (overflow > 0) {
    target = requestedHeight + overflow; // window too short — absorb the DPI deficit
  } else if (viewport - content >= 2) {
    target = content;                    // window taller than content — close the gap
  }
  target = Math.ceil(Math.max(1, target));
  if (target !== requestedHeight) {
    requestedHeight = target;
    window.api.reportContentHeight(target);
  }
}

// Re-sync whenever the content reflows (data arrives, an error box appears, …)
// or the viewport changes (our own resize lands, or the window moves to a
// display with a different scale factor). The monotonic grow/shrink with a 2px
// hysteresis converges in a couple of frames without oscillating.
function watchContentHeight() {
  const el = document.querySelector('.container');
  if (!el) return;
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
  renderService('claude', payload.claude, loginInProgress.claude);
  renderService('codex', payload.codex, loginInProgress.codex);
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

  document.querySelectorAll('[data-login]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const target = btn.getAttribute('data-login');
      window.api.openLogin(target);
    });
  });

  document.getElementById('quit-btn').addEventListener('click', () => {
    window.api.quit();
  });

  watchContentHeight();
}

window.addEventListener('DOMContentLoaded', init);
