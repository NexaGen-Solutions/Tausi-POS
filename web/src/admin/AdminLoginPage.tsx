import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api, apiUrl } from '../shared/api/client';
import type { Brand } from '../shared/api/types';
import { TausiMark } from '../shared/brand';
import { t } from '../shared/i18n/i18n';
import { Button, Field } from '../shared/ui';
import './admin.css';

/** Admin site sign-in: user name and password, for roles allowed to use the admin site. */
export function AdminLoginPage() {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });

  const signIn = async () => {
    setBusy(true);
    setError(null);
    try {
      await api('/api/admin/login', { method: 'POST', body: { userName, password } });
      await queries.invalidateQueries({ queryKey: ['me'] });
      navigate('/admin/menu', { replace: true });
    } catch (e) {
      setError(e instanceof ApiError && e.status !== 401 ? e.message : t('admin.loginFailed'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="admin__login">
      <form
        className="admin__login-panel"
        onSubmit={(event) => {
          event.preventDefault();
          void signIn();
        }}
      >
        {brand.data && (
          <div className="admin__login-business">
            {brand.data.logoFileId && (
              <img src={apiUrl(`/files/${brand.data.logoFileId}`)} alt={t('admin.logo')} />
            )}
            <p className="admin__business">{brand.data.name}</p>
          </div>
        )}
        <h1>{t('admin.loginTitle')}</h1>
        <Field
          label={t('admin.userName')}
          autoComplete="username"
          value={userName}
          onChange={(e) => setUserName(e.target.value)}
          autoFocus
        />
        <Field
          label={t('admin.password')}
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={error}
        />
        <Button type="submit" variant="primary" size="lg" disabled={busy || !userName || !password}>
          {t('admin.signIn')}
        </Button>
      </form>
      <p className="admin__login-tausi">
        <TausiMark size={24} label={null} />
        {t('common.appName')}
      </p>
    </main>
  );
}
