// Checks the files tools/brand/build-icons.mjs makes: every required size is there, and the Tauri
// config and the web page's head point at files that exist.   node --test tools/brand/icons.test.mjs
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const at = (...p) => join(root, ...p);

/** Width and height of a PNG (from its IHDR chunk). */
function pngSize(file) {
  const data = readFileSync(file);
  assert.equal(data.toString('ascii', 1, 4), 'PNG', `${file} is not a PNG`);
  return [data.readUInt32BE(16), data.readUInt32BE(20)];
}

/** The sizes in an .ico (0 in the directory means 256). */
function icoSizes(file) {
  const data = readFileSync(file);
  assert.equal(data.readUInt16LE(2), 1, `${file} is not an icon`);
  return Array.from({ length: data.readUInt16LE(4) }, (_, i) => data.readUInt8(6 + 16 * i) || 256);
}

/** Width, height and bits per pixel of a .bmp. */
function bmp(file) {
  const data = readFileSync(file);
  assert.equal(data.toString('ascii', 0, 2), 'BM', `${file} is not a BMP`);
  return [data.readInt32LE(18), Math.abs(data.readInt32LE(22)), data.readUInt16LE(28)];
}

test('web icons: favicon.ico 16/32/48, the touch icon and the app icons', () => {
  assert.deepEqual(icoSizes(at('web/public/favicon.ico')), [16, 32, 48]);
  assert.match(readFileSync(at('web/public/favicon.svg'), 'utf8'), /<svg/);
  assert.deepEqual(pngSize(at('web/public/apple-touch-icon.png')), [180, 180]);
  assert.deepEqual(pngSize(at('web/public/icon-192.png')), [192, 192]);
  assert.deepEqual(pngSize(at('web/public/icon-512.png')), [512, 512]);
  assert.ok(existsSync(at('web/src/shared/brand/tausi-mark.svg')));
  assert.ok(existsSync(at('web/src/shared/brand/tausi-logo.png')));
});

test("the web page's head links icons that exist", () => {
  const html = readFileSync(at('web/index.html'), 'utf8');
  const links = [...html.matchAll(/<link[^>]+rel="(?:icon|apple-touch-icon)"[^>]*href="\/([^"]+)"/g)].map((m) => m[1]);
  for (const wanted of ['favicon.ico', 'favicon.svg', 'apple-touch-icon.png', 'icon-192.png', 'icon-512.png'])
    assert.ok(links.includes(wanted), `index.html doesn't link ${wanted}`);
  for (const link of links) assert.ok(existsSync(at('web/public', link)), `index.html links a missing ${link}`);
});

test('terminal app: the Tauri icon set and the installer pictures, all referenced files exist', () => {
  const config = JSON.parse(readFileSync(at('terminal/src-tauri/tauri.conf.json'), 'utf8'));
  const tauri = (p) => at('terminal/src-tauri', p);
  for (const icon of config.bundle.icon) assert.ok(existsSync(tauri(icon)), `tauri.conf.json names a missing ${icon}`);
  assert.deepEqual(pngSize(tauri('icons/32x32.png')), [32, 32]);
  assert.deepEqual(pngSize(tauri('icons/128x128.png')), [128, 128]);
  assert.deepEqual(pngSize(tauri('icons/128x128@2x.png')), [256, 256]);
  for (const size of [16, 32, 48, 256]) assert.ok(icoSizes(tauri('icons/icon.ico')).includes(size), `icon.ico has no ${size}`);

  const nsis = config.bundle.windows.nsis;
  assert.deepEqual(icoSizes(tauri(nsis.installerIcon)), [16, 24, 32, 48, 64, 256]);
  assert.deepEqual(bmp(tauri(nsis.headerImage)), [150, 57, 24]);
  assert.deepEqual(bmp(tauri(nsis.sidebarImage)), [164, 314, 24]);
});

test('server installer: the icon, banner 493×58 and dialog 493×312, embedded in the setup program', () => {
  assert.deepEqual(icoSizes(at('installer/windows/tausi.ico')), [16, 24, 32, 48, 64, 256]);
  assert.deepEqual(pngSize(at('installer/windows/TausiSetup/banner.png')), [493, 58]);
  assert.deepEqual(pngSize(at('installer/windows/TausiSetup/dialog.png')), [493, 312]);
  const project = readFileSync(at('installer/windows/TausiSetup/TausiSetup.csproj'), 'utf8');
  for (const picture of ['banner.png', 'dialog.png'])
    assert.match(project, new RegExp(`EmbeddedResource Include="${picture}"`));
});

test('below 64 px only the mark is used: the small icons come from the square mark', () => {
  // The full logo is 2400×819; a small icon made from it would not be square.
  for (const file of ['web/public/icon-192.png', 'terminal/src-tauri/icons/32x32.png']) {
    const [w, h] = pngSize(at(file));
    assert.equal(w, h, `${file} is not square`);
  }
});
