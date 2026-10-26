import { useQuery, useQueryClient } from '@tanstack/react-query';
import { NavLink, Navigate, Outlet, useNavigate } from 'react-router';
import { ApiError, api, apiUrl } from '../shared/api/client';
import type { Brand, Me } from '../shared/api/types';
import { LiveUpdates } from '../shared/connection/LiveUpdates';
import { BackupBanner } from '../shared/connection/BackupBanner';
import { LicenceBanner } from '../shared/connection/LicenceBanner';
import { ServerStateOverlay } from '../shared/connection/ServerStateOverlay';
import { OfflineBanner } from '../shared/connection/OfflineBanner';
import { t } from '../shared/i18n/i18n';
import { Button } from '../shared/ui';
import { adminNav } from './nav';
import './admin.css';

export function AdminLayout() {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/auth/me') });
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });

  if (me.error instanceof ApiError && me.error.status === 401)
    return <Navigate to="/admin/login" replace />;

  const signOut = async () => {
    await api('/api/admin/logout', { method: 'POST' });
    queries.removeQueries({ queryKey: ['me'] });
    navigate('/admin/login', { replace: true });
  };

  return (
    <div className="admin">
      <nav className="admin__sidebar" aria-label="Admin">
        <div className="admin__brand">
          <div className="admin__logo">
            {brand.data?.logoFileId ? (
              <img src={apiUrl(`/files/${brand.data.logoFileId}`)} alt={t('admin.logo')} />
            ) : (
              t('admin.logo')
            )}
          </div>
          <div>
            <p className="admin__business">{brand.data?.name}</p>
            <p className="admin__subtitle">{t('admin.subtitle')}</p>
          </div>
        </div>
        <ul className="admin__nav">
          {adminNav.map((item) => (
            <li key={item.path}>
              <NavLink className="admin__link" to={`/admin/${item.path}`}>
                {t(item.label)}
              </NavLink>
            </li>
          ))}
        </ul>
        {me.data && (
          <div className="admin__footer">
            <span>{t('admin.signedIn', { name: me.data.user.name })}</span>
            <Button onClick={() => void signOut()}>{t('common.signOut')}</Button>
          </div>
        )}
        <p className="admin__about">{t('admin.about', { version: __APP_VERSION__ })}</p>
      </nav>
      <div className="admin__main">
        <OfflineBanner />
        {me.data && <LicenceBanner admin />}
        {me.data && <BackupBanner admin />}
        <ServerStateOverlay />
        {me.data && <LiveUpdates />}
        <Outlet />
      </div>
    </div>
  );
}
