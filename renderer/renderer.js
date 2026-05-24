'use strict';

const ERROR_HINTS = {
  claude_credentials_missing: '`claude login` を実行してから再度更新してください。',
  claude_unauthorized: 'OAuth トークンが無効です。`claude login` で再ログインしてください。',
  codex_cli_missing: 'PowerShell で `npm i -g @openai/codex` を実行してください。',
};

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
  if (!resetsAtMs) return '';
  if (resetsAtMs <= now) return 'まもなくリセット';
  const diffSec = Math.round((resetsAtMs - now) / 1000);
  const hours = Math.floor(diffSec / 3600);
  const mins = Math.floor((diffSec % 3600) / 60);
  const rel = hours > 0 ? `${hours}時間${mins}分` : `${mins}分`;
  const absolute = new Date(resetsAtMs).toLocaleString([], {
    hour: '2-digit',
    minute: '2-digit',
  });
  return `あと ${rel} (${absolute} リセット)`;
}

function renderBucket(label, limit, primary = false) {
  if (!limit) {
    if (primary) {
      return `<div class="bucket"><div class="bucket-row"><span class="bucket-label">${label}</span><span class="bucket-value">N/A</span></div></div>`;
    }
    return '';
  }
  const cls = classify(limit.utilization);
  const pct = percent(limit.utilization);
  const width = Math.min(100, Math.max(0, (limit.utilization || 0) * 100));
  if (primary) {
    return `
      <div class="bucket">
        <div class="bucket-row">
          <span class="bucket-label">${label}</span>
          <span class="bucket-value ${cls}">${pct}</span>
        </div>
        <div class="progress"><div class="progress-fill ${cls}" style="width:${width}%"></div></div>
        <div class="reset-text">${resetText(limit.resetsAt)}</div>
      </div>
    `;
  }
  return `
    <div class="secondary-row">
      <span class="bucket-label">${label}</span>
      <span class="bucket-value ${cls}">${pct}</span>
    </div>
  `;
}

function renderService(target, svc) {
  const body = document.querySelector(`[data-body="${target}"]`);
  if (!body) return;
  if (!svc) {
    body.innerHTML = '<div class="loading">取得中…</div>';
    return;
  }
  if (!svc.ok) {
    const err = svc.error || {};
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
  html += renderBucket('5時間', usage.fiveHour, true) || '<div class="bucket"><div class="bucket-row"><span class="bucket-label">5時間ウィンドウのデータがありません</span></div></div>';
  if (usage.weekly) html += renderBucket('週次', usage.weekly, false);
  if (usage.weeklySonnet) html += renderBucket('週次 (Sonnet)', usage.weeklySonnet, false);
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

function renderFooter(fetchedAt) {
  const el = document.getElementById('last-updated');
  if (!el) return;
  if (!fetchedAt) {
    el.textContent = '';
    return;
  }
  const t = new Date(fetchedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  el.textContent = `最終更新: ${t}`;
}

function applySnapshot(payload) {
  if (!payload) return;
  renderService('claude', payload.claude);
  renderService('codex', payload.codex);
  renderFooter(payload.fetchedAt);
  if (payload.settings) {
    const sel = document.getElementById('interval-select');
    if (sel && String(payload.settings.pollingIntervalSec) !== sel.value) {
      sel.value = String(payload.settings.pollingIntervalSec);
    }
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

  document.querySelectorAll('[data-login]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const target = btn.getAttribute('data-login');
      window.api.openLogin(target);
    });
  });

  document.getElementById('quit-btn').addEventListener('click', () => {
    window.api.quit();
  });
}

window.addEventListener('DOMContentLoaded', init);
