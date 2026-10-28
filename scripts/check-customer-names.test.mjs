// The customer-name check catches a planted name, and passes on the repo. node --test scripts/check-customer-names.test.mjs
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { test } from 'node:test';
import { findCustomerNames } from './check-customer-names.mjs';

const fixture = (files) => (name) => Buffer.from(files[name]);

test('a planted name in a test fixture fails the check, with its file and line', () => {
  const files = {
    'server/tests/Fixture.cs': 'var business = "Demo Bar";\nvar other = "Mau\'s Village";\n',
    'web/src/fixture.ts': 'const supervisor = "MAU";\nconst fine = "Kamau and maureen";\n',
  };
  const found = findCustomerNames(Object.keys(files), fixture(files));
  assert.equal(found.length, 2);
  assert.match(found[0], /^server\/tests\/Fixture\.cs:2: .*\(Mau's\)$/);
  assert.match(found.join('\n'), /web\/src\/fixture\.ts:1: .*\(MAU\)/);
});

test('history keeps the names: prompts and decisions are not checked', () => {
  const files = { 'docs/prompts/01.md': "Mau's Village", 'docs/DECISIONS.md': 'POSSERVER2' };
  assert.deepEqual(findCustomerNames(Object.keys(files), fixture(files)), []);
});

test('the repo has no customer names', () => {
  const root = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
  const files = execFileSync('git', ['ls-files'], { cwd: root, encoding: 'utf8' }).split('\n').filter(Boolean);
  process.chdir(root);
  assert.deepEqual(findCustomerNames(files), []);
});
