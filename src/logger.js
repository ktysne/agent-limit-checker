'use strict';

const fs = require('node:fs');
const path = require('node:path');

let logFile = null;

function init(logDir) {
  try {
    fs.mkdirSync(logDir, { recursive: true });
    logFile = path.join(logDir, 'agent-limit-checker.log');
  } catch (err) {
    console.error('[logger] init failed', err);
  }
}

function formatArg(arg) {
  if (arg instanceof Error) {
    return `${arg.name || 'Error'} ${arg.code || ''} ${arg.message || ''}`.trim();
  }
  if (typeof arg === 'string') return arg;
  try {
    return JSON.stringify(arg);
  } catch {
    return String(arg);
  }
}

function write(level, args) {
  if (!logFile) return;
  const line = `[${new Date().toISOString()}] ${level} ${args.map(formatArg).join(' ')}\n`;
  fs.appendFile(logFile, line, 'utf8', () => {});
}

function info(...args) {
  console.log(...args);
  write('INFO', args);
}

function warn(...args) {
  console.warn(...args);
  write('WARN', args);
}

function error(...args) {
  console.error(...args);
  write('ERROR', args);
}

module.exports = { init, info, warn, error };
