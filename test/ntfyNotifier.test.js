'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const {
  collectResetEvents,
  buildResetMessage,
  normalizeTopicUrl,
  sendNtfyMessage,
  NtfyResetNotifier,
} = require('../src/ntfyNotifier');

// Account ids are normalized absolute home paths; the tests only need them to
// be stable and distinct.
const CODEX_DEFAULT_ID = '/home/me/.codex';
const CODEX_REVIEW_ID = '/home/me/.codex-review';

const silentLogger = {
  info() {},
  warn() {},
  error() {},
};

function makeFakeClock(initialNow) {
  let currentTime = initialNow;
  const timers = [];
  return {
    timers,
    now: () => currentTime,
    setNow(value) {
      currentTime = value;
    },
    setTimer(fn, delay) {
      const timer = { fn, delay, cleared: false, fired: false };
      timers.push(timer);
      return timer;
    },
    clearTimer(timer) {
      timer.cleared = true;
    },
    run(timer) {
      timer.fired = true;
      timer.fn();
    },
    activeTimers() {
      return timers.filter((timer) => !timer.cleared && !timer.fired);
    },
  };
}

function makeResetCreditsAccount(id, displayName, availableCount, nextExpiresAt) {
  return {
    id,
    displayName,
    ok: true,
    data: { resetCredits: { availableCount, nextExpiresAt } },
  };
}

function makeCreditNotifier(clock, settings, sendMessage) {
  return new NtfyResetNotifier({
    getSettings: () => settings,
    sendMessage,
    now: clock.now,
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    logger: silentLogger,
  });
}

async function flushNotifier() {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
}

function sampleSnapshot(now) {
  return {
    claude: {
      ok: true,
      data: {
        fiveHour: { utilization: 0.2, resetsAt: now + 60_000 },
        weekly: { utilization: 0.4, resetsAt: now + 7 * 86_400_000 },
        weeklyScoped: [{ id: null, label: 'Fable', utilization: 0.1, resetsAt: now + 6 * 86_400_000 }],
      },
    },
    codexAccounts: [
      {
        id: CODEX_DEFAULT_ID,
        label: '.codex',
        home: CODEX_DEFAULT_ID,
        isDefault: true,
        ok: true,
        data: {
          fiveHour: { utilization: 0.3, resetsAt: now + 120_000 },
          weekly: { utilization: 0.5, resetsAt: now + 5 * 86_400_000 },
        },
      },
    ],
  };
}

test('collectResetEvents respects five-hour and weekly opt-in settings', () => {
  const now = 1_800_000_000_000;
  const settings = {
    ntfy: {
      notifyFiveHour: false,
      notifyWeekly: true,
    },
  };

  assert.deepEqual(
    collectResetEvents(sampleSnapshot(now), settings).map((event) => event.key),
    [`codex:${CODEX_DEFAULT_ID}:weekly`, 'claude:weeklyScoped:Fable', 'claude:weekly'],
  );
});

test('collectResetEvents keeps two Codex accounts apart and labels them', () => {
  const now = 1_800_000_000_000;
  const snapshot = {
    claude: null,
    codexAccounts: [
      {
        id: CODEX_DEFAULT_ID,
        label: '.codex',
        isDefault: true,
        ok: true,
        data: { fiveHour: { utilization: 0.3, resetsAt: now + 60_000 } },
      },
      {
        id: CODEX_REVIEW_ID,
        label: '.codex-review',
        isDefault: false,
        ok: true,
        data: { fiveHour: { utilization: 0.1, resetsAt: now + 120_000 } },
      },
    ],
  };

  const events = collectResetEvents(snapshot, { ntfy: { notifyFiveHour: true } });
  assert.deepEqual(events.map((event) => event.key), [
    `codex:${CODEX_DEFAULT_ID}:fiveHour`,
    `codex:${CODEX_REVIEW_ID}:fiveHour`,
  ]);
  assert.deepEqual(events.map((event) => event.serviceLabel), [
    'Codex (.codex)',
    'Codex (.codex-review)',
  ]);
});

