const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const sandbox = {
  Buffer,
  module: { exports: {} },
  require: () => ({ nativeImage: { createFromBitmap: (pixels) => pixels } }),
};
vm.runInNewContext(fs.readFileSync(path.resolve(__dirname, '../../../src/trayIcon.js'), 'utf8'), sandbox);
for (const [state, claude, codex, options] of [
  ['initial', null, null, {}],
  ['usage', .45, .9, {}],
  ['error', .45, .9, { claudeError: true, codexError: true }],
]) {
  const bytes = sandbox.module.exports.buildTrayImage(claude, codex, options);
  console.log(state, crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase());
}
