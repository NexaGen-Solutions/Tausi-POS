#!/usr/bin/env node
// Builds every icon and installer image from the two brand sources, so they can be remade whenever
// brand/ changes:   cd tools/brand && npm ci && node build-icons.mjs
//
//   brand/tausi-mark.png (512, the gold peacock head on deep teal) → favicons, app icons, .ico files,
//   the Tauri icon set (through `tauri icon`), and the small images.
//   brand/tausi-mark.svg                                            → favicon.svg, the web UI's mark.
//   brand/tausi-logo-teal.png (2400×819, the full logo on teal)     → the web UI's logo and installer art.
// Below 64 px the mark is always used, never the full logo.
import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const at = (...p) => join(root, ...p);
const MARK = at('brand/tausi-mark.png');
const MARK_SVG = at('brand/tausi-mark.svg');
const LOGO = at('brand/tausi-logo-teal.png');

// Brand colours (AGENTS.md).
const DEEP_TEAL = '#0A4A52';
const CREAM = '#F6F1E4';

const out = (file) => {
  mkdirSync(dirname(at(file)), { recursive: true });
  return at(file);
};

const markPng = (size) => sharp(MARK).resize(size, size).png().toBuffer();

/** An .ico holding a PNG per size (Windows Vista and later read PNG entries). */
async function ico(file, sizes) {
  const images = await Promise.all(sizes.map(markPng));
  const header = Buffer.alloc(6 + 16 * images.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  images.forEach((png, i) => {
    const size = sizes[i];
    const entry = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, entry);
    header.writeUInt8(size >= 256 ? 0 : size, entry + 1);
    header.writeUInt8(0, entry + 2);
    header.writeUInt8(0, entry + 3);
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(png.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  writeFileSync(out(file), Buffer.concat([header, ...images]));
}

/** A 24-bit .bmp (what the NSIS installer's pictures must be), from any image. */
async function bmp(file, image) {
  const { data, info } = await sharp(image).flatten({ background: DEEP_TEAL }).removeAlpha().raw().toBuffer({ resolveWithObject: true });
  const rowSize = Math.ceil((info.width * 3) / 4) * 4;
  const pixels = Buffer.alloc(rowSize * info.height);
  for (let y = 0; y < info.height; y++) {
    for (let x = 0; x < info.width; x++) {
      const from = (y * info.width + x) * 3;
      const to = (info.height - 1 - y) * rowSize + x * 3; // bottom-up, BGR
      pixels[to] = data[from + 2];
      pixels[to + 1] = data[from + 1];
      pixels[to + 2] = data[from];
    }
  }
  const header = Buffer.alloc(54);
  header.write('BM', 0);
  header.writeUInt32LE(54 + pixels.length, 2);
  header.writeUInt32LE(54, 10);
  header.writeUInt32LE(40, 14);
  header.writeInt32LE(info.width, 18);
  header.writeInt32LE(info.height, 22);
  header.writeUInt16LE(1, 26);
  header.writeUInt16LE(24, 28);
  header.writeUInt32LE(pixels.length, 34);
  writeFileSync(out(file), Buffer.concat([header, pixels]));
}

/** "Tausi POS" in cream, as an SVG layer (the system's sans; the files are built once and committed). */
const label = (width, height, x, size) =>
  Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">` +
      `<text x="${x}" y="${height / 2}" dominant-baseline="central" font-family="IBM Plex Sans, DejaVu Sans, Arial, sans-serif" ` +
      `font-size="${size}" font-weight="700" fill="${CREAM}">Tausi POS</text></svg>`,
  );

/** Deep teal with the mark and "Tausi POS" in cream on the left. */
async function banner(width, height) {
  const markSize = height - 12;
  return sharp({ create: { width, height, channels: 4, background: DEEP_TEAL } })
    .composite([
      { input: await markPng(markSize), left: 8, top: 6 },
      { input: label(width, height, markSize + 20, Math.round(height * 0.4)), left: 0, top: 0 },
    ])
    .png()
    .toBuffer();
}

/** A deep teal panel on the left with the full logo fitted, cream on the right (where text goes). */
async function dialog(width, height, panel) {
  const logo = await sharp(LOGO).resize({ width: panel - 16, height: height - 16, fit: 'inside' }).png().toBuffer();
  const { height: logoHeight } = await sharp(logo).metadata();
  return sharp({ create: { width, height, channels: 4, background: CREAM } })
    .composite([
      { input: { create: { width: panel, height, channels: 4, background: DEEP_TEAL } }, left: 0, top: 0 },
      { input: logo, left: 8, top: Math.round((height - logoHeight) / 2) },
    ])
    .png()
    .toBuffer();
}

/** A tall deep teal picture with the mark above and the full logo below (the NSIS welcome sidebar). */
async function sidebar(width, height) {
  const mark = await markPng(width - 48);
  const logo = await sharp(LOGO).resize({ width: width - 16 }).png().toBuffer();
  const { height: logoHeight } = await sharp(logo).metadata();
  return sharp({ create: { width, height, channels: 4, background: DEEP_TEAL } })
    .composite([
      { input: mark, left: 24, top: 32 },
      { input: logo, left: 8, top: height - logoHeight - 40 },
    ])
    .png()
    .toBuffer();
}

// ---- Web (admin site and terminal UI) ----
copyFileSync(MARK_SVG, out('web/public/favicon.svg'));
await ico('web/public/favicon.ico', [16, 32, 48]);
await sharp(MARK).resize(180, 180).flatten({ background: DEEP_TEAL }).png().toFile(out('web/public/apple-touch-icon.png'));
await sharp(MARK).resize(192, 192).png().toFile(out('web/public/icon-192.png'));
await sharp(MARK).resize(512, 512).png().toFile(out('web/public/icon-512.png'));
copyFileSync(MARK_SVG, out('web/src/shared/brand/tausi-mark.svg'));
await sharp(LOGO).resize({ width: 1200 }).png({ compressionLevel: 9, palette: true, quality: 90 }).toFile(out('web/src/shared/brand/tausi-logo.png'));

// ---- Terminal app (Tauri's own icon set: .ico, .icns and every PNG size) ----
execFileSync('npx', ['--prefix', at('terminal'), 'tauri', 'icon', MARK, '-o', at('terminal/src-tauri/icons')], {
  cwd: at('terminal'),
  stdio: 'inherit',
});
// The terminal is a Windows desktop app: no phone icons.
for (const phone of ['android', 'ios']) rmSync(at('terminal/src-tauri/icons', phone), { recursive: true, force: true });
// The terminal installer (NSIS): its icon, and the header (150×57) and welcome sidebar (164×314) pictures.
await ico('terminal/src-tauri/windows/installer.ico', [16, 24, 32, 48, 64, 256]);
await bmp('terminal/src-tauri/windows/nsis-header.bmp', await banner(150, 57));
await bmp('terminal/src-tauri/windows/nsis-sidebar.bmp', await sidebar(164, 314));

// ---- Server installer (Add/Remove Programs icon; the setup screens' banner and welcome picture) ----
await ico('installer/windows/tausi.ico', [16, 24, 32, 48, 64, 256]);
writeFileSync(out('installer/windows/TausiSetup/banner.png'), await banner(493, 58));
writeFileSync(out('installer/windows/TausiSetup/dialog.png'), await dialog(493, 312, 200));

console.log('Brand icons and images built.');
