'use strict';

const USER_AGENT = 'agent-limit-checker/1.0';
const DUE_GRACE_MS = 30 * 60 * 1000;
const RETRY_DELAY_MS = 5 * 1000;
const MAX_RETRY_ATTEMPTS = 3;
const MAX_TIMER_DELAY_MS = 2_147_483_647;

const SERVICE_WINDOWS = [
  {
    serviceId: 'claude',
    serviceLabel: 'Claude Code',
    snapshotKey: 'claude',
    buckets: [
      { bucketId: 'fiveHour', windowType: 'fiveHour', windowLabel: '5時間' },
      { bucketId: 'weekly', windowType: 'weekly', windowLabel: '週次' },
      { bucketId: 'weeklySonnet', windowType: 'weekly', windowLabel: '週次 (Sonnet)' },
    ],
  },
  {
    serviceId: 'codex',
    serviceLabel: 'Codex',
    snapshotKey: 'codex',
    buckets: [
      { bucketId: 'fiveHour', windowType: 'fiveHour', windowLabel: '5時間' },
      { bucketId: 'weekly', windowType: 'weekly', windowLabel: '週次' },
    ],
  },
];

function cleanString(value) {
  if (typeof value !== 'string') return '';
  return value.trim();
}

function normalizeNtfySettings(value) {
  const raw = value && typeof value === 'object' ? value : {};
  return {
    topicUrl: cleanString(raw.topicUrl),
    accessToken: cleanString(raw.accessToken),
    notifyFiveHour: !!raw.notifyFiveHour,
    notifyWeekly: !!raw.notifyWeekly,
  };
}

function ntfyFromAppSettings(settings) {
  if (settings && typeof settings === 'object' && Object.hasOwn(settings, 'ntfy')) {
    return normalizeNtfySettings(settings.ntfy);
  }
  return normalizeNtfySettings(settings);
}

function normalizeTopicUrl(value) {
  const raw = cleanString(value).replace(/\/+$/, '');
  if (!raw) return '';
  try {
    const url = new URL(raw);
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return '';
    if (url.pathname === '/' || url.pathname === '') return '';
    url.hash = '';
    return url.toString().replace(/\/+$/, '');
  } catch {
    return '';
  }
}

function hasNtfyTopic(config) {
  return !!normalizeTopicUrl(config && config.topicUrl);
}

function isWindowTypeEnabled(config, windowType) {
  if (!config) return false;
  if (windowType === 'fiveHour') return !!config.notifyFiveHour;
  if (windowType === 'weekly') return !!config.notifyWeekly;
  return false;
}

function hasAnyNotificationEnabled(config) {
  return !!(config && (config.notifyFiveHour || config.notifyWeekly));
}

function validResetAt(value) {
  return Number.isFinite(value) && value > 0;
}

function collectResetEvents(snapshot, settings) {
  const config = ntfyFromAppSettings(settings);
  if (!hasAnyNotificationEnabled(config)) return [];
  const events = [];

  for (const service of SERVICE_WINDOWS) {
    const svc = snapshot && snapshot[service.snapshotKey];
    if (!svc || !svc.ok || !svc.data) continue;

    for (const bucket of service.buckets) {
      if (!isWindowTypeEnabled(config, bucket.windowType)) continue;
      const limit = svc.data[bucket.bucketId];
      if (!limit || !validResetAt(limit.resetsAt)) continue;
      events.push({
        key: `${service.serviceId}:${bucket.bucketId}`,
        serviceId: service.serviceId,
        serviceLabel: service.serviceLabel,
        bucketId: bucket.bucketId,
        windowType: bucket.windowType,
        windowLabel: bucket.windowLabel,
        resetsAt: limit.resetsAt,
      });
    }
  }

  return events.sort((a, b) => a.resetsAt - b.resetsAt || a.key.localeCompare(b.key));
}

