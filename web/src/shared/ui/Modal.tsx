import { useEffect, useId, useRef, type CSSProperties, type ReactNode } from 'react';

export interface ModalProps {
  title: ReactNode;
  subtitle?: ReactNode;
  /** Called on Escape or a click outside. Leave out to make the dialog stay until answered. */
  onClose?: () => void;
  width?: number;
  children: ReactNode;
}

export function Modal({ title, subtitle, onClose, width, children }: ModalProps) {
  const titleId = useId();
  const dialog = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.focus();
    return () => previous?.focus();
  }, []);

  useEffect(() => {
    if (!onClose) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div
      className="nx-modal__backdrop"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose?.();
      }}
    >
      <div
        ref={dialog}
        className="nx-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        style={width ? ({ '--modal-width': `${width}px` } as CSSProperties) : undefined}
      >
        <h2 id={titleId} className="nx-modal__title">
          {title}
        </h2>
        {subtitle && <p className="nx-modal__subtitle">{subtitle}</p>}
        <div className="nx-modal__body">{children}</div>
      </div>
    </div>
  );
}
