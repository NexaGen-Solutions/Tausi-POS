#!/usr/bin/env node
// The licence texts of everything that ships with Tausi POS, for the installers:
//   cd tools/notices && npm ci && node build-notices.mjs           writes THIRD-PARTY-LICENSES.txt
//   node build-notices.mjs --check                                 fails if THIRD-PARTY-NOTICES misses a component
//
// What ships (the same three lists as THIRD-PARTY-NOTICES):
//   - the server: NuGet packages in the published Windows and Linux servers (dotnet publish to a temporary folder,
//     then their .deps.json), read from the NuGet cache (~/.nuget/packages);
//   - the web app: production npm packages (web/package-lock.json), read from web/node_modules;
//   - the terminal app: Rust crates in its Windows build (cargo metadata), read from Cargo's registry.
// Each component's own LICENSE / LICENCE / COPYING / NOTICE files are used; a package without one gets the
// standard SPDX text of the licence it declares, under its authors' names.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import spdx from 'spdx-license-list/full.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const at = (...p) => join(root, ...p);
const check = process.argv.includes('--check');

const LICENCE_FILE = /^(licen[cs]e|copying|notice|unlicense|copyright)([-._].*)?$/i;

/** The licence files in a package folder, as [name, text]. */
function licenceFiles(dir) {
  if (!dir || !existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true })
    .filter((e) => e.isFile() && LICENCE_FILE.test(e.name) && !/\.(png|svg|jpg|html?)$/i.test(e.name))
    .sort((a, b) => a.name.localeCompare(b.name))
    .map((e) => [e.name, readFileSync(join(dir, e.name), 'utf8').replace(/\r\n/g, '\n').trim()]);
}

/** The SPDX texts for a licence expression ("MIT OR Apache-2.0"), for packages that carry no file. */
function spdxTexts(expression) {
  const ids = (expression ?? '').match(/[A-Za-z0-9.+-]+/g)?.filter((id) => !['AND', 'OR', 'WITH'].includes(id)) ?? [];
  return ids.filter((id) => spdx[id]).map((id) => [`${id} (standard text)`, spdx[id].licenseText.trim()]);
}

// ---- Server: NuGet packages in the published Windows server (its .deps.json) ----
function server() {
  // Windows and Linux ship slightly different native packages: the list covers both.
  const libraries = new Map();
  for (const rid of ['win-x64', 'linux-x64']) {
    const out = join(mkdtempSync(join(tmpdir(), 'tausi-notices-')), 'server');
    execFileSync('dotnet', ['publish', at('server/src/Tausi.Api'), '-c', 'Release', '-r', rid, '--self-contained', 'true', '-o', out, '-v', 'q'],
      { stdio: ['ignore', 'ignore', 'inherit'] });
    const deps = JSON.parse(readFileSync(join(out, readdirSync(out).find((f) => f.endsWith('.deps.json'))), 'utf8'));
    rmSync(dirname(out), { recursive: true, force: true });
    for (const [key, l] of Object.entries(deps.libraries))
      if (l.type === 'package' && !/^runtimepack\./i.test(l.path ?? '')) libraries.set(key, l);
  }
  return [...libraries.keys()]
    .map((key) => {
      const [name, version] = key.split('/');
      const dir = join(homedir(), '.nuget/packages', name.toLowerCase(), version.toLowerCase());
      const nuspecPath = join(dir, `${name.toLowerCase()}.nuspec`);
      const nuspec = existsSync(nuspecPath) ? readFileSync(nuspecPath, 'utf8') : '';
      const licence = /<license type="expression">([^<]+)<\/license>/.exec(nuspec)?.[1];
      const licenceFile = /<license type="file">([^<]+)<\/license>/.exec(nuspec)?.[1];
      const authors = /<authors>([^<]+)<\/authors>/.exec(nuspec)?.[1];
      const copyright = /<copyright>([^<]+)<\/copyright>/.exec(nuspec)?.[1];
      let texts = licenceFiles(dir);
      if (licenceFile && existsSync(join(dir, licenceFile)))
        texts = [[licenceFile, readFileSync(join(dir, licenceFile), 'utf8').replace(/\r\n/g, '\n').trim()]];
      const url = /<projectUrl>([^<]+)</.exec(nuspec)?.[1] ?? /<repository [^>]*url="([^"]+)"/.exec(nuspec)?.[1] ?? '';
      return { name, version, licence, texts, by: copyright ?? (authors ? `the authors (${authors})` : null), url };
    });
}

