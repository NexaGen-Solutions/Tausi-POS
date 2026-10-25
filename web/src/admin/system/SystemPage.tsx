import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field } from '../../shared/ui';
import { size, when, type Health, type SystemInfo, type UpdateCheck } from './types';
import './system.css';

/**
 * Settings → System: the installed version and health (database, disk, last backup, licence), Check
 * for updates (NexaGen's feed, when the server has internet), Apply (with the day closed, or by typing
 * UPDATE), and an update package from a USB drive for bars without internet.
 */
export function SystemPage() {
  const health = useQuery({ queryKey: ['health'], queryFn: () => api<Health>('/health') });
  const info = useQuery({
    queryKey: ['system'],
    queryFn: () => api<SystemInfo>('/api/admin/system'),
  });
  const [check, setCheck] = useState<UpdateCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [confirm, setConfirm] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refusedOpenDay, setRefusedOpenDay] = useState(false);

  const h = health.data;
  const dayOpen = (check?.dayOpen ?? false) || refusedOpenDay;
  const forced = confirm.trim() === 'UPDATE';
  const cantInstall = info.data?.cantInstallReason ?? null;

  const runCheck = async () => {
    setChecking(true);
    setError(null);
    try {
      setCheck(await api<UpdateCheck>('/api/admin/system/updates'));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setChecking(false);
    }
  };

  const started = () => setMessage(t('admin.system.updateStarted'));

  const failed = (e: unknown) => {
    if (e instanceof ApiError && e.problem.code === 'update_day_open') setRefusedOpenDay(true);
    setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
  };

  const apply = async () => {
    setBusy(true);
    setError(null);
    try {
      await api('/api/admin/system/updates/apply', {
        method: 'POST',
        body: { force: forced, confirm: forced ? confirm.trim() : null },
      });
      started();
    } catch (e) {
      failed(e);
    } finally {
      setBusy(false);
    }
  };

  const upload = async () => {
    if (!file) return;
    setBusy(true);
    setError(null);
    const form = new FormData();
    form.append('file', file);
    const query = forced ? `?force=true&confirm=${encodeURIComponent(confirm.trim())}` : '';
    try {
      await api(`/api/admin/system/updates/upload${query}`, { method: 'POST', body: form });
      started();
    } catch (e) {
      failed(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="system">
      <h1 className="admin__title">{t('admin.system.title')}</h1>
      <section className="system__panel" aria-label={t('admin.system.health')}>
        <h2 className="system__heading">{t('admin.system.health')}</h2>
        {h && (
          <dl className="system__facts">
            <dt>{t('admin.system.version')}</dt>
            <dd className="system__mono">{h.version}</dd>
            <dt>{t('admin.system.server')}</dt>
            <dd>{h.server}</dd>
            <dt>{t('admin.system.database')}</dt>
            <dd className={h.database === 'ok' ? undefined : 'system__bad'}>
              {t(`admin.system.db.${h.database === 'ok' ? 'ok' : 'down'}`)}
            </dd>
            <dt>{t('admin.system.disk')}</dt>
            <dd className="system__mono">{size(h.diskFreeBytes)}</dd>
            <dt>{t('admin.system.lastBackup')}</dt>
            <dd className={h.backup === 'ok' || h.backup === 'none' ? undefined : 'system__bad'}>
              {when(h.lastBackupAt)}
              {h.backupMessage && ` · ${h.backupMessage}`}
            </dd>
            <dt>{t('admin.system.licence')}</dt>
            <dd>{h.licence ? t(`admin.licence.status.${h.licence}`) : '-'}</dd>
            <dt>{t('admin.system.minimumTerminal')}</dt>
            <dd className="system__mono">{h.minimumTerminalVersion}</dd>
          </dl>
        )}
      </section>

      <section className="system__panel" aria-label={t('admin.system.updates')}>
        <h2 className="system__heading">{t('admin.system.updates')}</h2>
        {cantInstall && <p className="system__note">{cantInstall}</p>}
        <div className="system__buttons">
          <Button onClick={() => void runCheck()} disabled={checking}>
            {t('admin.system.check')}
          </Button>
        </div>
        {check && (
          <div className="system__check" role="status">
            {check.error ? (
              <p>{check.error}</p>
            ) : check.available ? (
              <>
                <p>
                  {t('admin.system.available', {
                    latest: check.latest ?? '',
                    current: check.current,
                  })}
                </p>
                {check.notes && <p className="system__notes">{check.notes}</p>}
              </>
            ) : (
              <p>{t('admin.system.upToDate', { current: check.current })}</p>
            )}
          </div>
        )}
        {(check?.available || file) && dayOpen && (
          <Field
            label={t('admin.system.dayOpen')}
            hint={t('admin.system.typeUpdate')}
            value={confirm}
            autoComplete="off"
            onChange={(e) => setConfirm(e.target.value)}
          />
        )}
        {check?.available && (
          <div className="system__buttons">
            <Button
              variant="primary"
              disabled={busy || !!cantInstall || (dayOpen && !forced)}
              onClick={() => void apply()}
            >
              {t('admin.system.apply', { latest: check.latest ?? '' })}
            </Button>
          </div>
        )}
        <h3 className="system__subheading">{t('admin.system.usb')}</h3>
        <p className="system__note">{t('admin.system.usbHelp')}</p>
        <label className="nx-field">
          <span className="nx-field__label">{t('admin.system.package')}</span>
          <input
            className="nx-field__input"
            type="file"
            accept=".tausiupdate"
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
          />
        </label>
        <div className="system__buttons">
          <Button
            disabled={busy || !file || !!cantInstall || (dayOpen && !forced)}
            onClick={() => void upload()}
          >
            {t('admin.system.install')}
          </Button>
        </div>
        {message && <p role="status">{message}</p>}
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
      </section>
    </main>
  );
}
