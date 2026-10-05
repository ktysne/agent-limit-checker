'use strict';

const { accountDisplayName } = require('./codexHomes');

const USER_AGENT = 'agent-limit-checker/1.0';
const DUE_GRACE_MS = 30 * 60 * 1000;
const RESET_CREDIT_WARNING_MS = 5 * 60 * 60 * 1000;
const RETRY_DELAY_MS = 5 * 1000;
const MAX_RETRY_ATTEMPTS = 3;
const MAX_TIMER_DELAY_MS = 2_147_483_647;

const RESET_BUCKETS = [
  { bucketId: 'fiveHour', windowType: 'fiveHour', windowLabel: '5時間' },
  { bucketId: 'weekly', windowType: 'weekly', windowLabel: '週次' },
];

// The notifiable services behind one snapshot. Claude is a single service;
// Codex contributes one service per configured account, so a two-account setup
// gets two independent sets of reset notifications. `serviceId` carries the
// account id, which keeps the dedup keys of two accounts from colliding (one
// account's notification must never suppress the other's).
function servicesForSnapshot(snapshot) {
  const services = [{
    serviceId: 'claude',
    serviceLabel: 'Claude Code',
    buckets: RESET_BUCKETS,
    usage: snapshot && snapshot.claude,
  }];
  const accounts = Array.isArray(snapshot && snapshot.codexAccounts) ? snapshot.codexAccounts : [];
  for (const account of accounts) {
    services.push({
      serviceId: `codex:${account.id}`,
      // The snapshot carries the resolved name (the user's override included);
      // the fallback covers a snapshot built without that decoration, e.g. in
      // tests.
      serviceLabel: account.displayName || accountDisplayName(account, accounts),
      buckets: RESET_BUCKETS,
      usage: account,
    });
  }
  return services;
}

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
    notifyResetCreditsExpiry: !!raw.notifyResetCreditsExpiry,
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
  return !!(config && (config.notifyFiveHour || config.notifyWeekly || config.notifyResetCreditsExpiry));
}

function validResetAt(value) {
  return Number.isFinite(value) && value > 0;
}

function collectResetEvents(snapshot, settings) {
  const config = ntfyFromAppSettings(settings);
  if (!hasAnyNotificationEnabled(config)) return [];
  const events = [];

  for (const service of servicesForSnapshot(snapshot)) {
    const svc = service.usage;
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

    // Per-model weekly caps (e.g. Fable) arrive as an array whose members
    // carry their own model label. Treat each as a weekly window under the
    // same opt-in as the overall weekly reset.
    if (isWindowTypeEnabled(config, 'weekly') && Array.isArray(svc.data.weeklyScoped)) {
      const labelSeen = new Map();
      svc.data.weeklyScoped.forEach((scoped) => {
        if (!scoped || !validResetAt(scoped.resetsAt)) return;
        // Key off the stable scope id when the API supplies one, so a display
        // rename doesn't re-fire the notification. Otherwise fall back to the
        // label, appending an ordinal only when the same label repeats. That
        // stays stable across API reordering (unlike a bare index) yet still
        // guarantees two same-labelled scopes never collide onto one key,
        // which would silently drop one model's reset. The label is
        // display-only, in windowLabel.
        let scopeKey;
        if (scoped.id) {
          scopeKey = scoped.id;
        } else {
          const n = labelSeen.get(scoped.label) || 0;
          labelSeen.set(scoped.label, n + 1);
          scopeKey = n === 0 ? scoped.label : `${scoped.label}#${n}`;
        }
        events.push({
          key: `${service.serviceId}:weeklyScoped:${scopeKey}`,
          serviceId: service.serviceId,
          serviceLabel: service.serviceLabel,
          bucketId: `weeklyScoped:${scopeKey}`,
          windowType: 'weekly',
          windowLabel: `週次 (${scoped.label})`,
          resetsAt: scoped.resetsAt,
        });
      });
    }
  }

  return events.sort((a, b) => a.resetsAt - b.resetsAt || a.key.localeCompare(b.key));
}

