'use strict';

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

function pathDirs(env = process.env) {
  return (env.PATH || '').split(path.delimiter).filter(Boolean);
}

function fileExists(p) {
  try {
    return !!p && fs.existsSync(p);
  } catch {
    return false;
  }
}

function resolveExecutableFromPath(commandNames, env = process.env, extraDirs = []) {
  for (const dir of [...pathDirs(env), ...extraDirs]) {
    for (const name of commandNames) {
      const candidate = path.join(dir, name);
      if (fileExists(candidate)) return candidate;
    }
  }
  return null;
}

function uniquePaths(paths) {
  const seen = new Set();
  const out = [];
  for (const p of paths) {
    if (!p) continue;
    const key = process.platform === 'win32' ? p.toLowerCase() : p;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(p);
  }
  return out;
}

function collectExecutables(commandNames, dirGroups) {
  const found = [];
  for (const dirs of dirGroups) {
    for (const name of commandNames) {
      for (const dir of dirs) {
        const candidate = path.join(dir, name);
        if (fileExists(candidate)) found.push(candidate);
      }
    }
  }
  return uniquePaths(found);
}

function latestDirectory(parent, predicate) {
  try {
    return fs.readdirSync(parent, { withFileTypes: true })
      .filter((entry) => entry.isDirectory() && predicate(entry.name))
      .map((entry) => path.join(parent, entry.name))
      .sort((a, b) => b.localeCompare(a))
      .at(0) || null;
  } catch {
    return null;
  }
}

function isChatgptExtensionBin(dir) {
  const normalized = dir.replace(/\\/g, '/').toLowerCase();
  return normalized.includes('/.vscode/extensions/openai.chatgpt-') && normalized.endsWith('/bin/windows-x86_64');
}

function resolveCodexExecutables(env = process.env) {
  const override = env.CODEX_PATH;
  if (override && fileExists(override)) return [override];

  const candidateNames = process.platform === 'win32'
    ? ['codex.exe', 'codex.cmd', 'codex.bat', 'codex.ps1', 'codex']
    : ['codex'];

  const normalPathDirs = pathDirs(env).filter((dir) => !isChatgptExtensionBin(dir));
  const npmDirs = [];
  const extensionDirs = [];

  if (process.platform === 'win32') {
    const npmGlobal = path.join(env.APPDATA || '', 'npm');
    if (npmGlobal) npmDirs.push(npmGlobal);

    const extensionRoot = path.join(env.USERPROFILE || os.homedir(), '.vscode', 'extensions');
    const chatgptExtension = latestDirectory(extensionRoot, (name) => name.startsWith('openai.chatgpt-'));
    if (chatgptExtension) {
      extensionDirs.push(path.join(chatgptExtension, 'bin', 'windows-x86_64'));
    }
  }

  return collectExecutables(candidateNames, [normalPathDirs, npmDirs, extensionDirs]);
}

function resolveCodexExecutable(env = process.env) {
  return resolveCodexExecutables(env)[0] || null;
}

function resolveClaudeExecutable(env = process.env) {
  const override = env.CLAUDE_PATH;
  if (override && fileExists(override)) return override;

  const candidateNames = process.platform === 'win32'
    ? ['claude.exe', 'claude.ps1', 'claude.cmd', 'claude.bat', 'claude']
    : ['claude'];

  const extraDirs = [];
  if (process.platform === 'win32') {
    const npmGlobal = path.join(env.APPDATA || '', 'npm');
    if (npmGlobal) extraDirs.push(npmGlobal);
  }

  return resolveExecutableFromPath(candidateNames, env, extraDirs);
}

module.exports = {
  resolveCodexExecutable,
  resolveCodexExecutables,
  resolveClaudeExecutable,
  resolveExecutableFromPath,
};
