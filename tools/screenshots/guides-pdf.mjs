// The printable PDFs of the user guides: docs/guides/<name>.md (with its pictures in docs/guides/images,
// from `node capture.mjs guides`) → docs/guides/pdf/<name>.pdf, A4.   node guides-pdf.mjs
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { marked } from 'marked';
import { chromium } from 'playwright';

const guides = resolve(dirname(fileURLToPath(import.meta.url)), '../../docs/guides');
mkdirSync(join(guides, 'pdf'), { recursive: true });

// Tausi's deep teal for headings; plain print otherwise. Pictures are kept small enough for 1–2 pages.
const style = `
  @page { size: A4; margin: 14mm 15mm; }
  body { font-family: 'IBM Plex Sans', 'DejaVu Sans', Arial, sans-serif; font-size: 10.5pt; line-height: 1.4; color: #1c1c1c; }
  h1 { color: #0a4a52; font-size: 20pt; margin: 0 0 2mm; }
  h1 + p { color: #3d3d3a; margin-top: 0; }
  h2 { color: #0a4a52; font-size: 13pt; margin: 5mm 0 1.5mm; break-after: avoid; }
  p, li { margin: 1mm 0; }
  ul, ol { padding-left: 6mm; margin: 1mm 0; }
  /* Pictures sit beside the text they belong to, to keep each guide to one or two pages. */
  img { float: right; clear: right; width: 62mm; margin: 0 0 2mm 4mm; border: 0.3mm solid #c9c9c4; break-inside: avoid; }
  h1, footer { clear: both; }
  code { font-family: 'IBM Plex Mono', 'DejaVu Sans Mono', monospace; font-size: 9.5pt; }
  footer { margin-top: 6mm; color: #5c5c58; font-size: 8.5pt; }
`;

const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  for (const file of readdirSync(guides).filter((f) => f.endsWith('.md')).sort()) {
    const name = file.replace(/\.md$/, '');
    const body = marked.parse(readFileSync(join(guides, file), 'utf8'));
    const html = join(guides, `.${name}.html`);
    writeFileSync(html, `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>${name}</title><style>${style}</style></head>` +
      `<body>${body}<footer>Tausi POS by NexaGen Solutions · nexagensolutions.com</footer></body></html>`);
    await page.goto(pathToFileURL(html).href);
    await page.waitForLoadState('load');
    const pdf = await page.pdf({ format: 'A4', printBackground: true, preferCSSPageSize: true });
    rmSync(html);
    writeFileSync(join(guides, 'pdf', `${name}.pdf`), pdf);
    const pages = (pdf.toString('latin1').match(/\/Type\s*\/Page[^s]/g) ?? []).length;
    console.log(`${name}.pdf: ${pages} page(s)`);
  }
} finally {
  await browser.close();
}
