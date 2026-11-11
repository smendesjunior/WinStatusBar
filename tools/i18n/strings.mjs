// Translation helper for Strings/<lang>/Resources.resw (six languages).
//
//   node tools/i18n/strings.mjs missing [path ...]   list Localization.T/F literals that have no key in some language
//   node tools/i18n/strings.mjs get "<source text>"  print the stored translations of one text
//   node tools/i18n/strings.mjs apply <file.json>    add or replace entries
//
// apply file: [ { "source": "Texto em português", "en-US": "...", "es": "...", "fr-FR": "...", "ja-JP": "...", "zh-Hans": "..." },
//               { "key": "SomeUid.Text", "pt-BR": "...", "en-US": "...", ... } ]
// "source" entries use the key of Localization.T ("S" + first 16 hex digits of SHA-256 of the text) and pt-BR = source.
// "key" entries (x:Uid resources such as "General.Content") need every language, pt-BR included.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const languages = ['pt-BR', 'en-US', 'es', 'fr-FR', 'ja-JP', 'zh-Hans'];
const reswPath = lang => join(root, 'src', 'WinStatusBar', 'Strings', lang, 'Resources.resw');

export const keyOf = source => 'S' + createHash('sha256').update(source, 'utf8').digest('hex').slice(0, 16).toUpperCase();
const escapeXml = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const unescapeXml = s => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
const escapeRegex = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const entryPattern = name => new RegExp('(<data name="' + escapeRegex(name) + '"[^>]*>\\s*<value>)([\\s\\S]*?)(</value>)');

export function load(lang) {
  const text = readFileSync(reswPath(lang), 'utf8');
  const map = new Map();
  for (const m of text.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)) map.set(m[1], unescapeXml(m[2]));
  return { text, map };
}

/** Adds or replaces entries ({ name: value }) in one language file. Returns [added, replaced]. */
export function write(lang, entries) {
  let { text } = load(lang);
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  let added = 0, replaced = 0, block = '';
  for (const [name, value] of Object.entries(entries)) {
    const pattern = entryPattern(name);
    if (pattern.test(text)) {
      const before = text;
      text = text.replace(pattern, (_, open, __, close) => open + escapeXml(value) + close);
      if (text !== before) replaced++;
    } else {
      block += `  <data name="${name}" xml:space="preserve">${eol}    <value>${escapeXml(value)}</value>${eol}  </data>${eol}`;
      added++;
    }
  }
  if (block) text = text.replace(/<\/root>\s*$/, block + '</root>');
  writeFileSync(reswPath(lang), text, 'utf8');
  return [added, replaced];
}

/** String literals passed first to Localization.T/F or LocalizedText.T/F in a C# file. */
export function literals(file) {
  const text = readFileSync(file, 'utf8');
  const found = [];
  for (const m of text.matchAll(/(?:LocalizedText|Localization)\.[TF]\(\s*"((?:[^"\\]|\\.)*)"/g))
    found.push(m[1].replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)))
      .replace(/\\(["\\nrt])/g, (_, c) => ({ '"': '"', '\\': '\\', n: '\n', r: '\r', t: '\t' }[c])));
  return found;
}

function csFiles(path) {
  if (statSync(path).isFile()) return path.endsWith('.cs') ? [path] : [];
  return readdirSync(path).filter(n => n !== 'bin' && n !== 'obj').flatMap(n => csFiles(join(path, n)));
}

const [command, ...args] = process.argv.slice(2);
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (command === 'missing') {
    const maps = Object.fromEntries(languages.map(l => [l, load(l).map]));
    const seen = new Set();
    for (const path of args.length ? args : [join(root, 'src')])
      for (const file of csFiles(resolve(path)))
        for (const source of literals(file)) {
          if (seen.has(source)) continue;
          seen.add(source);
          const absent = languages.filter(l => !maps[l].has(keyOf(source)));
          if (absent.length) console.log(JSON.stringify({ source, missing: absent.join(',') }));
        }
  } else if (command === 'get') {
    for (const l of languages) console.log(l.padEnd(8), load(l).map.get(args[0].includes('.') && !args[0].includes(' ') ? args[0] : keyOf(args[0])) ?? '(none)');
  } else if (command === 'apply') {
    const items = JSON.parse(readFileSync(resolve(args[0]), 'utf8'));
    const perLanguage = Object.fromEntries(languages.map(l => [l, {}]));
    for (const item of items) {
      const name = item.key ?? keyOf(item.source);
      for (const l of languages) {
        const value = l === 'pt-BR' && item.source !== undefined ? item.source : item[l];
        if (typeof value !== 'string' || value.length === 0) throw new Error(`${name}: no ${l} text (${item.source ?? item.key})`);
        for (const hole of ['{0}', '{1}', '{2}']) if ((item.source ?? item['pt-BR']).includes(hole) && !value.includes(hole)) throw new Error(`${name}: ${l} lost ${hole}`);
        perLanguage[l][name] = value;
      }
    }
    for (const l of languages) { const [a, r] = write(l, perLanguage[l]); console.log(`${l}: ${a} added, ${r} replaced`); }
  } else {
    console.log('usage: strings.mjs missing [path ...] | get "<text or key>" | apply <file.json>');
  }
}
