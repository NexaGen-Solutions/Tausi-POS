import logoUrl from './tausi-logo.png';
import './brand.css';

/**
 * The full Tausi POS logo (peacock, "Tausi", the POS pill and "Simply Outstanding") on its deep teal.
 * Only on deep teal and at 64 px or more; smaller places use {@link TausiMark}.
 */
export function TausiLogo({ width = 320 }: { width?: number }) {
  return <img className="tausi-logo" src={logoUrl} alt="Tausi POS" width={width} />;
}
