import type { ReactNode } from 'react';
import { Button } from './Button';

export interface BannerProps {
  tone?: 'warning' | 'info';
  children: ReactNode;
  action?: { label: string; onClick: () => void };
}

/** A full-width strip under the top bar, e.g. "No connection" or "Not printed · Retry". */
export function Banner({ tone = 'warning', children, action }: BannerProps) {
  return (
    <div className={`nx-banner nx-banner--${tone}`} role="alert">
      <span className="nx-banner__text">{children}</span>
      {action && <Button onClick={action.onClick}>{action.label}</Button>}
    </div>
  );
}
