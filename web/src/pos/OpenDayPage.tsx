import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Navigate, useNavigate } from 'react-router';
import { ApiError, api } from '../shared/api/client';
import type { DeviceInfo, Me } from '../shared/api/types';
import { OfflineBanner } from '../shared/connection/OfflineBanner';
import { t } from '../shared/i18n/i18n';
import { Button } from '../shared/ui';
import { PosTopBar } from './PosTopBar';
import { useLogout } from './useLogout';

/**
 * After login: straight on to the tables when the day is open. Otherwise a user who may open
 * the day can do so here; anyone else is told to ask a supervisor.
 */
export function OpenDayPage() {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const logout = useLogout();
  const [busy, setBusy] = useState(false);
  const device = useQuery({
    queryKey: ['device'],
    queryFn: () => api<DeviceInfo>('/api/device'),
    staleTime: 0,
  });
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/auth/me') });

  if (device.data?.workPeriod) return <Navigate to="/pos/tables" replace />;
  if (!device.data || !me.data) return <div className="pos-screen" />;

  const allowed = me.data.permissions['day.open_close'];
  const canOpen = allowed === 'Allowed' || allowed === 'NeedsApproval';

  const open = async () => {
    setBusy(true);
    try {
      await api('/api/day/open', { method: 'POST' });
      await queries.invalidateQueries({ queryKey: ['device'] });
      navigate('/pos/tables', { replace: true });
    } catch (error) {
      // Someone else opened it a moment ago: that is fine.
      if (error instanceof ApiError && error.status === 409)
        navigate('/pos/tables', { replace: true });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="pos-screen">
      <PosTopBar />
      <OfflineBanner />
      <main className="pos-center">
        {canOpen ? (
          <>
            <h1 className="pos-center__title">{t('pos.day.notOpen')}</h1>
            <p className="pos-center__text">{t('pos.day.help')}</p>
            <Button variant="primary" size="lg" disabled={busy} onClick={() => void open()}>
              {t('pos.day.openTheDay')}
            </Button>
          </>
        ) : (
          <>
            <h1 className="pos-center__title">{t('pos.day.askSupervisor')}</h1>
            <Button size="lg" onClick={() => void logout()}>
              {t('common.back')}
            </Button>
          </>
        )}
      </main>
    </div>
  );
}
