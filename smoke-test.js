'use strict';

const path = require('node:path');
const claude = require('./src/claudeProvider');
const codex = require('./src/codexProvider');

(async () => {
  console.log('--- Claude provider ---');
  try {
    const c = await claude.fetch();
    console.log(JSON.stringify(c, null, 2));
  } catch (err) {
    console.log('ERROR:', err.code, err.message);
  }

  console.log('\n--- Codex provider ---');
  try {
    const x = await codex.fetch();
    console.log(JSON.stringify(x, null, 2));
  } catch (err) {
    console.log('ERROR:', err.code, err.message);
  }

  await codex.shutdown();
  process.exit(0);
})();
