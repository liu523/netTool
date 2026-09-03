'use strict';

const path = require('path');
const { spawnSync } = require('child_process');

const project = path.resolve(__dirname, '..');
const stage = path.join(project, '.legacy-stage');
const output = path.join(project, 'release', 'legacy');
const environment = Object.assign({}, process.env, {
  PATH: `${path.dirname(process.execPath)}${path.delimiter}${process.env.PATH || ''}`,
  ELECTRON_MIRROR: process.env.ELECTRON_MIRROR || 'https://npmmirror.com/mirrors/electron/'
});
run(process.execPath, [path.join(__dirname, 'prepare-legacy.js')], project);
if (path.resolve(output).startsWith(`${path.resolve(project)}${path.sep}`)) {
  require('fs').rmSync(output, { recursive: true, force: true });
}

const pnpm = process.platform === 'win32' ? 'pnpm.cmd' : 'pnpm';
run(pnpm, ['install', '--no-frozen-lockfile'], stage);
const builder = process.platform === 'win32'
  ? path.join(stage, 'node_modules', '.bin', 'electron-builder.cmd')
  : path.join(stage, 'node_modules', '.bin', 'electron-builder');
run(builder, ['--win', 'portable', '--x64'], stage);
run(builder, ['--win', 'portable', '--ia32'], stage);

function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', shell: process.platform === 'win32', env: environment });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status || 1);
}
