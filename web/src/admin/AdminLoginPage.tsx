import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../shared/api/client';
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
    </main>
  );
}
