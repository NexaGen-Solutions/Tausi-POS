import { useEffect, useState } from 'react';
import { ApiError, api } from '../api/client';
import { permissionLabel, t } from '../i18n/i18n';
import { Button, Keypad, Modal, PinDots } from '../ui';

export interface ApprovalIssued {
  approvalToken: string;
  approvedByName: string;
}

export interface ApprovalPadProps {
  permission: string;
  /** The approval token, or null when cancelled or refused. */
  onDone: (token: string | null, refusedBy?: string) => void;
}

const maxPin = 6;

/** A supervisor types their PIN to approve one action (single-use, valid for 60 seconds). */
export function ApprovalPad({ permission, onDone }: ApprovalPadProps) {
  const [pin, setPin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const approve = async () => {
    if (pin.length < 4 || busy) return;
    setBusy(true);
    setError(null);
    try {
      const issued = await api<ApprovalIssued>('/api/auth/approve', {
        method: 'POST',
        body: { pin, permissionKey: permission },
      });
      onDone(issued.approvalToken);
    } catch (e) {
      setPin('');
      if (e instanceof ApiError && e.status === 403) onDone(null, e.problem.detail);
      else if (e instanceof ApiError && e.status === 429)
        setError(t('pos.locked', { seconds: e.problem.retryAfterSeconds ?? 60 }));
      else if (e instanceof ApiError && e.status === 401) setError(t('approval.wrongPin'));
      else setError(t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (/^\d$/.test(event.key)) setPin((p) => (p.length < maxPin ? p + event.key : p));
      else if (event.key === 'Backspace') setPin((p) => p.slice(0, -1));
      else if (event.key === 'Enter') void approve();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  return (
    <Modal
      title={t('approval.title')}
      subtitle={t('approval.subtitle', { action: permissionLabel(permission) })}
      onClose={() => onDone(null)}
      width={420}
    >
      <div style={{ display: 'grid', gap: 'var(--space-4)' }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          <strong>{t('approval.pinLabel')}</strong>
          <PinDots length={pin.length} size="small" />
        </div>
        <Keypad
          variant="compact"
          onDigit={(d) => setPin((p) => (p.length < maxPin ? p + d : p))}
          onClear={() => setPin('')}
          onEnter={() => void approve()}
          enterLabel={t('approval.approve')}
          enterDisabled={pin.length < 4 || busy}
        />
        {error && (
          <p role="alert" style={{ margin: 0, color: 'var(--color-warning-text)' }}>
            {error}
          </p>
        )}
        <Button block onClick={() => onDone(null)}>
          {t('common.cancel')}
        </Button>
      </div>
    </Modal>
  );
}
