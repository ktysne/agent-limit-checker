'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { formatWeeklyPace, formatWeeklyLabel } = require('../renderer/weeklyPace');

const DAY_MS = 86_400_000;
const NOW = 0;

function weeklyLimit(utilization, remainingDays) {
  return { utilization, resetsAt: NOW + remainingDays * DAY_MS };
}

test('10% 使用で残り6日の週次配分を表示する', () => {
  assert.equal(formatWeeklyPace(weeklyLimit(0.1, 6), NOW), '今日あと18.6% · 以降15.0%/日');
});

test('今日の配分を超過した場合は超過表示にする', () => {
  assert.equal(formatWeeklyPace(weeklyLimit(0.35, 6), NOW), '今日の枠 超過 · 以降10.8%/日');
});

test('今日の残り枠が丸め後に0.0%なら超過表示にする', () => {
  assert.equal(formatWeeklyPace(weeklyLimit(0.857, 2), NOW), '今日の枠 超過 · 以降7.1%/日');
});

test('最終日は週次配分を表示しない', () => {
  assert.equal(formatWeeklyLabel(weeklyLimit(0.1, 1), NOW), '週次');
});

test('リセット時刻がないか過去なら週次配分を表示しない', () => {
  assert.equal(formatWeeklyLabel({ utilization: 0.1, resetsAt: null }, NOW), '週次');
  assert.equal(formatWeeklyLabel({ utilization: 0.1, resetsAt: NOW }, NOW), '週次');
});

test('使用率が null なら週次配分を表示しない', () => {
  assert.equal(formatWeeklyLabel(weeklyLimit(null, 6), NOW), '週次');
  assert.equal(formatWeeklyLabel(weeklyLimit(Number.NaN, 6), NOW), '週次');
});

test('使用率が100%を超えた場合は以降の日ごとの量を0.0%と表示する', () => {
  assert.equal(formatWeeklyPace(weeklyLimit(1.2, 6), NOW), '今日の枠 超過 · 以降0.0%/日');
});

test('リセット直後の残り7日を週次配分に反映する', () => {
  assert.equal(formatWeeklyPace(weeklyLimit(0, 7), NOW), '今日あと14.3% · 以降14.3%/日');
});

test('モデル別週次ラベルではエスケープ済みモデル名の後に配分を表示する', () => {
  assert.equal(
    formatWeeklyLabel(weeklyLimit(0.1, 6), NOW, 'Sonnet &amp; Opus'),
    '週次 (Sonnet &amp; Opus, 今日あと18.6% · 以降15.0%/日)',
  );
});

test('配分を表示できないモデル別週次ラベルは従来の形式を保つ', () => {
  assert.equal(formatWeeklyLabel(weeklyLimit(0.1, 1), NOW, 'Sonnet'), '週次 (Sonnet)');
});
