'use strict';

const fs = require('fs');
const path = require('path');
const { app } = require('electron');

const output = process.argv.find((item) => item.indexOf('--output=') === 0);
const target = output ? path.resolve(output.slice('--output='.length)) : path.join(__dirname, 'electron-smoke.json');
app.whenReady().then(() => {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, JSON.stringify({ version: process.versions.electron, chrome: process.versions.chrome, node: process.versions.node, platform: process.platform, arch: process.arch }, null, 2));
  app.exit(0);
});
