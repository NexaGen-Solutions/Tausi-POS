import { createContext } from 'react';

export interface ToastOptions {
  tone?: 'normal' | 'warning';
  action?: { label: string; onClick: () => void };
}

export type ShowToast = (message: string, options?: ToastOptions) => void;

export const ToastContext = createContext<ShowToast>(() => {});