test('collectResetEvents keeps the plain "Codex" label for a single account', () => {
  const now = 1_800_000_000_000;
  const events = collectResetEvents(sampleSnapshot(now), { ntfy: { notifyFiveHour: true } });
  const codex = events.find((event) => event.serviceId.startsWith('codex:'));

  assert.equal(codex.serviceLabel, 'Codex');
  assert.match(buildResetMessage(codex).message, /^Codex の5時間リセット時刻です。/);
});

test('collectResetEvents labels an account by the display name the snapshot carries', () => {
  const now = 1_800_000_000_000;
  const snapshot = sampleSnapshot(now);
  snapshot.codexAccounts[0].displayName = 'Codex Sub';

  const events = collectResetEvents(snapshot, { ntfy: { notifyFiveHour: true } });
  const codex = events.find((event) => event.serviceId.startsWith('codex:'));

  assert.equal(codex.serviceLabel, 'Codex Sub');
  assert.match(buildResetMessage(codex).message, /^Codex Sub の5時間リセット時刻です。/);
});

test('collectResetEvents keys scoped weekly events off a stable scope id when present', () => {
  const now = 1_800_000_000_000;
  const snapshot = {
    claude: {
      ok: true,
      data: {
        weeklyScoped: [
          { id: 'claude-fable-5', label: 'Fable', utilization: 0.3, resetsAt: now + 6 * 86_400_000 },
          // No id and a shared label → the ordinal keeps the keys apart so
          // neither reset notification is silently deduped away.
          { id: null, label: 'スコープ', utilization: 0.1, resetsAt: now + 6 * 86_400_000 },
          { id: null, label: 'スコープ', utilization: 0.2, resetsAt: now + 5 * 86_400_000 },
        ],
      },
    },
  };
  const keys = collectResetEvents(snapshot, { ntfy: { notifyWeekly: true } }).map((e) => e.key);
  assert.deepEqual(new Set(keys), new Set([
    'claude:weeklyScoped:claude-fable-5', // stable id preferred
    'claude:weeklyScoped:スコープ',        // first of a repeated label
    'claude:weeklyScoped:スコープ#1',      // ordinal disambiguates the collision
  ]));
});

test('normalizeTopicUrl accepts topic URLs and rejects missing topics', () => {
  assert.equal(normalizeTopicUrl('https://ntfy.sh/agent_limit_checker'), 'https://ntfy.sh/agent_limit_checker');
  assert.equal(normalizeTopicUrl('https://ntfy.sh/agent_limit_checker/'), 'https://ntfy.sh/agent_limit_checker');
  assert.equal(normalizeTopicUrl('https://ntfy.sh/'), '');
  assert.equal(normalizeTopicUrl('not a url'), '');
});

test('buildResetMessage includes the service, reset window, and ntfy metadata', () => {
  const resetsAt = Date.parse('2026-06-12T18:30:00+09:00');
  const message = buildResetMessage({
    serviceLabel: 'Codex',
    windowLabel: '5時間',
    resetsAt,
  });

  assert.equal(message.title, 'Agent Limit Checker');
  assert.match(message.message, /Codex の5時間リセット時刻です。/);
  assert.equal(message.priority, 'default');
  assert.equal(message.tags, 'hourglass');
});

