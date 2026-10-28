#!/usr/bin/env node
// Tausi POS is sold to many businesses: no customer's name may appear in the product. Fails (exit 1)
// with the file and line of every match in the repo's tracked files, except the history that keeps
// them on purpose (docs/prompts/, docs/DECISIONS.md) and this check itself.
//   node scripts/check-customer-names.mjs
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

/** The names of the first site (a case study). Phrases match in any case; MAU as a whole word, in capitals. */
export const patterns = [
  { name: "Mau's", regex: /mau['’]s/i },
  { name: 'Mau Village', regex: /mau\s+village/i },
  { name: 'POSSERVER2', regex: /posserver2/i },
  { name: 'MAU', regex: /\bMAU\b/ },
];

export const excluded = (file) =>
  file.startsWith('docs/prompts/') ||
  file === 'docs/DECISIONS.md' ||
  file.startsWith('scripts/check-customer-names');

/** Every match as "file:line: text (pattern)". */
export function findCustomerNames(files, read = (f) => readFileSync(f)) {
  const found = [];
  for (const file of files) {
    if (excluded(file)) continue;
    let bytes;
    try {
      bytes = read(file);
    } catch {
      continue; // deleted in the working tree
    }
    if (bytes.includes(0)) continue; // binary
    bytes
      .toString('utf8')
      .split('\n')
      .forEach((line, i) => {
        for (const p of patterns)
          if (p.regex.test(line)) found.push(`${file}:${i + 1}: ${line.trim().slice(0, 120)} (${p.name})`);
      });
  }
  return found;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const files = execFileSync('git', ['ls-files'], { encoding: 'utf8' }).split('\n').filter(Boolean);
  const found = findCustomerNames(files);
  if (found.length > 0) {
    console.error(`Customer names in the product (${found.length}); use "Demo Bar" or a placeholder:`);
    for (const f of found) console.error('  ' + f);
    process.exit(1);
  }
  console.log(`No customer names in ${files.length} files.`);
}