// ---- Web: npm, production only ----
function web() {
  const lock = JSON.parse(readFileSync(at('web/package-lock.json'), 'utf8'));
  return Object.entries(lock.packages)
    .filter(([path, p]) => path.startsWith('node_modules/') && !p.dev && !p.devOptional)
    .map(([path, p]) => {
      const dir = at('web', path);
      const pkg = existsSync(join(dir, 'package.json')) ? JSON.parse(readFileSync(join(dir, 'package.json'), 'utf8')) : {};
      const author = typeof pkg.author === 'string' ? pkg.author : pkg.author?.name;
      const repo = typeof pkg.repository === 'string' ? pkg.repository : pkg.repository?.url;
      const url = (pkg.homepage ?? repo ?? '').replace(/^git\+/, '').replace(/\.git$/, '');
      return { name: path.slice(path.lastIndexOf('node_modules/') + 13), version: p.version, licence: p.license ?? pkg.license, texts: licenceFiles(dir), by: author ?? null, url };
    });
}

// ---- Terminal: Rust crates in the Windows build (normal dependencies only) ----
function terminal() {
  const meta = JSON.parse(
    execFileSync('cargo', ['metadata', '--format-version', '1', '--filter-platform', 'x86_64-pc-windows-msvc',
      '--manifest-path', at('terminal/src-tauri/Cargo.toml')], { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 }),
  );
  const packages = new Map(meta.packages.map((p) => [p.id, p]));
  const nodes = new Map(meta.resolve.nodes.map((n) => [n.id, n]));
  const seen = new Set();
  const walk = (id) => {
    if (seen.has(id)) return;
    seen.add(id);
    // A proc-macro runs only while compiling: neither it nor what only it uses is in the program.
    if (packages.get(id)?.targets.some((t) => t.kind.includes('proc-macro'))) return;
    for (const dep of nodes.get(id)?.deps ?? [])
      if (dep.dep_kinds.some((k) => k.kind === null)) walk(dep.pkg);
  };
  walk(meta.resolve.root);
  return [...seen]
    .map((id) => packages.get(id))
    .filter((p) => p && p.source && !p.targets.some((t) => t.kind.includes('proc-macro'))) // not our own crates, not macros
    .map((p) => ({ name: p.name, version: p.version, licence: p.license, texts: licenceFiles(dirname(p.manifest_path)), by: p.authors?.join(', ') || null, url: p.repository ?? p.homepage ?? '' }));
}

const groups = [
  ['Server (.NET, NuGet)', server()],
  ['Web app (npm, production dependencies)', web()],
  ['Terminal app (Rust crates in the Windows build)', terminal()],
];
for (const [, list] of groups) list.sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version));

// ---- THIRD-PARTY-NOTICES: the "ALL DISTRIBUTED COMPONENTS" lists are written from the lists above ----
const ALL = 'ALL DISTRIBUTED COMPONENTS';
const titles = {
  'Server (.NET, NuGet)': (n) => `Server (.NET, NuGet): ${n} packages`,
  'Web app (npm, production dependencies)': (n) => `Web app (npm, production dependencies): ${n} packages`,
  'Terminal app (Rust crates in the Windows build)': (n) => `Terminal app with its print library (Rust crates in the Windows build): ${n} crates`,
};
function table(list) {
  const first = list.map((c) => `${c.name} ${c.version}`);
  const w1 = Math.max(...first.map((f) => f.length)) + 2;
  const w2 = Math.max(...list.map((c) => (c.licence ?? '').length)) + 2;
  return list.map((c, i) => `  ${first[i].padEnd(w1)}${(c.licence ?? '').padEnd(w2)}${c.url}`.trimEnd()).join('\n');
}
const oldNotices = readFileSync(at('THIRD-PARTY-NOTICES'), 'utf8');
const allSection = `${ALL}\n\n` + groups.map(([title, list]) => `${titles[title](list.length)}\n${table(list)}`).join('\n\n') + '\n';
const newNotices = oldNotices.slice(0, oldNotices.indexOf(`\n${ALL}\n`) + 1) + allSection;

