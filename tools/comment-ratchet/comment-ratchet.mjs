#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { extname, basename, dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export const BASELINE_NAME = 'comment-baseline.json';

const C_LIKE = ['ts', 'tsx', 'mts', 'cts', 'js', 'jsx', 'mjs', 'cjs', 'cs', 'cpp', 'cc', 'c', 'h', 'hpp', 'usf', 'ush', 'jsonc', 'scss'];
const JS_LIKE = new Set(['ts', 'tsx', 'mts', 'cts', 'js', 'jsx', 'mjs', 'cjs', 'jsonc']);
const FAMILY = new Map([
  ...C_LIKE.map((ext) => [ext, 'c']),
  ['css', 'css'],
  ['py', 'python'],
  ['toml', 'toml'],
  ['sh', 'shell'],
  ['bash', 'shell'],
  ['ps1', 'powershell'],
  ['psm1', 'powershell'],
  ['psd1', 'powershell'],
  ['yml', 'yaml'],
  ['yaml', 'yaml'],
  ['sql', 'sql'],
  ['html', 'xml'],
  ['xml', 'xml'],
  ['csproj', 'xml'],
  ['props', 'xml'],
  ['targets', 'xml'],
  ['xaml', 'xml'],
  ['slnx', 'xml'],
  ['ini', 'ini'],
  ['cmd', 'batch'],
  ['bat', 'batch'],
]);

const DIRECTIVES = [
  /^!/,
  /^\s*eslint-(disable|enable|env)\b/,
  /^\s*@ts-(expect-error|ignore|nocheck|check)\b/,
  /^\s*prettier-ignore\b/,
  /^\s*(istanbul|v8|c8) ignore\b/,
  /^\s*biome-ignore\b/,
  /^\s*@(vitest|jest)-environment\b/,
  /^\s*@vite-ignore\b/,
  /^\s*webpack[A-Z]\w*:/,
  /^\s*[@#]__(PURE|NO_SIDE_EFFECTS)__/,
  /^\/\s*<reference\b/,
  /^\s*type:\s*ignore\b/,
  /^\s*noqa\b/,
  /^\s*(pylint|ruff|mypy|pyright|flake8|isort|yapf):/,
  /^\s*pragma:/,
  /^\s*fmt:\s*(off|on|skip)\b/,
  /^\s*-\*-.*-\*-/,
  /^\s*(syntax|escape)=/,
  /^\s*shellcheck\b/,
  /^\s*yaml-language-server:/,
  /^\s*clang-format (off|on)\b/,
  /^\s*NOLINT/,
  /^[Rr]equires -/,
  /^\s*ReSharper (disable|restore)\b/,
];

const LICENSE = /copyright|SPDX-License-Identifier|licensed under/i;

function lineStarts(text) {
  const starts = [0];
  for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) starts.push(i + 1);
  return starts;
}

function lineOf(starts, offset) {
  let lo = 0;
  let hi = starts.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (starts[mid] <= offset) lo = mid;
    else hi = mid - 1;
  }
  return lo + 1;
}

function lineEnd(text, i) {
  const end = text.indexOf('\n', i);
  return end === -1 ? text.length : end;
}

function skipQuoted(text, i, quote) {
  i++;
  while (i < text.length) {
    const ch = text[i];
    if (ch === '\\') {
      i += 2;
      continue;
    }
    if (ch === quote) return i + 1;
    if (ch === '\n') return i;
    i++;
  }
  return i;
}

function skipUntil(text, i, terminator) {
  const end = text.indexOf(terminator, i);
  return end === -1 ? text.length : end + terminator.length;
}

const REGEX_PREV_WORDS = new Set(['return', 'typeof', 'case', 'in', 'of', 'delete', 'void', 'throw', 'new', 'else', 'do', 'yield', 'await', 'instanceof']);

function regexAllowed(text, i) {
  if (text[i - 1] === '<' || text[i + 1] === '>') return false;
  let j = i - 1;
  while (j >= 0 && (text[j] === ' ' || text[j] === '\t' || text[j] === '\n' || text[j] === '\r')) j--;
  if (j < 0) return true;
  const ch = text[j];
  if ('(,=:[!&|?{};+-*%<>~^'.includes(ch)) return true;
  if (/[A-Za-z_$]/.test(ch)) {
    let k = j;
    while (k >= 0 && /[\w$]/.test(text[k])) k--;
    return REGEX_PREV_WORDS.has(text.slice(k + 1, j + 1));
  }
  return false;
}

function skipRegex(text, i) {
  let j = i + 1;
  let inClass = false;
  while (j < text.length) {
    const ch = text[j];
    if (ch === '\n') return i + 1;
    if (ch === '\\') {
      j += 2;
      continue;
    }
    if (ch === '[') inClass = true;
    else if (ch === ']') inClass = false;
    else if (ch === '/' && !inClass) return j + 1;
    j++;
  }
  return i + 1;
}

