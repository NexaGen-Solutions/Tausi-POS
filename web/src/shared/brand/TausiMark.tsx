import markSvg from './tausi-mark.svg?raw';
import './brand.css';

// The file's own <title> would add "Tausi POS" to the text of whatever holds the mark; the label below names it.
const svg = markSvg.replace(/<title>[^<]*<\/title>/, '');

/**
 * The Tausi POS mark (the gold peacock head on deep teal), drawn inline so it stays sharp at any
 * size. Use it below 64 px; the full logo only on deep teal and larger. Made from brand/tausi-mark.svg
 * by tools/brand/build-icons.mjs.
 */
export function TausiMark({
  size = 36,
  label = 'Tausi POS',
}: {
  size?: number;
  label?: string | null;
}) {
  return (
    <span
      className="tausi-mark"
      style={{ width: size, height: size }}
      role={label ? 'img' : undefined}
      aria-label={label ?? undefined}
      aria-hidden={label ? undefined : true}
      // A file of our own, checked into the repo: no user content.
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  );
}
