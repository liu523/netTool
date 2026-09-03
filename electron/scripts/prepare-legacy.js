'use strict';

const fs = require('fs');
const path = require('path');

const project = path.resolve(__dirname, '..');
const currentPackage = JSON.parse(fs.readFileSync(path.join(project, 'package.json'), 'utf8'));
const stage = path.join(project, '.legacy-stage');
fs.rmSync(stage, { recursive: true, force: true });
fs.mkdirSync(stage, { recursive: true });
fs.cpSync(path.join(project, 'app'), path.join(stage, 'app'), { recursive: true });
fs.cpSync(path.join(project, 'native', 'bin'), path.join(stage, 'native', 'bin'), { recursive: true });

const packageJson = {
  name: 'lyfz-netdiag-electron-legacy',
  version: currentPackage.version,
  private: true,
  description: '利亚方舟海螺云网络诊断工具 Windows 7 遗留兼容版',
  main: 'app/main.js',
  author: '利亚方舟',
  license: 'UNLICENSED',
  devDependencies: {
    electron: '22.3.27',
    'electron-builder': '26.0.12'
  },
  build: {
    appId: 'com.lyfz.netdiag.legacy',
    productName: '利亚方舟海螺云网络诊断工具-Win7兼容版',
    asar: true,
    files: ['app/**/*'],
    extraResources: [{ from: 'native/bin', to: 'native', filter: ['**/*'] }],
    directories: { output: '../release/legacy' },
    win: {
      artifactName: `LYFZ-NetDiag-Electron-${currentPackage.version}-Win7-\${arch}.\${ext}`,
      signAndEditExecutable: false,
      target: ['portable']
    }
  }
};
fs.writeFileSync(path.join(stage, 'package.json'), `${JSON.stringify(packageJson, null, 2)}\n`, 'utf8');
fs.writeFileSync(path.join(stage, 'pnpm-workspace.yaml'), `packages:\n  - .\n\nallowBuilds:\n  electron: true\n  electron-builder: true\n  electron-winstaller: true\n\nblockExoticSubdeps: false\n\nminimumReleaseAgeExclude:\n  - electron@22.3.27\n`, 'utf8');
fs.writeFileSync(path.join(stage, '.npmrc'), 'block-exotic-subdeps=false\nstrict-peer-dependencies=true\n', 'utf8');
process.stdout.write(`Legacy staging prepared: ${stage}\n`);