// The hand-written "DIRECT DEPENDENCIES" must name versions that ship (installer and print-library lines aside).
const shipped = new Set(groups.flatMap(([, list]) => list.map((c) => `${c.name} ${c.version}`)));
const direct = newNotices.slice(newNotices.indexOf('DIRECT DEPENDENCIES'), newNotices.indexOf('Server installer (Windows)'))
  .split('\n').map((l) => /^  (\S+) (\S+)\s/.exec(l)).filter(Boolean).map((m) => `${m[1]} ${m[2]}`);
const staleDirect = direct.filter((d) => !shipped.has(d));

if (check) {
  const problems = [];
  if (newNotices !== oldNotices) problems.push('THIRD-PARTY-NOTICES: its component lists are out of date.');
  if (staleDirect.length) problems.push(`THIRD-PARTY-NOTICES: DIRECT DEPENDENCIES names versions that don't ship: ${staleDirect.join(', ')}.`);
  const licences = existsSync(at('THIRD-PARTY-LICENSES.txt')) ? readFileSync(at('THIRD-PARTY-LICENSES.txt'), 'utf8') : '';
  if (licences !== licenceText().text) problems.push('THIRD-PARTY-LICENSES.txt is out of date.');
  if (problems.length) {
    console.error(`${problems.join('\n')}\nRun: cd tools/notices && npm ci && node build-notices.mjs (then commit both files).`);
    process.exit(1);
  }
  console.log(`THIRD-PARTY-NOTICES and THIRD-PARTY-LICENSES.txt cover all ${shipped.size} shipped components.`);
  process.exit(0);
}

function licenceText() {
// Components that share exactly the same text are listed together under it.
  const out = [
    'THIRD-PARTY LICENCE TEXTS',
    '',
    'Tausi POS',
    'Copyright (c) 2026 NexaGen Solutions (nexagensolutions.com). All rights reserved.',
    '',
    'The full licence texts of the third-party components that ship with Tausi POS, as listed in',
    'THIRD-PARTY-NOTICES. Each text is the one in the component\'s own package. Where a package has no',
    'licence file, the standard text of the licence it declares is given, for its named authors.',
    'Made by tools/notices/build-notices.mjs; do not edit by hand.',
    '',
  ];
  const missingText = [];
  for (const [title, list] of groups) {
    out.push('', '='.repeat(100), `${title}: ${list.length} components`, '='.repeat(100));
    const byText = new Map();
    for (const c of list) {
      const texts = c.texts.length ? c.texts : spdxTexts(c.licence);
      if (!texts.length) missingText.push(`${c.name} ${c.version} (${c.licence ?? 'no licence declared'})`);
      const key = texts.map(([, t]) => t).join('\n\n');
      if (!byText.has(key)) byText.set(key, { texts, components: [] });
      byText.get(key).components.push(c);
    }
    for (const { texts, components } of byText.values()) {
      out.push('', '-'.repeat(100));
      for (const c of components)
        out.push(`${c.name} ${c.version}${c.licence ? `  (${c.licence})` : ''}${!c.texts.length && c.by ? `  Copyright ${c.by}` : ''}`);
      out.push('-'.repeat(100));
      for (const [name, text] of texts) out.push('', `[${name}]`, '', text);
    }
  }
  return { text: out.join('\n') + '\n', missingText };
}

const { text, missingText } = licenceText();
writeFileSync(at('THIRD-PARTY-LICENSES.txt'), text);
writeFileSync(at('THIRD-PARTY-NOTICES'), newNotices);
if (staleDirect.length) console.error(`Update DIRECT DEPENDENCIES in THIRD-PARTY-NOTICES by hand: ${staleDirect.join(', ')}`);
console.log(`THIRD-PARTY-LICENSES.txt: ${groups.map(([t, l]) => `${l.length} ${t.split(' ')[0].toLowerCase()}`).join(', ')}.`);
if (missingText.length) {
  console.error(`No licence text found for:\n  ${missingText.join('\n  ')}`);
  process.exit(1);
}
