'use strict';

const WEEKLY_WINDOW_DAYS = 7;
const DAY_MS = 86_400_000;

function formatWeeklyPace(limit, now) {
  if (!limit || !Number.isFinite(now) || !Number.isFinite(limit.resetsAt) || limit.resetsAt <= now) return null;
  if (!Number.isFinite(limit.utilization)) return null;

  const remainingDays = Math.min(
    WEEKLY_WINDOW_DAYS,
    Math.max(1, Math.ceil((limit.resetsAt - now) / DAY_MS)),
  );
  if (remainingDays <= 1) return null;

  const utilizationPercent = limit.utilization * 100;
  const todayLeft = (100 / WEEKLY_WINDOW_DAYS) * (8 - remainingDays) - utilizationPercent;
  const perDay = Math.max(0, 100 - utilizationPercent) / remainingDays;
  if (Number(todayLeft.toFixed(1)) <= 0) {
    return `今日の枠 超過 · 以降${perDay.toFixed(1)}%/日`;
  }
  return `今日あと${todayLeft.toFixed(1)}% · 以降${perDay.toFixed(1)}%/日`;
}

function formatWeeklyLabel(limit, now, escapedScopeLabel = null) {
  const pace = formatWeeklyPace(limit, now);
  if (escapedScopeLabel == null) return pace ? `週次 (${pace})` : '週次';
  if (!pace) return `週次 (${escapedScopeLabel})`;
  return `週次 (${escapedScopeLabel}, ${pace})`;
}

if (typeof module !== 'undefined') {
  module.exports = { formatWeeklyPace, formatWeeklyLabel };
}
