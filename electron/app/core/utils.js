'use strict';

const fs = require('fs');
const path = require('path');

function delay(milliseconds, signal) {
  return new Promise((resolve, reject) => {
    if (signal && signal.aborted) {
      reject(abortError());
      return;
    }
    const timer = setTimeout(done, milliseconds);
    function done() {
      cleanup();
      resolve();
    }
    function aborted() {
      clearTimeout(timer);
      cleanup();
      reject(abortError());
    }
    function cleanup() {
      if (signal) signal.removeEventListener('abort', aborted);
    }
    if (signal) signal.addEventListener('abort', aborted, { once: true });
  });
}

function abortError() {
  const error = new Error('检测已取消');
  error.name = 'AbortError';
  return error;
}

function isAbortError(error) {
  return Boolean(error && (error.name === 'AbortError' || error.code === 'ABORT_ERR'));
}

function oneLine(value) {
  return String(value == null ? '' : value).replace(/[\r\n\t]+/g, ' ').replace(/\s{2,}/g, ' ').trim();
}

function safeName(value) {
  const cleaned = String(value || '')
    .replace(/[<>:"/\\|?*\x00-\x1F]/g, '_')
    .replace(/[. ]+$/g, '')
    .trim();
  return cleaned.slice(0, 48) || '未填写门店';
}

function timestampForFile(date) {
  const pad = (number) => String(number).padStart(2, '0');
  return `${date.getFullYear()}${pad(date.getMonth() + 1)}${pad(date.getDate())}-${pad(date.getHours())}${pad(date.getMinutes())}${pad(date.getSeconds())}`;
}

function formatDate(date) {
  const pad = (number) => String(number).padStart(2, '0');
  const offset = -date.getTimezoneOffset();
  const sign = offset >= 0 ? '+' : '-';
  const abs = Math.abs(offset);
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())} ${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
}

function escapeHtml(value) {
  return String(value == null ? '' : value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function csvCell(value) {
  const text = String(value == null ? '' : value);
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

function withTimeout(promise, milliseconds, label, onTimeout) {
  let timer;
  return Promise.race([
    promise.finally(() => clearTimeout(timer)),
    new Promise((_, reject) => {
      timer = setTimeout(() => {
        if (onTimeout) onTimeout();
        const error = new Error(`${label || '操作'}超时`);
        error.code = 'ETIMEDOUT';
        reject(error);
      }, milliseconds);
    })
  ]);
}

function writeFileAtomic(filePath, content) {
  const temporary = `${filePath}.tmp-${process.pid}-${Date.now()}`;
  fs.writeFileSync(temporary, content, 'utf8');
  fs.renameSync(temporary, filePath);
}

function ensureDirectory(directory) {
  fs.mkdirSync(directory, { recursive: true });
}

function isIpV4(value) {
  const parts = String(value).split('.');
  return parts.length === 4 && parts.every((part) => /^\d{1,3}$/.test(part) && Number(part) <= 255);
}

function unique(values) {
  return Array.from(new Set(values));
}

function basenameLink(filePath) {
  return encodeURI(path.basename(filePath));
}

module.exports = {
  abortError,
  basenameLink,
  csvCell,
  delay,
  ensureDirectory,
  escapeHtml,
  formatDate,
  isAbortError,
  isIpV4,
  oneLine,
  safeName,
  timestampForFile,
  unique,
  withTimeout,
  writeFileAtomic
};
