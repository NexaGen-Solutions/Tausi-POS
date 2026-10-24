import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { ApiError, api, type ApprovalKind } from '../api/client';
import { usePosRules } from '../device/usePosRules';
import { t } from '../i18n/i18n';
import { Button, Keypad, PinDots } from '../ui';
import type { ApprovalIssued } from './ApprovalPad';
import type { Approver } from './types';
import './approval.css';

const maxPin = 6;

export interface ApprovalChoicesProps {
  permission: string;
  kind: ApprovalKind;
  /** "Send void request". */
  sendLabel: string;
  /** Option 1: the supervisor's PIN gave a token; do the action with it now. */
  onToken: (token: string) => Promise<void>;
  /** Option 2: send a request, to this person (or null: anyone who can approve). */
  onSend: (preferredApproverId: string | null) => Promise<void>;
  /** The form on the left isn't complete yet. */
  disabled?: boolean;
}

/**
 * Wireframe 5's right side, shared by every action that may need a supervisor: Option 1, the
 * supervisor types their PIN here (never the requester's own); Option 2, send a request to a
 * supervisor at their own counter, unless the business switched requests off.
 */
export function ApprovalChoices({
  permission,
  kind,
  sendLabel,
  onToken,
  onSend,
  disabled = false,
}: ApprovalChoicesProps) {
  const [pin, setPin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const requests = usePosRules().approvalsFromOwnCounter;
  const approvers = useQuery({
    queryKey: ['approvers', kind],
    queryFn: () => api<Approver[]>(`/api/pos/approvals/approvers?kind=${kind}`),
    staleTime: 0,
    enabled: requests,
  });
  // Nothing picked yet: the first approver; '' means anyone who can approve.
  const [to, setTo] = useState<string | undefined>();
  const chosen = to ?? approvers.data?.[0]?.userId ?? '';

  const approve = async () => {
    if (pin.length < 4 || busy || disabled) return;
    setBusy(true);
    setError(null);
    try {
      const issued = await api<ApprovalIssued>('/api/auth/approve', {
        method: 'POST',
        body: { pin, permissionKey: permission },
      });
      await onToken(issued.approvalToken);
    } catch (e) {
      setPin('');
      if (e instanceof ApiError && e.status === 429)
        setError(t('pos.locked', { seconds: e.problem.retryAfterSeconds ?? 60 }));
      else if (e instanceof ApiError && e.status === 401) setError(t('approval.wrongPin'));
      else if (e instanceof ApiError) setError(e.message);
      else setError(t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const send = async () => {
    if (busy || disabled) return;
    setBusy(true);
    setError(null);
    try {
      await onSend(chosen || null);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  // Digits typed on a keyboard go to the PIN (unless a field has focus); Enter approves.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target;
      if (
        target instanceof HTMLInputElement ||
        target instanceof HTMLTextAreaElement ||
        target instanceof HTMLSelectElement
      )
        return;
      if (/^\d$/.test(event.key)) setPin((p) => (p.length < maxPin ? p + event.key : p));
      else if (event.key === 'Backspace') setPin((p) => p.slice(0, -1));
      else if (event.key === 'Enter' && pin.length >= 4) {
        event.preventDefault();
        void approve();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  return (
    <div className="approval-choices">
      <section aria-labelledby="approval-option-1">
        <h3 id="approval-option-1" className="approval-choices__heading">
          {t('approval.option1')}
        </h3>
        <div className="approval-choices__pin">
          <strong>{t('approval.pinLabel')}</strong>
          <PinDots length={pin.length} size="small" />
        </div>
        <Keypad
          variant="compact"
          onDigit={(d) => setPin((p) => (p.length < maxPin ? p + d : p))}
          onClear={() => setPin('')}
          onEnter={() => void approve()}
          enterLabel={t('approval.approve')}
          enterDisabled={pin.length < 4 || busy || disabled}
        />
      </section>
      {error && (
        <p role="alert" className="approval-choices__error">
          {error}
        </p>
      )}
      {requests && (
        <section aria-labelledby="approval-option-2" className="approval-choices__send">
          <h3 id="approval-option-2" className="approval-choices__heading">
            {t('approval.option2')}
          </h3>
          <label className="approval-choices__to">
            <span>{t('approval.sendTo')}</span>
            <select value={chosen} onChange={(e) => setTo(e.target.value)} disabled={busy}>
              {(approvers.data ?? []).map((a) => (
                <option key={a.userId} value={a.userId}>
                  {t('approval.approverOption', {
                    name: a.name,
                    role: a.role,
                    terminal: a.terminal,
                  })}
                </option>
              ))}
              <option value="">{t('approval.anyone')}</option>
            </select>
          </label>
          <Button
            variant="dark"
            size="lg"
            block
            disabled={busy || disabled}
            onClick={() => void send()}
          >
            {sendLabel}
          </Button>
          <p className="approval-choices__note">{t('approval.sendNote')}</p>
        </section>
      )}
    </div>
  );
}
