import { useState } from 'react';
import { ApiError, api } from '../shared/api/client';
import { t } from '../shared/i18n/i18n';
import { Button, Field } from '../shared/ui';
import './setup.css';

/**
 * A lost admin-site password (/setup/recover): the token `TausiServer reset-admin` printed on the
 * server, the admin's user name and a new password.
 */
export function RecoverPage() {
  const [token, setToken] = useState('');
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const recover = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await api<{ userName: string }>('/api/setup/recover', {
        method: 'POST',
        body: { token, userName: userName.trim() || null, password },
      });
      setDone(r.userName);
    } catch (e) {
      setError(
        e instanceof ApiError
          ? (e.problem.fieldErrors?.password ?? e.message)
          : t('common.somethingWrong'),
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="wizard wizard--plain">
      {done ? (
        <div className="wizard__token">
          <p className="wizard__brand wizard__brand--dark">{t('wizard.brand')}</p>
          <h1 className="wizard__title">{t('recover.done')}</h1>
          <p>{t('recover.doneText', { name: done })}</p>
          <a className="nx-btn nx-btn--primary nx-btn--lg" href="/admin">
            {t('wizard.complete.admin')}
          </a>
        </div>
      ) : (
        <form
          className="wizard__token"
          onSubmit={(e) => {
            e.preventDefault();
            void recover();
          }}
        >
          <p className="wizard__brand wizard__brand--dark">{t('wizard.brand')}</p>
          <h1 className="wizard__title">{t('recover.title')}</h1>
          <p>{t('recover.help')}</p>
          <Field
            label={t('recover.token')}
            className="wizard__token-field"
            value={token}
            autoComplete="off"
            maxLength={9}
            onChange={(e) => setToken(e.target.value.toUpperCase())}
          />
          <Field
            label={t('recover.userName')}
            hint={t('recover.userNameHint')}
            value={userName}
            autoComplete="off"
            onChange={(e) => setUserName(e.target.value)}
          />
          <Field
            label={t('recover.password')}
            hint={t('recover.passwordHint')}
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            error={error}
          />
          <Button type="submit" variant="primary" size="lg" disabled={busy || !token || !password}>
            {t('recover.submit')}
          </Button>
        </form>
      )}
    </main>
  );
}
