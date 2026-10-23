import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { api } from '../../shared/api/client';
import type { Me } from '../../shared/api/types';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import { usePinSession } from '../usePinSession';

/** "My shift" for cashiers (ticket.settle) and "Close day" for those with day.open_close, next to Approvals. */
export function CashButtons() {
  const navigate = useNavigate();
  const session = usePinSession();
  const me = useQuery({
    queryKey: ['me'],
    queryFn: () => api<Me>('/api/auth/me'),
    enabled: !!session,
  });
  const allowed = (key: string) => me.data?.permissions[key] === 'Allowed';
  return (
    <>
      {allowed('ticket.settle') && (
        <Button variant="ghost-dark" onClick={() => navigate('/pos/shift')}>
          {t('pos.cash.myShift')}
        </Button>
      )}
      {allowed('day.open_close') && (
        <Button variant="ghost-dark" onClick={() => navigate('/pos/day')}>
          {t('pos.day.closeButton')}
        </Button>
      )}
    </>
  );
}