test('sendNtfyMessage posts text with ntfy headers to the topic URL', async () => {
  const calls = [];
  const fetchImpl = async (url, options) => {
    calls.push({ url, options });
    return {
      ok: true,
      status: 200,
      json: async () => ({ id: 'msg-1', topic: 'agent_limit_checker' }),
    };
  };

  const result = await sendNtfyMessage(
    { topicUrl: 'https://ntfy.sh/agent_limit_checker', accessToken: 'tk_secret' },
    { title: 'Title', message: 'Body', priority: 'default', tags: 'hourglass' },
    fetchImpl,
  );

  assert.deepEqual(result, { id: 'msg-1', topic: 'agent_limit_checker' });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, 'https://ntfy.sh/agent_limit_checker');
  assert.equal(calls[0].options.method, 'POST');
  assert.equal(calls[0].options.headers['Content-Type'], 'text/plain; charset=utf-8');
  assert.equal(calls[0].options.headers.Title, 'Title');
  assert.equal(calls[0].options.headers.Priority, 'default');
  assert.equal(calls[0].options.headers.Tags, 'hourglass');
  assert.equal(calls[0].options.headers.Authorization, 'Bearer tk_secret');
  assert.equal(calls[0].options.body, 'Body');
});

test('sendNtfyMessage marks rate-limit responses as retryable', async () => {
  const fetchImpl = async () => ({
    ok: false,
    status: 429,
    json: async () => ({ error: 'rate limit exceeded', id: 'msg-2' }),
  });

  await assert.rejects(
    () => sendNtfyMessage(
      { topicUrl: 'https://ntfy.sh/agent_limit_checker' },
      { title: 'Title', message: 'Body' },
      fetchImpl,
    ),
    {
      code: 'ntfy_api_error',
      status: 429,
      retryable: true,
      request: 'msg-2',
      message: 'rate limit exceeded',
    },
  );
});

test('NtfyResetNotifier sends one notification per reset timestamp', async () => {
  let now = 1_800_000_000_000;
  const timers = [];
  const sent = [];
  const notifier = new NtfyResetNotifier({
    getSettings: () => ({
      ntfy: {
        topicUrl: 'https://ntfy.sh/agent_limit_checker',
        notifyFiveHour: true,
        notifyWeekly: false,
      },
    }),
    sendMessage: async (_config, message) => {
      sent.push(message);
      return { id: 'msg-3' };
    },
    now: () => now,
    setTimer: (fn, delay) => {
      const timer = { fn, delay, cleared: false };
      timers.push(timer);
      return timer;
    },
    clearTimer: (timer) => {
      timer.cleared = true;
    },
    logger: silentLogger,
  });

  const snapshot = {
    claude: { ok: true, data: { fiveHour: { utilization: 0.2, resetsAt: now + 1000 } } },
    codexAccounts: [],
  };

  notifier.update(snapshot);
  assert.equal(timers.length, 1);
  assert.equal(timers[0].delay, 1000);

  now += 1000;
  timers[0].fn();
  await Promise.resolve();
  await Promise.resolve();

  assert.equal(sent.length, 1);
  assert.equal(sent[0].title, 'Agent Limit Checker');

  notifier.update(snapshot);
  assert.equal(timers.length, 1);
});

test('reset-credit expiry warnings schedule five hours before expiry per account', async () => {
  const now = 1_800_000_000_000;
  const expiry = now + 8 * 60 * 60 * 1000;
  const clock = makeFakeClock(now);
  const sent = [];
  const settings = {
    ntfy: {
      topicUrl: 'https://ntfy.sh/agent_limit_checker',
      notifyFiveHour: false,
      notifyWeekly: false,
      notifyResetCreditsExpiry: true,
    },
  };
  const notifier = makeCreditNotifier(clock, settings, async (_config, message) => {
    sent.push(message);
    return { id: `msg-${sent.length}` };
  });
  const snapshot = {
    claude: null,
    codexAccounts: [
      makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex Main', 2, expiry),
      makeResetCreditsAccount(CODEX_REVIEW_ID, 'Codex Review', 1, expiry),
    ],
  };

  notifier.update(snapshot);
  assert.equal(clock.activeTimers().length, 2);
  assert.deepEqual(clock.activeTimers().map((timer) => timer.delay), [3 * 60 * 60 * 1000, 3 * 60 * 60 * 1000]);

  notifier.update(snapshot);
  assert.equal(clock.activeTimers().length, 2);

  clock.setNow(now + 3 * 60 * 60 * 1000);
  for (const timer of clock.activeTimers()) clock.run(timer);
  await flushNotifier();

  assert.equal(sent.length, 2);
  assert.ok(sent.some((message) => /Codex Main/.test(message.message)));
  assert.ok(sent.some((message) => /Codex Review/.test(message.message)));
  assert.ok(sent.some((message) => /残り: 2 回/.test(message.message)));

  notifier.update(snapshot);
  assert.equal(clock.activeTimers().length, 0);
});