function skipTemplate(text, i, templateDepth) {
  while (i < text.length) {
    const ch = text[i];
    if (ch === '\\') {
      i += 2;
      continue;
    }
    if (ch === '`') return i + 1;
    if (ch === '$' && text[i + 1] === '{') {
      templateDepth.push(0);
      return i + 2;
    }
    i++;
  }
  return i;
}

function scanC(text, ext, emit) {
  const js = JS_LIKE.has(ext);
  const cs = ext === 'cs';
  const cpp = !js && !cs;
  const templateDepth = [];
  let i = 0;
  while (i < text.length) {
    const ch = text[i];
    const next = text[i + 1];
    if (ch === '/' && next === '/') {
      if (text[i - 1] === ':' && /[A-Za-z]/.test(text[i - 2] || '')) {
        i += 2;
        continue;
      }
      let end = lineEnd(text, i);
      while (cpp && text[end - 1] === '\\' && end < text.length) end = lineEnd(text, end + 1);
      emit(i, end, text.slice(i + 2, end), 'line');
      i = end;
      continue;
    }
    if (ch === '/' && next === '*') {
      const end = skipUntil(text, i + 2, '*/');
      emit(i, end, text.slice(i + 2, Math.max(i + 2, end - 2)), 'block');
      i = end;
      continue;
    }
    if (cs && ch === '"' && next === '"' && text[i + 2] === '"') {
      let n = 3;
      while (text[i + n] === '"') n++;
      i = skipUntil(text, i + n, '"'.repeat(n));
      continue;
    }
    if (cs && (ch === '@' || ch === '$') && (next === '"' || ((next === '@' || next === '$') && text[i + 2] === '"'))) {
      const open = text.indexOf('"', i);
      const verbatim = text.slice(i, open).includes('@');
      if (text[open + 1] === '"' && text[open + 2] === '"') {
        i = open;
        continue;
      }
      if (!verbatim) {
        i = skipQuoted(text, open, '"');
        continue;
      }
      let j = open + 1;
      while (j < text.length) {
        if (text[j] === '"') {
          if (text[j + 1] === '"') {
            j += 2;
            continue;
          }
          break;
        }
        j++;
      }
      i = j + 1;
      continue;
    }
    if (cpp && ch === 'R' && next === '"' && !/[\w]/.test(text[i - 1] || '')) {
      const paren = text.indexOf('(', i);
      if (paren !== -1 && paren - i < 20) {
        i = skipUntil(text, paren, `)${text.slice(i + 2, paren)}"`);
        continue;
      }
    }
    if (ch === '"' || ch === "'") {
      if (cpp && ch === "'" && /[0-9a-fA-F]/.test(text[i - 1] || '') && /[0-9a-fA-F]/.test(next || '')) {
        i++;
        continue;
      }
      i = skipQuoted(text, i, ch);
      continue;
    }
    if (js && ch === '`') {
      i = skipTemplate(text, i + 1, templateDepth);
      continue;
    }
    if (js && ch === '{' && templateDepth.length) {
      templateDepth[templateDepth.length - 1]++;
      i++;
      continue;
    }
    if (js && ch === '}' && templateDepth.length) {
      if (templateDepth[templateDepth.length - 1] === 0) {
        templateDepth.pop();
        i = skipTemplate(text, i + 1, templateDepth);
        continue;
      }
      templateDepth[templateDepth.length - 1]--;
      i++;
      continue;
    }
    if (js && ch === '/' && regexAllowed(text, i)) {
      i = skipRegex(text, i);
      continue;
    }
    i++;
  }
}

function scanCss(text, emit) {
  let i = 0;
  while (i < text.length) {
    const ch = text[i];
    if (ch === '/' && text[i + 1] === '*') {
      const end = skipUntil(text, i + 2, '*/');
      emit(i, end, text.slice(i + 2, Math.max(i + 2, end - 2)), 'block');
      i = end;
      continue;
    }
    if (ch === '"' || ch === "'") {
      i = skipQuoted(text, i, ch);
      continue;
    }
    i++;
  }
}

