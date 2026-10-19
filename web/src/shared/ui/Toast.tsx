import { useCallback, useState, type ReactNode } from 'react';
import { t } from '../i18n/i18n';
import { ToastContext, type ToastOptions } from './toast-context';

interface ToastItem extends ToastOptions {
  id: number;
  message: string;
}

let nextId = 1;
const visibleFor = 5000;

/** Short messages at the bottom of the screen, e.g. "Not approved." */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);

  const dismiss = useCallback((id: number) => {
    setToasts((list) => list.filter((toast) => toast.id !== id));
  }, []);

  const show = useCallback(
    (message: string, options: ToastOptions = {}) => {
      const id = nextId++;
      setToasts((list) => [...list, { id, message, ...options }]);
      window.setTimeout(() => dismiss(id), visibleFor);
    },
    [dismiss],
  );

  return (
    <ToastContext.Provider value={show}>
      {children}
      <div className="nx-toasts" role="status" aria-live="polite">
        {toasts.map((toast) => (
          <div key={toast.id} className={`nx-toast nx-toast--${toast.tone ?? 'normal'}`}>
            <span className="nx-toast__text">{toast.message}</span>
            {toast.action && (
              <button
                type="button"
                className="nx-btn nx-btn--secondary"
                onClick={() => {
                  toast.action!.onClick();
                  dismiss(toast.id);
                }}
              >
                {toast.action.label}
              </button>
            )}
            <button
              type="button"
              className="nx-btn nx-btn--ghost-dark"
              aria-label={t('common.close')}
              onClick={() => dismiss(toast.id)}
            >
              ×
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}