test('reset-credit expiry warning fires immediately when its five-hour lead time has passed', async () => {
  const now = 1_800_000_000_000;
  const clock = makeFakeClock(now);
  const sent = [];
  const settings = {
    ntfy: {
      topicUrl: 'https://ntfy.sh/agent_limit_checker',
      notifyFiveHour: false,
      notifyWeekly: false,
      notifyResetCreditsExpiry: true,
    },
  };
  const notifier = makeCreditNotifier(clock, settings, async (_config, message) => {
    sent.push(message);
    return { id: 'msg-1' };
  });
  notifier.update({
    claude: null,
    codexAccounts: [
      makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex Main', 2, now + 2 * 60 * 60 * 1000),
      makeResetCreditsAccount(CODEX_REVIEW_ID, 'No Credits', 0, now + 60 * 60 * 1000),
      makeResetCreditsAccount('/home/me/.codex-invalid', 'Expired', 1, now),
      makeResetCreditsAccount('/home/me/.codex-past', 'Past', 1, now - 1000),
      makeResetCreditsAccount('/home/me/.codex-unknown', 'Unknown', 1, null),
    ],
  });

  assert.equal(clock.activeTimers().length, 1);
  assert.equal(clock.activeTimers()[0].delay, 0);
  clock.run(clock.activeTimers()[0]);
  await flushNotifier();
  assert.equal(sent.length, 1);

  settings.ntfy.notifyResetCreditsExpiry = false;
  notifier.update({
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex Main', 2, now + 2 * 60 * 60 * 1000)],
  });
  assert.equal(clock.activeTimers().length, 0);
});

test('reset-credit expiry warning rechecks a capped long timer before its due time', async () => {
  const now = 1_800_000_000_000;
  const maxTimerDelay = 2_147_483_647;
  const expiry = now + 5 * 60 * 60 * 1000 + maxTimerDelay + 5_000;
  const clock = makeFakeClock(now);
  let sends = 0;
  const notifier = makeCreditNotifier(clock, {
    ntfy: {
      topicUrl: 'https://ntfy.sh/agent_limit_checker',
      notifyResetCreditsExpiry: true,
    },
  }, async () => {
    sends += 1;
    return { id: 'msg-1' };
  });

  notifier.update({
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex', 1, expiry)],
  });
  const cappedTimer = clock.activeTimers()[0];
  assert.equal(cappedTimer.delay, maxTimerDelay);
  clock.setNow(now + maxTimerDelay);
  clock.run(cappedTimer);
  await flushNotifier();

  assert.equal(sends, 0);
  assert.equal(clock.activeTimers().length, 1);
  assert.equal(clock.activeTimers()[0].delay, 5_000);

  clock.setNow(now + maxTimerDelay + 5_000);
  clock.run(clock.activeTimers()[0]);
  await flushNotifier();
  assert.equal(sends, 1);
});