function scanPython(text, emit, docstrings) {
  let i = 0;
  let depth = 0;
  let lineHasCode = false;
  let previous = ':';
  while (i < text.length) {
    const ch = text[i];
    if (ch === '\n') {
      lineHasCode = false;
      i++;
      continue;
    }
    if (ch === '#') {
      const end = lineEnd(text, i);
      emit(i, end, text.slice(i + 1, end), 'line');
      i = end;
      continue;
    }
    if (ch === '"' || ch === "'") {
      const triple = text[i + 1] === ch && text[i + 2] === ch;
      const start = i;
      if (triple) {
        let j = i + 3;
        while (j < text.length) {
          if (text[j] === '\\') {
            j += 2;
            continue;
          }
          if (text[j] === ch && text[j + 1] === ch && text[j + 2] === ch) break;
          j++;
        }
        i = Math.min(text.length, j + 3);
        if (docstrings && !lineHasCode && depth === 0 && previous === ':' && text.slice(i, lineEnd(text, i)).trim() === '') {
          emit(start, i, text.slice(start + 3, Math.max(start + 3, i - 3)), 'docstring');
        }
      } else {
        i = skipQuoted(text, i, ch);
      }
      lineHasCode = true;
      previous = 's';
      continue;
    }
    if (ch === '(' || ch === '[' || ch === '{') depth++;
    else if (ch === ')' || ch === ']' || ch === '}') depth = Math.max(0, depth - 1);
    if (ch !== ' ' && ch !== '\t' && ch !== '\r') {
      lineHasCode = true;
      previous = ch;
    }
    i++;
  }
}

