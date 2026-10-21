import { useEffect, useState, type ReactNode } from 'react';
import { setApprovalHandler, type ApprovalContext, type SentRequest } from '../api/client';
import { t } from '../i18n/i18n';
import { useToast } from '../ui';
import { ApprovalPad } from './ApprovalPad';

interface Pending {
  permission: string;
  context?: ApprovalContext;
  resolve: (answer: string | { requested: SentRequest } | null) => void;
}

/**
 * Connects the API client to the shared approval pad: when the server says an action needs
 * approval, the pad opens, and the request is retried with the supervisor's token. When the caller
 * said what to ask for, the pad can send a request to a supervisor elsewhere instead.
 */
export function ApprovalProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null);
  const toast = useToast();

  useEffect(() => {
    setApprovalHandler(
      (permission, context) =>
        new Promise((resolve) => setPending({ permission, context, resolve })),
    );
    return () => setApprovalHandler(null);
  }, []);

  return (
    <>
      {children}
      {pending && (
        <ApprovalPad
          permission={pending.permission}
          context={pending.context}
          onRequested={(request) => {
            toast(t('approval.requestSent', { what: request.summary }));
            pending.resolve({ requested: request });
            setPending(null);
          }}
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