function collectResetCreditsExpiryEvents(snapshot, settings, now = Date.now()) {
  const config = ntfyFromAppSettings(settings);
  const accounts = Array.isArray(snapshot && snapshot.codexAccounts) ? snapshot.codexAccounts : [];
  if (!config.notifyResetCreditsExpiry) return [];

  return accounts.flatMap((account) => {
    const resetCredits = account && account.ok && account.data && account.data.resetCredits;
    if (!resetCredits || !Number.isFinite(resetCredits.availableCount) || resetCredits.availableCount <= 0) {
      return [];
    }

    const expiresAt = resetCredits.nextExpiresAt;
    if (!validResetAt(expiresAt) || expiresAt <= now) return [];

    const serviceId = `codex:${account.id}`;
    return [{
      key: `${serviceId}:resetCredits:${expiresAt}`,
      kind: 'resetCreditsExpiry',
      serviceId,
      serviceLabel: account.displayName || accountDisplayName(account, accounts),
      availableCount: resetCredits.availableCount,
      expiresAt,
      dueAt: expiresAt - RESET_CREDIT_WARNING_MS,
    }];
  }).sort((a, b) => a.expiresAt - b.expiresAt || a.key.localeCompare(b.key));
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

function buildResetCreditsExpiryMessage(event) {
  return {
    title: 'Agent Limit Checker',
    message: `${event.serviceLabel} のリセット権の有効期限が5時間以内に到来します。\n残り: ${event.availableCount} 回\n最も早い期限: ${formatResetTime(event.expiresAt)}`,
    priority: 'default',
    tags: 'hourglass',
  };
}

function eventIdentity(event) {
  const timestamp = event.kind === 'resetCreditsExpiry' ? event.expiresAt : event.resetsAt;
  return `${event.key}:${event.kind || 'reset'}:${timestamp}`;
}

function isEventEnabled(config, event) {
  if (event.kind === 'resetCreditsExpiry') return !!config.notifyResetCreditsExpiry;
  return isWindowTypeEnabled(config, event.windowType);
}

function canRetryEvent(event, retryAt) {
  if (event.kind === 'resetCreditsExpiry') return retryAt < event.expiresAt;
  return retryAt <= event.resetsAt + DUE_GRACE_MS;
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
    this.inFlight = new Map();
    this.desiredEvents = new Map();
  }

  update(snapshot) {
    const settings = this.getSettings();
    const config = ntfyFromAppSettings(settings);
    const now = this.now();
    const desired = new Map();

    if (hasAnyNotificationEnabled(config)) {
      const events = [
        ...collectResetEvents(snapshot, settings),
        ...collectResetCreditsExpiryEvents(snapshot, settings, now),
      ];
      for (const event of events) {
        const sentAt = this.sentResets.get(event.key);
        const eventAt = event.kind === 'resetCreditsExpiry' ? event.expiresAt : event.resetsAt;
        if (sentAt === eventAt) continue;
        if (event.kind !== 'resetCreditsExpiry' && event.resetsAt < now - DUE_GRACE_MS) continue;
        desired.set(event.key, event);
      }
    }

    this.desiredEvents = desired;
    for (const event of desired.values()) {
      const identity = eventIdentity(event);
      const inFlight = this.inFlight.get(identity);
      if (inFlight) {
        inFlight.event = event;
        continue;
      }
      const existing = this.timers.get(event.key);
      if (existing && eventIdentity(existing.event) === identity) {
        existing.event = event;
        continue;
      }
      this.cancel(event.key);
      this.schedule(event, 0);
    }

    for (const [key, existing] of this.timers.entries()) {
      const event = desired.get(key);
      if (!event || eventIdentity(event) !== eventIdentity(existing.event)) {
        this.cancel(key);
      }
    }
  }

  schedule(event, attempts, delayOverride = null) {
    const now = this.now();
    const dueAt = event.dueAt == null ? event.resetsAt : event.dueAt;
    const dueDelay = Math.max(0, dueAt - now);
    const delay = delayOverride == null
      ? Math.min(dueDelay, MAX_TIMER_DELAY_MS)
      : Math.max(0, delayOverride);
    const scheduled = { event, attempts, timer: null };
    this.timers.set(event.key, scheduled);
    scheduled.timer = this.setTimer(() => {
      if (this.timers.get(event.key) !== scheduled) return;
      void this.fire(event.key);
    }, delay);
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
    const dueAt = event.dueAt == null ? event.resetsAt : event.dueAt;

    if (dueAt > now) {
      this.schedule(event, attempts);
      return;
    }

    if (event.kind === 'resetCreditsExpiry' && now >= event.expiresAt) {
      this.sentResets.set(key, event.expiresAt);
      return;
    }

    if (event.kind !== 'resetCreditsExpiry' && now > event.resetsAt + DUE_GRACE_MS) {
      this.sentResets.set(key, event.resetsAt);
      this.logger.warn('[ntfy] skipped stale reset notification', key);
      return;
    }

    const settings = this.getSettings();
    const config = ntfyFromAppSettings(settings);
    if (!isEventEnabled(config, event)) return;
    if (!hasNtfyTopic(config)) {
      this.logger.warn('[ntfy] reset notification skipped; topic URL missing');
      return;
    }

    const identity = eventIdentity(event);
    if (this.inFlight.has(identity)) return;
    const flight = { event };
    this.inFlight.set(identity, flight);
    try {
      const message = event.kind === 'resetCreditsExpiry'
        ? buildResetCreditsExpiryMessage(event)
        : buildResetMessage(event);
      const result = await this.sendMessage(config, message);
      this.sentResets.set(key, event.kind === 'resetCreditsExpiry' ? event.expiresAt : event.resetsAt);
      this.logger.info('[ntfy] reset notification sent', key, result && result.id);
    } catch (err) {
      this.logger.error('[ntfy] reset notification failed', err);
      if (err && err.retryable && attempts < MAX_RETRY_ATTEMPTS) {
        const retryAt = this.now() + RETRY_DELAY_MS;
        const currentEvent = this.desiredEvents.get(key);
        const currentConfig = ntfyFromAppSettings(this.getSettings());
        if (currentEvent
          && eventIdentity(currentEvent) === identity
          && isEventEnabled(currentConfig, currentEvent)
          && canRetryEvent(currentEvent, retryAt)) {
          this.schedule(currentEvent, attempts + 1, RETRY_DELAY_MS);
        }
      }
    } finally {
      if (this.inFlight.get(identity) === flight) this.inFlight.delete(identity);
    }
  }

  dispose() {
    this.desiredEvents.clear();
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
  collectResetCreditsExpiryEvents,
  buildResetMessage,
  buildResetCreditsExpiryMessage,
  sendNtfyMessage,
  NtfyResetNotifier,
  _private: {
    hasNtfyTopic,
    hasAnyNotificationEnabled,
    isWindowTypeEnabled,
    formatResetTime,
  },
};