function shellScanner(emit) {
  let quote = null;
  let heredoc = null;
  return (line, start) => {
    if (heredoc) {
      if (line.trim() === heredoc) heredoc = null;
      return;
    }
    let pending = null;
    for (let i = 0; i < line.length; i++) {
      const ch = line[i];
      if (quote === "'") {
        if (ch === "'") quote = null;
        continue;
      }
      if (quote === '"') {
        if (ch === '\\') i++;
        else if (ch === '"') quote = null;
        continue;
      }
      if (ch === '\\') {
        i++;
        continue;
      }
      if (ch === '"' || ch === "'") {
        quote = ch;
        continue;
      }
      if (ch === '<' && line[i + 1] === '<' && line[i + 2] !== '<' && line[i - 1] !== '<') {
        const here = /^<<-?\s*(['"]?)([A-Za-z_][A-Za-z0-9_]*)\1/.exec(line.slice(i));
        if (here) pending = here[2];
      }
      if (ch === '#' && (i === 0 || /[\s;(|&]/.test(line[i - 1]))) {
        emit(start + i, start + line.length, line.slice(i + 1), 'line');
        break;
      }
    }
    if (pending) heredoc = pending;
  };
}

function hashInLine(line, escape, before = /[\s;({]/) {
  let quote = null;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (quote) {
      if (ch === escape && quote === '"') i++;
      else if (ch === quote) quote = null;
      continue;
    }
    if (ch === escape) {
      i++;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (ch === '#' && (i === 0 || before.test(line[i - 1]))) return i;
    if (ch === '<' && line[i + 1] === '#' && escape === '`') return i;
  }
  return -1;
}

function eachLine(text, visit) {
  let offset = 0;
  for (const raw of text.split('\n')) {
    visit(raw.endsWith('\r') ? raw.slice(0, -1) : raw, offset);
    offset += raw.length + 1;
  }
}

function scanShell(text, emit) {
  const scan = shellScanner(emit);
  eachLine(text, scan);
}

function scanYaml(text, emit) {
  let block = null;
  eachLine(text, (line, start) => {
    const indent = line.search(/\S/);
    if (block) {
      if (indent === -1 || indent > block.indent) {
        if (block.scan) block.scan(line, start);
        return;
      }
      block = null;
    }
    const at = hashInLine(line, '\\', /\s/);
    const code = at === -1 ? line : line.slice(0, at);
    if (at !== -1) emit(start + at, start + line.length, line.slice(at + 1), 'line');
    const scalar = /(?:^|\s)(?:([\w.-]+)\s*:|-)\s*[|>][+-]?\d*\s*$/.exec(code);
    if (scalar) block = { indent, scan: scalar[1] === 'run' || scalar[1] === 'script' ? shellScanner(emit) : null };
  });
}

function scanPowershell(text, emit) {
  let hereString = null;
  let block = null;
  eachLine(text, (line, start) => {
    if (hereString) {
      if (line.startsWith(hereString)) hereString = null;
      return;
    }
    if (block) {
      const close = line.indexOf('#>');
      if (close === -1) {
        block.text += `\n${line}`;
        return;
      }
      emit(block.start, start + close + 2, `${block.text}\n${line.slice(0, close)}`, 'block');
      block = null;
      return;
    }
    const at = hashInLine(line, '`');
    if (at !== -1 && line[at] === '<') {
      const close = line.indexOf('#>', at + 2);
      if (close !== -1) emit(start + at, start + close + 2, line.slice(at + 2, close), 'block');
      else block = { start: start + at, text: line.slice(at + 2) };
      return;
    }
    if (at !== -1) {
      emit(start + at, start + line.length, line.slice(at + 1), 'line');
      return;
    }
    const here = /@(["'])\s*$/.exec(line);
    if (here) hereString = `${here[1]}@`;
  });
  if (block) emit(block.start, text.length, block.text, 'block');
}

function scanIni(text, emit) {
  eachLine(text, (line, start) => {
    const match = /^\s*[;#]/.exec(line);
    if (match) emit(start + match[0].length - 1, start + line.length, line.slice(match[0].length), 'line');
  });
}

function scanBatch(text, emit) {
  eachLine(text, (line, start) => {
    const match = /^\s*(?:@?rem\b|::)/i.exec(line);
    if (match) emit(start + line.search(/\S/), start + line.length, line.slice(match[0].length), 'line');
  });
}

function scanDocker(text, emit) {
  eachLine(text, (line, start) => {
    const match = /^\s*#/.exec(line);
    if (match) emit(start + match[0].length - 1, start + line.length, line.slice(match[0].length), 'line');
  });
}

function scanSql(text, emit) {
  let i = 0;
  while (i < text.length) {
    const ch = text[i];
    if (ch === '-' && text[i + 1] === '-') {
      const end = lineEnd(text, i);
      emit(i, end, text.slice(i + 2, end), 'line');
      i = end;
      continue;
    }
    if (ch === '/' && text[i + 1] === '*') {
      const end = skipUntil(text, i + 2, '*/');
      emit(i, end, text.slice(i + 2, Math.max(i + 2, end - 2)), 'block');
      i = end;
      continue;
    }
    if (ch === "'") {
      let j = i + 1;
      while (j < text.length) {
        if (text[j] === "'") {
          if (text[j + 1] === "'") {
            j += 2;
            continue;
          }
          break;
        }
        j++;
      }
      i = j + 1;
      continue;
    }
    if (ch === '"') {
      i = skipUntil(text, i + 1, '"');
      continue;
    }
    i++;
  }
}

function scanXml(text, emit) {
  let i = 0;
  for (;;) {
    const open = text.indexOf('<!--', i);
    if (open === -1) return;
    const end = skipUntil(text, open + 4, '-->');
    emit(open, end, text.slice(open + 4, Math.max(open + 4, end - 3)), 'block');
    i = end;
  }
}

const SCANNERS = {
  css: scanCss,
  python: (text, emit) => scanPython(text, emit, true),
  toml: (text, emit) => scanPython(text, emit, false),
  shell: scanShell,
  yaml: scanYaml,
  powershell: scanPowershell,
  sql: scanSql,
  xml: scanXml,
  ini: scanIni,
  batch: scanBatch,
  docker: scanDocker,
};

function familyOf(file) {
  const name = basename(file).toLowerCase();
  if (name === 'dockerfile' || name.startsWith('dockerfile.')) return { family: 'docker', ext: 'dockerfile' };
  const ext = extname(name).slice(1);
  return { family: FAMILY.get(ext), ext };
}

export function scanText(raw, file, config = {}) {
  const text = raw.includes('\r\n') ? raw.replace(/\r\n/g, '\n') : raw;
  const { family, ext } = familyOf(file);
  if (!family || text.includes('\u0000')) return null;
  const starts = lineStarts(text);
  const comments = [];
  const emit = (start, end, body, kind) => {
    const line = lineOf(starts, start);
    const endLine = lineOf(starts, Math.max(start, end - 1));
    const before = text.slice(starts[line - 1], start);
    const after = text.slice(end, lineEnd(text, end));
    let whole = before.trim() === '' && after.trim() === '';
    let from = start;
    let to = end;
    if (!whole && family === 'c' && /^\s*\{\s*$/.test(before) && /^\s*\}\s*$/.test(after)) {
      whole = true;
      from = starts[line - 1] + before.indexOf('{');
      to = end + after.indexOf('}') + 1;
    }
    comments.push({ line, endLine, kind, text: body, whole, start: from, end: to });
  };
  if (family === 'c') scanC(text, ext, emit);
  else SCANNERS[family](text, emit);
  comments.sort((a, b) => a.start - b.start);
  const commentLines = new Set();
  for (const c of comments) for (let n = c.line; n <= c.endLine; n++) commentLines.add(n);
  let firstCode = Infinity;
  for (let n = 1; n <= starts.length; n++) {
    if (commentLines.has(n)) continue;
    const content = text.slice(starts[n - 1], n < starts.length ? starts[n] : text.length).trim();
    if (content !== '' && !(n === 1 && content.startsWith('#!'))) {
      firstCode = n;
      break;
    }
  }
  const extra = (config.directives || []).map((source) => new RegExp(source));
  const runtimeDocstrings = family === 'python' && /__doc__|^\s*(?:import|from)\s+(?:typer|click|fastapi)\b/m.test(text);
  for (const comment of comments) {
    const first = comment.text.split('\n')[0];
    if (comment.kind === 'docstring') {
      if (config.keepDocstrings || runtimeDocstrings) comment.exempt = 'docstring';
    } else if (DIRECTIVES.some((re) => re.test(first)) || extra.some((re) => re.test(comment.text))) comment.exempt = 'directive';
    else if (comment.line < firstCode && LICENSE.test(comment.text)) comment.exempt = 'license';
  }
  if (ext === 'h' || ext === 'hpp') markReflected(text, starts, comments);
  return comments;
}

const REFLECTED = /^\s*U(?:PROPERTY|FUNCTION|CLASS|STRUCT|ENUM|INTERFACE|DELEGATE)\s*\(/;

function markReflected(text, starts, comments) {
  if (!new RegExp(REFLECTED.source, 'm').test(text)) return;
  const whole = new Set();
  for (const c of comments) if (c.whole) for (let n = c.line; n <= c.endLine; n++) whole.add(n);
  const lineAt = (n) => text.slice(starts[n - 1], n < starts.length ? starts[n] : text.length);
  const enumLines = new Set();
  let state = 0;
  for (let n = 1; n <= starts.length; n++) {
    if (whole.has(n)) {
      if (state === 2) enumLines.add(n);
      continue;
    }
    const line = lineAt(n);
    if (state === 0 && /^\s*UENUM\s*\(/.test(line)) state = 1;
    if (state === 1 && line.includes('{')) state = 2;
    if (state === 2 && /\}\s*;/.test(line)) state = 0;
  }
  for (const comment of comments) {
    if (comment.exempt || !comment.whole) continue;
    if (enumLines.has(comment.line)) {
      comment.exempt = 'reflected';
      continue;
    }
    let n = comment.endLine + 1;
    while (n <= starts.length && (whole.has(n) || lineAt(n).trim() === '')) n++;
    if (n <= starts.length && REFLECTED.test(lineAt(n))) comment.exempt = 'reflected';
  }
}

export function measure(comments) {
  const lines = new Set();
  let count = 0;
  let shed = 0;
  let trailing = 0;
  for (const comment of comments) {
    if (comment.exempt) continue;
    count++;
    for (let n = comment.line; n <= comment.endLine; n++) lines.add(n);
    if (comment.whole) shed += comment.endLine - comment.line + 1;
    else trailing++;
  }
  return { comments: count, lines: lines.size, shed, trailing };
}

const OPENERS = /[{([:,]$|=>$/;
const CLOSERS = /^[})\]]/;

const MARKS = ['\u0001', '\u0002', '', '', ''];

function tidyJunctions(text, mark) {
  const parts = text.split(new RegExp(`${mark}+`));
  let out = parts[0];
  for (let p = 1; p < parts.length; p++) {
    let rest = parts[p];
    for (;;) {
      const prevEnd = out.endsWith('\n') ? out.length - 1 : out.length;
      const prevStart = out.lastIndexOf('\n', prevEnd - 1) + 1;
      const prev = out.slice(prevStart, prevEnd);
      const nextEnd = lineEnd(rest, 0);
      const next = rest.slice(0, nextEnd);
      const atStart = out.length === 0;
      const atEnd = rest.length === 0 && p === parts.length - 1;
      const prevBlank = !atStart && prev.trim() === '';
      const nextBlank = rest.length > 0 && next.trim() === '';
      if (nextBlank && (atStart || prevBlank || OPENERS.test(prev.trimEnd()))) {
        rest = rest.slice(Math.min(rest.length, nextEnd + 1));
        continue;
      }
      if (prevBlank && (atEnd || CLOSERS.test(next.trimStart()))) {
        out = out.slice(0, prevStart);
        continue;
      }
      break;
    }
    out += rest;
  }
  return out;
}

function soleBody(text, comment, comments) {
  const indent = text.slice(text.lastIndexOf('\n', comment.start - 1) + 1, comment.start);
  if (indent.length === 0) return null;
  const skip = new Set();
  for (const c of comments) if (c.whole) for (let n = c.line; n <= c.endLine; n++) skip.add(n);
  const lines = text.split('\n');
  for (let n = comment.endLine + 1; n <= lines.length; n++) {
    const line = lines[n - 1];
    if (line.trim() === '' || skip.has(n)) continue;
    return line.search(/\S/) >= indent.length ? null : indent;
  }
  return indent;
}

export function stripText(source, file, config = {}, wanted = null) {
  const paired = (source.match(/\r\n/g) ?? []).length;
  if (!paired) return stripLf(source, file, config, wanted);
  const bare = (source.match(/(?<!\r)\n/g) ?? []).length;
  const odd = bare ? MARKS.find((candidate) => !source.includes(candidate)) : '';
  if (odd === undefined) return { text: source, removed: 0 };
  const crlf = paired >= bare;
  const flat = crlf
    ? source.replace(/(?<!\r)\n/g, `${odd}\n`).replace(/\r\n/g, '\n')
    : source.replace(/\r\n/g, `${odd}\n`);
  const stripped = stripLf(flat, file, config, wanted);
  if (!stripped.removed) return { ...stripped, text: source };
  const [usual, rare] = crlf ? ['\r\n', '\n'] : ['\n', '\r\n'];
  const lines = stripped.text.split('\n');
  const last = lines.pop();
  const text = `${lines
    .map((line) => (odd && line.endsWith(odd) ? `${line.slice(0, -1)}${rare}` : `${line}${usual}`))
    .join('')}${last}`;
  return { ...stripped, text: odd ? text.split(odd).join('') : text };
}

function stripLf(text, file, config, wanted) {
  const comments = scanText(text, file, config);
  const mark = MARKS.find((candidate) => !text.includes(candidate));
  if (!comments || !mark) return { text, removed: 0 };
  const targets = comments.filter((c) => !c.exempt && (!wanted || wanted(c)));
  let out = text;
  for (const comment of [...targets].sort((a, b) => b.start - a.start)) {
    if (comment.whole) {
      const from = out.lastIndexOf('\n', comment.start - 1) + 1;
      const to = Math.min(out.length, lineEnd(out, comment.end) + 1);
      const pass = comment.kind === 'docstring' ? soleBody(text, comment, comments) : null;
      out = `${out.slice(0, from)}${pass === null ? mark : `${pass}pass\n`}${out.slice(to)}`;
      continue;
    }
    const tail = out.slice(comment.end, lineEnd(out, comment.end));
    if (tail.trim() === '') {
      let from = comment.start;
      while (from > 0 && (out[from - 1] === ' ' || out[from - 1] === '\t')) from--;
      out = out.slice(0, from) + out.slice(lineEnd(out, comment.end));
      continue;
    }
    let from = comment.start;
    let to = comment.end;
    if ((from === 0 || /[\s([{]/.test(out[from - 1])) && out[to] === ' ') to++;
    else if (out[from - 1] === ' ' && /[)\]},;]/.test(out[to] || '')) from--;
    out = out.slice(0, from) + out.slice(to);
  }
  return { text: tidyJunctions(out, mark), removed: targets.length };
}

function globToRegExp(glob) {
  let re = '';
  for (let i = 0; i < glob.length; i++) {
    const ch = glob[i];
    if (ch === '*') {
      if (glob[i + 1] === '*') {
        re += '.*';
        i++;
        if (glob[i + 1] === '/') i++;
      } else re += '[^/]*';
    } else if ('.+^$()|{}[]\\'.includes(ch)) re += `\\${ch}`;
    else if (ch === '?') re += '[^/]';
    else re += ch;
  }
  return new RegExp(`^${re}$`);
}

function git(dir, args) {
  return execFileSync('git', args, { cwd: dir, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore'] });
}

function trackedFiles(dir, config, only) {
  const exclude = (config.exclude || []).map(globToRegExp);
  return git(dir, ['ls-files', '-z'])
    .split('\u0000')
    .filter((file) => file && familyOf(file).family && !exclude.some((re) => re.test(file)) && (!only || only.some((prefix) => file.startsWith(prefix))));
}

function readText(dir, file) {
  try {
    return readFileSync(join(dir, file), 'utf8');
  } catch {
    return null;
  }
}

export function scanRepo(dir, config = {}, only = null) {
  const result = {};
  for (const file of trackedFiles(dir, config, only)) {
    const text = readText(dir, file);
    const comments = text === null ? null : scanText(text, file, config);
    if (comments) result[file] = comments;
  }
  return result;
}

export function loadBaseline(dir, path) {
  const tools = join(dir, 'tools', 'comment-ratchet');
  const candidates = [join(dir, '.claude', BASELINE_NAME), join(tools, BASELINE_NAME)];
  const file = path || candidates.find((candidate) => existsSync(candidate)) || candidates[existsSync(tools) ? 1 : 0];
  return { path: file, data: existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : null };
}

function countsOf(scan) {
  const files = {};
  for (const file of Object.keys(scan).sort()) {
    const { lines } = measure(scan[file]);
    if (lines > 0) files[file] = lines;
  }
  return files;
}

function sum(files) {
  return Object.values(files || {}).reduce((total, n) => total + n, 0);
}

function baselineAt(dir, ref, path) {
  try {
    const relative = git(dir, ['ls-files', '--full-name', '--', path]).trim();
    return relative ? JSON.parse(git(dir, ['show', `${ref}:${relative}`])) : null;
  } catch {
    return null;
  }
}

export function check(dir, options = {}) {
  const { path, data } = loadBaseline(dir, options.baseline);
  if (!data) return { ok: false, problems: [`no baseline at ${path}: run comment-ratchet write`] };
  const current = countsOf(scanRepo(dir, data));
  const allowed = data.files || {};
  const problems = [];
  for (const file of [...new Set([...Object.keys(current), ...Object.keys(allowed)])].sort()) {
    const now = current[file] || 0;
    const was = allowed[file] || 0;
    if (now > was) problems.push(`${file}: ${now} comment lines, the baseline allows ${was}. Delete the comment; a decision worth keeping goes in an ADR.`);
    else if (now < was) problems.push(`${file}: ${now} comment lines, the baseline still says ${was}. Run comment-ratchet write to lock the drop in.`);
  }
  const base = options.base ? baselineAt(dir, options.base, path) : null;
  if (base && sum(allowed) > sum(base.files)) {
    problems.push(`the baseline total rose from ${sum(base.files)} to ${sum(allowed)} against ${options.base}: the ratchet only goes down.`);
  }
  return { ok: problems.length === 0, problems, total: sum(current), baseline: sum(allowed) };
}

export function write(dir, options = {}) {
  const { path, data } = loadBaseline(dir, options.baseline);
  const config = data || { exclude: options.exclude || [], directives: [], keepDocstrings: false };
  const files = countsOf(scanRepo(dir, config));
  const next = { version: 1, exclude: config.exclude || [], directives: config.directives || [], keepDocstrings: Boolean(config.keepDocstrings), total: sum(files), files };
  if (data && !options.force && next.total > sum(data.files)) {
    return { ok: false, problems: [`refusing to raise the baseline from ${sum(data.files)} to ${next.total} comment lines. Delete the new comments.`] };
  }
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `${JSON.stringify(next, null, 2)}\n`);
  return { ok: true, path, total: next.total, was: data ? sum(data.files) : null };
}

export function report(dir, options = {}) {
  const config = loadBaseline(dir, options.baseline).data || { exclude: options.exclude || [] };
  const scan = scanRepo(dir, config, options.only);
  const byExt = {};
  const totals = { files: 0, comments: 0, lines: 0, shed: 0, trailing: 0, exempt: 0 };
  for (const [file, comments] of Object.entries(scan)) {
    const m = measure(comments);
    const row = (byExt[familyOf(file).ext] ||= { files: 0, comments: 0, lines: 0, shed: 0, trailing: 0 });
    if (m.comments) {
      row.files++;
      totals.files++;
    }
    for (const key of ['comments', 'lines', 'shed', 'trailing']) {
      row[key] += m[key];
      totals[key] += m[key];
    }
    totals.exempt += comments.filter((c) => c.exempt).length;
  }
  return { totals, byExt };
}

export function units(text, file, config = {}, context = 0) {
  const comments = (scanText(text, file, config) || []).filter((c) => !c.exempt);
  const lines = text.split('\n');
  const out = [];
  for (const c of comments) {
    const last = out[out.length - 1];
    if (last && c.whole && last.whole && c.kind === 'line' && last.kind === 'line' && c.line === last.endLine + 1) {
      last.endLine = c.endLine;
      last.text += `\n${c.text}`;
      continue;
    }
    out.push({ file, line: c.line, endLine: c.endLine, kind: c.kind, whole: c.whole, text: c.text });
  }
  for (const unit of out) {
    if (!unit.whole) unit.code = lines[unit.line - 1].trim().slice(0, 200);
    if (context > 0) {
      unit.after = lines
        .slice(unit.endLine, unit.endLine + context)
        .map((line) => line.replace(/\r$/, ''))
        .join('\n');
    }
  }
  return out;
}

export function list(dir, options = {}) {
  const config = loadBaseline(dir, options.baseline).data || { exclude: options.exclude || [] };
  const rows = [];
  for (const file of trackedFiles(dir, config, options.only)) {
    const text = readText(dir, file);
    if (text !== null) rows.push(...units(text, file, config, options.context || 0));
  }
  return rows;
}

function parseRanges(path) {
  const byFile = new Map();
  for (const raw of readFileSync(path, 'utf8').split('\n')) {
    const match = /^(.+):(\d+)(?:-(\d+))?\s*$/.exec(raw.trim());
    if (!match) continue;
    const ranges = byFile.get(match[1]) || [];
    ranges.push([Number(match[2]), Number(match[3] || match[2])]);
    byFile.set(match[1], ranges);
  }
  return byFile;
}

export function strip(dir, options = {}) {
  const config = loadBaseline(dir, options.baseline).data || { exclude: options.exclude || [] };
  const ranges = options.ids ? parseRanges(options.ids) : null;
  const keep = options.keep ? parseRanges(options.keep) : null;
  let files = 0;
  let removed = 0;
  let lines = 0;
  for (const file of trackedFiles(dir, config, options.only)) {
    if (ranges && !ranges.has(file)) continue;
    const text = readText(dir, file);
    if (text === null) continue;
    const inAny = (set, c) => (set.get(file) || []).some(([from, to]) => c.line >= from && c.line <= to);
    const wanted = (c) => (!ranges || inAny(ranges, c)) && !(keep && inAny(keep, c));
    const result = stripText(text, file, config, wanted);
    if (result.text === text) continue;
    files++;
    removed += result.removed;
    lines += text.split('\n').length - result.text.split('\n').length;
    if (!options.dryRun) writeFileSync(join(dir, file), result.text);
  }
  return { files, removed, lines };
}

function parseArgs(argv) {
  const options = { dir: process.cwd() };
  const rest = [];
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--dir') options.dir = resolve(argv[++i]);
    else if (arg === '--baseline') options.baseline = resolve(argv[++i]);
    else if (arg === '--base') options.base = argv[++i];
    else if (arg === '--only') options.only = argv[++i].split(',');
    else if (arg === '--exclude') options.exclude = argv[++i].split(',');
    else if (arg === '--ids') options.ids = resolve(argv[++i]);
    else if (arg === '--keep') options.keep = resolve(argv[++i]);
    else if (arg === '--context') options.context = Number(argv[++i]);
    else if (arg === '--force') options.force = true;
    else if (arg === '--dry-run') options.dryRun = true;
    else if (arg === '--json') options.json = true;
    else rest.push(arg);
  }
  return { command: rest[0] || 'check', options };
}

const USAGE = `usage: comment-ratchet <command> [--dir <checkout>] [--baseline <file>]
  check [--base <ref>]     fail when a file holds more or fewer comment lines than the baseline, or the baseline total rose against <ref>
  write [--force]          rewrite the baseline from the tree; refuses a higher total without --force
  report [--json]          comment counts by file type, and the lines deleting them would shed
  list [--only <prefix,..>] [--context <n>]
                           one JSON line per comment: file, line, endLine, text, and <n> lines of the code after it
  strip [--only <prefix,..>] [--ids <file>] [--keep <file>] [--dry-run]
                           delete comments; --ids and --keep name them as path:line or path:line-endLine, one per line`;

function main() {
  const { command, options } = parseArgs(process.argv.slice(2));
  if (command === 'check') {
    const result = check(options.dir, options);
    if (result.ok) {
      console.log(`comment-ratchet: ${result.total} comment lines, level with the baseline.`);
      return 0;
    }
    for (const problem of result.problems.slice(0, 50)) console.log(problem);
    if (result.problems.length > 50) console.log(`and ${result.problems.length - 50} more.`);
    console.log('comment-ratchet: failed. Comments are banned and the baseline only goes down.');
    return 1;
  }
  if (command === 'write') {
    const result = write(options.dir, options);
    if (!result.ok) {
      for (const problem of result.problems) console.log(problem);
      return 1;
    }
    console.log(`comment-ratchet: baseline ${result.was ?? 'new'} -> ${result.total} comment lines (${result.path}).`);
    return 0;
  }
  if (command === 'report') {
    const result = report(options.dir, options);
    if (options.json) {
      console.log(JSON.stringify(result));
      return 0;
    }
    const cell = (n) => String(n).padStart(7);
    for (const [ext, row] of Object.entries(result.byExt).sort((a, b) => b[1].lines - a[1].lines)) {
      if (row.lines) console.log(`${ext.padEnd(12)} files ${cell(row.files)}  comments ${cell(row.comments)}  lines ${cell(row.lines)}  shed ${cell(row.shed)}`);
    }
    const t = result.totals;
    console.log(`${'total'.padEnd(12)} files ${cell(t.files)}  comments ${cell(t.comments)}  lines ${cell(t.lines)}  shed ${cell(t.shed)}  exempt ${t.exempt}`);
    return 0;
  }
  if (command === 'list') {
    for (const row of list(options.dir, options)) console.log(JSON.stringify(row));
    return 0;
  }
  if (command === 'strip') {
    const result = strip(options.dir, options);
    console.log(`comment-ratchet: ${options.dryRun ? 'would delete' : 'deleted'} ${result.removed} comments in ${result.files} files, ${result.lines} lines shed.`);
    return 0;
  }
  console.log(USAGE);
  return 2;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  process.exitCode = main();
}