function formatResetTime(resetsAt) {
  return new Date(resetsAt).toLocaleString('ja-JP', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function buildResetMessage(event) {
  const resetTime = formatResetTime(event.resetsAt);
  return {
    title: 'Agent Limit Checker',
    message: `${event.serviceLabel} の${event.windowLabel}リセット時刻です。\n${resetTime}`,
    priority: 'default',
    tags: 'hourglass',
  };
}

function makeNtfyError(message, options = {}) {
  const err = new Error(message);
  err.code = 'ntfy_api_error';
  err.status = options.status || null;
  err.retryable = !!options.retryable;
  err.request = options.request || null;
  return err;
}

async function parseJsonResponse(res) {
  try {
    return await res.json();
  } catch {
    return null;
  }
}

async function sendNtfyMessage(configInput, message, fetchImpl = globalThis.fetch) {
  const config = normalizeNtfySettings(configInput);
  const topicUrl = normalizeTopicUrl(config.topicUrl);
  if (!topicUrl) {
    throw makeNtfyError('ntfy topic URL is not configured');
  }
  if (typeof fetchImpl !== 'function') {
    throw makeNtfyError('fetch is not available in this runtime', { retryable: true });
  }

  const headers = {
    'Content-Type': 'text/plain; charset=utf-8',
    'User-Agent': USER_AGENT,
    Title: String(message.title || ''),
  };
  if (message.priority) headers.Priority = String(message.priority);
  if (message.tags) headers.Tags = String(message.tags);
  if (config.accessToken) headers.Authorization = `Bearer ${config.accessToken}`;

  let res;
  try {
    res = await fetchImpl(topicUrl, {
      method: 'POST',
      headers,
      body: String(message.message || ''),
    });
  } catch (err) {
    throw makeNtfyError(`ntfy request failed: ${err.message || String(err)}`, {
      retryable: true,
    });
  }

  const payload = await parseJsonResponse(res);
  if (res.ok) {
    return {
      id: payload && payload.id ? payload.id : null,
      topic: payload && payload.topic ? payload.topic : null,
    };
  }

  throw makeNtfyError(
    (payload && (payload.error || payload.message)) || `ntfy API error (${res.status})`,
    {
      status: res.status,
      retryable: res.status >= 500 || res.status === 429 || res.status === 0,
      request: payload && payload.id,
    },
  );
}

class NtfyResetNotifier {
  constructor(options = {}) {
    this.getSettings = options.getSettings || (() => ({}));
    this.sendMessage = options.sendMessage || sendNtfyMessage;
    this.logger = options.logger || console;
    this.now = options.now || Date.now;
    this.setTimer = options.setTimer || setTimeout;
    this.clearTimer = options.clearTimer || clearTimeout;
    this.timers = new Map();
    this.sentResets = new Map();
  }

  update(snapshot) {
    const settings = this.getSettings();
    const config = ntfyFromAppSettings(settings);
    const desired = new Map();
    const now = this.now();

    if (hasAnyNotificationEnabled(config)) {
      for (const event of collectResetEvents(snapshot, settings)) {
        if (this.sentResets.get(event.key) === event.resetsAt) continue;
        if (event.resetsAt < now - DUE_GRACE_MS) continue;
        desired.set(event.key, event);
        const existing = this.timers.get(event.key);
        if (existing && existing.event.resetsAt === event.resetsAt) continue;
        this.cancel(event.key);
        this.schedule(event, 0);
      }
    }

    for (const [key, existing] of this.timers.entries()) {
      const event = desired.get(key);
      if (!event || event.resetsAt !== existing.event.resetsAt) {
        this.cancel(key);
      }
    }
  }

  schedule(event, attempts, delayOverride = null) {
    const now = this.now();
    const dueDelay = Math.max(0, event.resetsAt - now);
    const delay = delayOverride == null
      ? Math.min(dueDelay, MAX_TIMER_DELAY_MS)
      : Math.max(0, delayOverride);
    const timer = this.setTimer(() => {
      void this.fire(event.key);
    }, delay);
    this.timers.set(event.key, { event, attempts, timer });
  }

  cancel(key) {
    const existing = this.timers.get(key);
    if (existing) {
      this.clearTimer(existing.timer);
      this.timers.delete(key);
    }
  }

  async fire(key) {
    const scheduled = this.timers.get(key);
    if (!scheduled) return;
    this.timers.delete(key);
    const { event, attempts } = scheduled;
    const now = this.now();

    if (event.resetsAt > now) {
      this.schedule(event, attempts);
      return;
    }

    if (now > event.resetsAt + DUE_GRACE_MS) {
      this.sentResets.set(key, event.resetsAt);
      this.logger.warn('[ntfy] skipped stale reset notification', key);
      return;
    }

    const settings = this.getSettings();
    const config = ntfyFromAppSettings(settings);
    if (!isWindowTypeEnabled(config, event.windowType)) return;
    if (!hasNtfyTopic(config)) {
      this.logger.warn('[ntfy] reset notification skipped; topic URL missing');
      return;
    }

    try {
      const result = await this.sendMessage(config, buildResetMessage(event));
      this.sentResets.set(key, event.resetsAt);
      this.logger.info('[ntfy] reset notification sent', key, result && result.id);
    } catch (err) {
      this.logger.error('[ntfy] reset notification failed', err);
      if (err && err.retryable && attempts < MAX_RETRY_ATTEMPTS) {
        const retryAt = this.now() + RETRY_DELAY_MS;
        if (retryAt <= event.resetsAt + DUE_GRACE_MS) {
          this.schedule(event, attempts + 1, RETRY_DELAY_MS);
        }
      }
    }
  }

  dispose() {
    for (const key of this.timers.keys()) {
      this.cancel(key);
    }
  }
}

module.exports = {
  DUE_GRACE_MS,
  RETRY_DELAY_MS,
  normalizeNtfySettings,
  normalizeTopicUrl,
  collectResetEvents,
  buildResetMessage,
  sendNtfyMessage,
  NtfyResetNotifier,
  _private: {
    hasNtfyTopic,
    hasAnyNotificationEnabled,
    isWindowTypeEnabled,
    formatResetTime,
  },
};
