import { useEffect, useState, type ReactNode } from 'react';
import { setApprovalHandler } from '../api/client';
import { t } from '../i18n/i18n';
import { useToast } from '../ui';
import { ApprovalPad } from './ApprovalPad';

interface Pending {
  permission: string;
  resolve: (token: string | null) => void;
}

/**
 * Connects the API client to the shared approval pad: when the server says an action needs
 * approval, the pad opens, and the request is retried with the supervisor's token.
 */
export function ApprovalProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null);
  const toast = useToast();

  useEffect(() => {
    setApprovalHandler(
      (permission) => new Promise((resolve) => setPending({ permission, resolve })),
    );
    return () => setApprovalHandler(null);
  }, []);

  return (
    <>
      {children}
      {pending && (
        <ApprovalPad
          permission={pending.permission}
          onDone={(token, refusedBy) => {
            if (!token && refusedBy)
              toast(`${t('approval.denied')} ${refusedBy}`, { tone: 'warning' });
            pending.resolve(token);
            setPending(null);
          }}
        />
      )}
    </>
  );
}
