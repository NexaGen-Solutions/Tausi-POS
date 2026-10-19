import { useQuery } from '@tanstack/react-query';
import { useSyncExternalStore } from 'react';
import { Navigate, useNavigate } from 'react-router';
import { api } from '../shared/api/client';
import type { Brand } from '../shared/api/types';
import { LiveUpdates } from '../shared/connection/LiveUpdates';
import { OfflineBanner } from '../shared/connection/OfflineBanner';
import { t } from '../shared/i18n/i18n';
import { onPinSessionChange, pinSession, setPinSession } from '../shared/session/session';
import { Button, TopBar } from '../shared/ui';
import './pos.css';

/** Placeholder after a PIN login until the table map arrives. */
export function PosHomePage() {
  const navigate = useNavigate();
  const session = useSyncExternalStore(onPinSessionChange, pinSession);
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });

  if (!session) return <Navigate to="/pos" replace />;
  const { user } = session;
  const who = t('pos.whoWhere', {
    terminal: user.terminalName ?? '',
    name: user.name,
    role: user.role,
  });

  return (
    <div className="pos-screen">
      <TopBar
        title={brand.data?.name ?? ''}
        subtitle={who}
        end={
          <Button
            variant="ghost-dark"
            onClick={() => {
              setPinSession(null);
              navigate('/pos', { replace: true });
            }}
          >
            {t('common.logOut')}
          </Button>
        }
      />
      <OfflineBanner />
      <LiveUpdates />
      <main className="pos-home">
        <p className="pos-home__who">
          {t('pos.loggedInAs', { name: user.name, terminal: user.terminalName ?? '' })}
        </p>
        <p>{t('pos.placeholder')}</p>
      </main>
    </div>
  );
}