test('same pending reset-credit expiry refreshes its count and account name', async () => {
  const now = 1_800_000_000_000;
  const expiry = now + 6 * 60 * 60 * 1000;
  const clock = makeFakeClock(now);
  const sent = [];
  const settings = {
    ntfy: {
      topicUrl: 'https://ntfy.sh/agent_limit_checker',
      notifyResetCreditsExpiry: true,
    },
  };
  const notifier = makeCreditNotifier(clock, settings, async (_config, message) => {
    sent.push(message);
    return { id: 'msg-1' };
  });

  notifier.update({
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Old Name', 1, expiry)],
  });
  const originalTimer = clock.activeTimers()[0];
  notifier.update({
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Updated Name', 3, expiry)],
  });

  assert.equal(clock.activeTimers().length, 1);
  assert.equal(clock.activeTimers()[0], originalTimer);
  clock.setNow(now + 60 * 60 * 1000);
  clock.run(originalTimer);
  await flushNotifier();

  assert.equal(sent.length, 1);
  assert.match(sent[0].message, /Updated Name/);
  assert.match(sent[0].message, /3 回/);
});

test('reset-credit expiry changes, removal, depletion, and opt-out cancel pending timers', () => {
  const now = 1_800_000_000_000;
  const clock = makeFakeClock(now);
  const settings = { ntfy: { notifyResetCreditsExpiry: true } };
  const notifier = makeCreditNotifier(clock, settings, async () => ({ id: 'msg-1' }));
  const snapshotFor = (expiresAt, count = 1) => ({
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex', count, expiresAt)],
  });
  const firstExpiry = now + 8 * 60 * 60 * 1000;
  const changedExpiry = now + 9 * 60 * 60 * 1000;

  notifier.update(snapshotFor(firstExpiry));
  const firstTimer = clock.activeTimers()[0];
  notifier.update(snapshotFor(changedExpiry));
  assert.equal(firstTimer.cleared, true);
  assert.equal(clock.activeTimers().length, 1);
  const changedTimer = clock.activeTimers()[0];

  notifier.update({ claude: null, codexAccounts: [] });
  assert.equal(changedTimer.cleared, true);
  assert.equal(clock.activeTimers().length, 0);

  notifier.update(snapshotFor(firstExpiry));
  const usedTimer = clock.activeTimers()[0];
  notifier.update(snapshotFor(firstExpiry, 0));
  assert.equal(usedTimer.cleared, true);
  assert.equal(clock.activeTimers().length, 0);

  notifier.update(snapshotFor(firstExpiry));
  const optedOutTimer = clock.activeTimers()[0];
  settings.ntfy.notifyResetCreditsExpiry = false;
  notifier.update(snapshotFor(firstExpiry));
  assert.equal(optedOutTimer.cleared, true);
  assert.equal(clock.activeTimers().length, 0);
});

test('reset-credit expiry warning stops at the exact expiry time', async () => {
  const now = 1_800_000_000_000;
  const expiry = now + 60 * 60 * 1000;
  const clock = makeFakeClock(now);
  let sends = 0;
  const notifier = makeCreditNotifier(clock, {
    ntfy: {
      topicUrl: 'https://ntfy.sh/agent_limit_checker',
      notifyResetCreditsExpiry: true,
    },
  }, async () => {
    sends += 1;
    return { id: 'msg-1' };
  });

  notifier.update({
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex', 1, expiry)],
  });
  const timer = clock.activeTimers()[0];
  clock.setNow(expiry);
  clock.run(timer);
  await flushNotifier();

  assert.equal(sends, 0);
  assert.equal(clock.activeTimers().length, 0);
});

test('reset-credit expiry retry is not scheduled when its delay reaches expiry', async () => {
  const now = 1_800_000_000_000;
  const expiry = now + 5_000;
  const clock = makeFakeClock(now);
  let sends = 0;
  const notifier = makeCreditNotifier(clock, {
    ntfy: {
      topicUrl: 'https://ntfy.sh/agent_limit_checker',
      notifyResetCreditsExpiry: true,
    },
  }, async () => {
    sends += 1;
    throw Object.assign(new Error('temporary failure'), { retryable: true });
  });

  notifier.update({
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex', 1, expiry)],
  });
  clock.run(clock.activeTimers()[0]);
  await flushNotifier();

  assert.equal(sends, 1);
  assert.equal(clock.activeTimers().length, 0);
});

