import { describe, expect, it } from 'vitest';
import css from './tokens.css?raw';

/**
 * WCAG AA for the colour tokens: every text/background pair the UI uses reaches 4.5:1 (3:1 for large
 * text and for icons, borders and focus rings), and gold is never text on a light ground.
 */

/** Every stylesheet in the web app, by path. */
const sheets = import.meta.glob<string>('../../**/*.css', {
  query: '?raw',
  import: 'default',
  eager: true,
});

/** Lines of every stylesheet but tokens.css that match a pattern, as "path:line: text". */
function linesMatching(pattern: RegExp): string[] {
  return Object.entries(sheets)
    .filter(([path]) => !path.endsWith('/tokens.css'))
    .flatMap(([path, text]) =>
      text
        .split('\n')
        .map((line, i) => ({ line: line.trim(), at: `${path}:${i + 1}` }))
        .filter(({ line }) => pattern.test(line))
        .map(({ at, line }) => `${at}: ${line}`),
    );
}

function token(name: string): string {
  const match = new RegExp(`--color-${name}:\\s*([^;]+);`).exec(css);
  if (!match) throw new Error(`No token --color-${name}`);
  const value = match[1].trim();
  const alias = /^var\(--color-([\w-]+)\)$/.exec(value);
  return alias ? token(alias[1]) : value;
}

function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrast(foreground: string, background: string): number {
  const [a, b] = [luminance(token(foreground)), luminance(token(background))].sort((x, y) => y - x);
  return (a + 0.05) / (b + 0.05);
}

const lightGrounds = ['ground', 'ground-brand', 'panel'];

/** [text, background]: normal-size text, 4.5:1. */
const textPairs: [string, string][] = [
  ...lightGrounds.flatMap((g) => [
    ['text', g] as [string, string],
    ['text-secondary', g] as [string, string],
    ['text-muted', g] as [string, string],
    ['warning-text', g] as [string, string],
    ['selected', g] as [string, string],
  ]),
  ['surface-dark-text', 'surface-dark'],
  ['surface-dark-muted', 'surface-dark'],
  ['topbar-text', 'topbar'],
  ['topbar-muted', 'topbar'],
  ['topbar-text', 'surface-dark-raised'],
  ['surface-dark-muted', 'surface-dark-raised'],
  ['primary-text', 'primary'],
  ['primary-text', 'primary-pressed'],
  ['accent-text', 'accent'],
  ['selected-text', 'selected'],
  ['selected', 'selected-tint'],
  ['text', 'selected-tint'],
  ['warning-on', 'warning'],
  // The training band: white on both of its orange stripes.
  ['warning-on', 'warning-text'],
  ['warning-text', 'warning-tint'],
  ['text', 'row-alt'],
  ['text-secondary', 'readonly'],
  ['text-muted', 'disabled'],
  ['text-muted', 'key-disabled'],
];

/** Large or bold text (19 px bold and up), 3:1: the wizard's current step number. */
const largeTextPairs: [string, string][] = [['surface-dark', 'accent']];

/** Icons, borders and indicators, 3:1. */
const graphicPairs: [string, string][] = [
  // The focus ring: gold, 2 px outside the element (so it sits on the ground, never on the button),
  // with a deep teal edge: the gold stands out on dark grounds, the edge on light ones.
  ['focus', 'surface-dark'],
  ['focus', 'focus-edge'],
  ...lightGrounds.map((g) => ['focus-edge', g] as [string, string]),
  // Tiles and status dots: occupied (blue), idle (orange), the primary button's edge.
  ...lightGrounds.map((g) => ['selected', g] as [string, string]),
  ...lightGrounds.map((g) => ['warning', g] as [string, string]),
  ...lightGrounds.map((g) => ['primary', g] as [string, string]),
  ['input-border', 'panel'],
];

describe('colour tokens (WCAG AA)', () => {
  it('match the brand colours in AGENTS.md', () => {
    expect(token('primary')).toBe('#0e6670');
    expect(token('primary-pressed')).toBe('#0a4a52');
    expect(token('surface-dark')).toBe('#0a4a52');
    expect(token('ground-brand')).toBe('#f6f1e4');
    expect(token('accent')).toBe('#c9a227');
    expect(token('warning')).toBe('#b34a0c');
    expect(token('selected')).toBe('#1f5fa8');
  });

  it.each(textPairs)('text %s on %s is at least 4.5:1', (fg, bg) => {
    expect(contrast(fg, bg)).toBeGreaterThanOrEqual(4.5);
  });

  it.each(largeTextPairs)('large text %s on %s is at least 3:1', (fg, bg) => {
    expect(contrast(fg, bg)).toBeGreaterThanOrEqual(3);
  });

  it.each(graphicPairs)('graphic %s on %s is at least 3:1', (fg, bg) => {
    expect(contrast(fg, bg)).toBeGreaterThanOrEqual(3);
  });

  it('the brand pairs quoted in the task: teal/white 6.7, deep teal/cream 8.8', () => {
    expect(contrast('primary-text', 'primary')).toBeCloseTo(6.7, 0);
    expect(contrast('surface-dark-text', 'surface-dark')).toBeGreaterThanOrEqual(8.8);
  });

  it('gold is too light for text on cream or white, and no stylesheet uses it as text', () => {
    for (const ground of lightGrounds) expect(contrast('accent', ground)).toBeLessThan(4.5);

    expect(Object.keys(sheets).length).toBeGreaterThan(10);
    expect(css).toContain('--color-accent');
    expect(linesMatching(/(^|[\s;{])color:\s*var\(--color-(accent|focus)\)/)).toEqual([]);
  });

  it('components use tokens, never colour values', () => {
    // The stylesheets are read (not blanked by the test set-up): they are full of tokens.
    expect(linesMatching(/var\(--color-/).length).toBeGreaterThan(100);
    expect(linesMatching(/#[0-9a-f]{3,8}\b|rgba?\(/i)).toEqual([]);
  });
});
