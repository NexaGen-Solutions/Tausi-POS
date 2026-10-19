import type { ReactNode } from 'react';

export interface TopBarProps {
  /** Usually a back button, e.g. "Tables". */
  start?: ReactNode;
  title: ReactNode;
  subtitle?: ReactNode;
  /** Shown on the right, e.g. "Counter A · edna (Waiter)" or buttons. */
  end?: ReactNode;
}

export function TopBar({ start, title, subtitle, end }: TopBarProps) {
  return (
    <header className="nx-topbar">
      {start}
      <h1 className="nx-topbar__title">{title}</h1>
      {subtitle && <span className="nx-topbar__subtitle">{subtitle}</span>}
      <span className="nx-topbar__spacer" />
      {end && <div className="nx-topbar__actions">{end}</div>}
    </header>
  );
}