test('same in-flight reset-credit expiry does not duplicate and removal prevents retry', async () => {
  const now = 1_800_000_000_000;
  const snapshot = {
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex', 1, now + 2 * 60 * 60 * 1000)],
  };
  const clock = makeFakeClock(now);
  let rejectSend;
  let sends = 0;
  const notifier = makeCreditNotifier(clock, {
    ntfy: {
      topicUrl: 'https://ntfy.sh/agent_limit_checker',
      notifyResetCreditsExpiry: true,
    },
  }, () => {
    sends += 1;
    return new Promise((_resolve, reject) => { rejectSend = reject; });
  });

  notifier.update(snapshot);
  clock.run(clock.activeTimers()[0]);
  assert.equal(sends, 1);

  notifier.update(snapshot);
  assert.equal(clock.activeTimers().length, 0);
  notifier.update({ claude: null, codexAccounts: [] });
  rejectSend(Object.assign(new Error('temporary failure'), { retryable: true }));
  await flushNotifier();

  assert.equal(sends, 1);
  assert.equal(clock.activeTimers().length, 0);
});

test('reset-credit expiry retries after five seconds and deduplicates after success', async () => {
  const now = 1_800_000_000_000;
  const expiry = now + 60 * 60 * 1000;
  const clock = makeFakeClock(now);
  let sends = 0;
  const notifier = makeCreditNotifier(clock, {
    ntfy: {
      topicUrl: 'https://ntfy.sh/agent_limit_checker',
      notifyResetCreditsExpiry: true,
    },
  }, async () => {
    sends += 1;
    if (sends === 1) throw Object.assign(new Error('temporary failure'), { retryable: true });
    return { id: 'msg-2' };
  });
  const snapshot = {
    claude: null,
    codexAccounts: [makeResetCreditsAccount(CODEX_DEFAULT_ID, 'Codex', 1, expiry)],
  };

  notifier.update(snapshot);
  clock.run(clock.activeTimers()[0]);
  await flushNotifier();
  assert.equal(sends, 1);
  assert.equal(clock.activeTimers().length, 1);
  assert.equal(clock.activeTimers()[0].delay, 5_000);

  clock.setNow(now + 5_000);
  clock.run(clock.activeTimers()[0]);
  await flushNotifier();
  assert.equal(sends, 2);

  notifier.update(snapshot);
  assert.equal(clock.activeTimers().length, 0);
});

test('ordinary reset retry succeeds at the 30-minute grace boundary', async () => {
  const resetAt = 1_800_000_000_000;
  const clock = makeFakeClock(resetAt);
  let sends = 0;
  const notifier = new NtfyResetNotifier({
    getSettings: () => ({
      ntfy: {
        topicUrl: 'https://ntfy.sh/agent_limit_checker',
        notifyFiveHour: true,
        notifyWeekly: false,
      },
    }),
    sendMessage: async () => {
      sends += 1;
      if (sends === 1) throw Object.assign(new Error('temporary failure'), { retryable: true });
      return { id: 'msg-2' };
    },
    now: clock.now,
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    logger: silentLogger,
  });
  const snapshot = {
    claude: { ok: true, data: { fiveHour: { utilization: 0.2, resetsAt: resetAt } } },
    codexAccounts: [],
  };

  notifier.update(snapshot);
  clock.setNow(resetAt + 30 * 60 * 1000 - 5_000);
  clock.run(clock.activeTimers()[0]);
  await flushNotifier();
  assert.equal(sends, 1);
  assert.equal(clock.activeTimers().length, 1);
  assert.equal(clock.activeTimers()[0].delay, 5_000);

  clock.setNow(resetAt + 30 * 60 * 1000);
  clock.run(clock.activeTimers()[0]);
  await flushNotifier();
  assert.equal(sends, 2);

  notifier.update(snapshot);
  assert.equal(clock.activeTimers().length, 0);
});
