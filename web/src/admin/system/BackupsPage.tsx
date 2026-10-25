import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api, apiFile } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, Toggle } from '../../shared/ui';
import { BackupBanner } from '../../shared/connection/BackupBanner';
import { size, when, type BackupContents, type BackupRecord, type BackupSettings } from './types';
import './system.css';

/**
 * Settings → Backups: when the nightly backup runs, the second copy (USB drive, network share or
 * synced folder), encryption, Back up now, and the list with Download and Restore.
 */
export function BackupsPage() {
  const queries = useQueryClient();
  const list = useQuery({
    queryKey: ['backups'],
    queryFn: () => api<BackupRecord[]>('/api/admin/backups'),
  });
  const settings = useQuery({
    queryKey: ['backups', 'settings'],
    queryFn: () => api<BackupSettings>('/api/admin/backups/settings'),
  });
  const [restoring, setRestoring] = useState<BackupRecord | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const refresh = () => queries.invalidateQueries({ queryKey: ['backups'] });

  const backUpNow = async () => {
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      const made = await api<BackupRecord>('/api/admin/backups', { method: 'POST' });
      setDone(
        made.copyError
          ? t('admin.backups.madeCopyFailed', { error: made.copyError })
          : t('admin.backups.made', { size: size(made.size) }),
      );
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
      await refresh();
    }
  };

  const download = async (b: BackupRecord) => {
    try {
      const file = await apiFile(`/api/admin/backups/${encodeURIComponent(b.name)}/download`);
      const link = document.createElement('a');
      link.href = URL.createObjectURL(file.blob);
      link.download = b.name;
      link.click();
      URL.revokeObjectURL(link.href);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };

  return (
    <main className="system">
      <h1 className="admin__title">{t('admin.backups.title')}</h1>
      <BackupBanner admin={false} />
      {settings.data && <SettingsForm settings={settings.data} onSaved={() => void refresh()} />}

      <section className="system__panel" aria-label={t('admin.backups.list')}>
        <div className="system__row">
          <h2 className="system__heading">{t('admin.backups.list')}</h2>
          <Button variant="primary" disabled={busy} onClick={() => void backUpNow()}>
            {busy ? t('admin.backups.working') : t('admin.backups.now')}
          </Button>
        </div>
        {done && <p role="status">{done}</p>}
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
        <div className="system__table-box">
          <table className="system__table" aria-label={t('admin.backups.list')}>
            <thead>
              <tr>
                <th>{t('admin.backups.date')}</th>
                <th>{t('admin.backups.kind')}</th>
                <th className="system__num">{t('admin.backups.size')}</th>
                <th>{t('admin.backups.verified')}</th>
                <th>{t('admin.backups.copy')}</th>
                <th>
                  <span className="visually-hidden">{t('admin.backups.actions')}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {(list.data ?? []).map((b) => (
                <tr key={b.name}>
                  <td>
                    {when(b.createdAt)}
                    {b.encrypted && (
                      <span className="system__tag">{t('admin.backups.encrypted')}</span>
                    )}
                  </td>
                  <td>{t(`admin.backups.reason.${b.reason}`)}</td>
                  <td className="system__num system__mono">{size(b.size)}</td>
                  <td>{b.verified ? t('admin.backups.yes') : t('admin.backups.no')}</td>
                  <td className={b.copyError ? 'system__bad' : undefined}>
                    {b.copyError ? t('admin.backups.copyFailed') : (b.copiedTo ?? '-')}
                  </td>
                  <td className="system__actions">
                    <Button variant="link" onClick={() => void download(b)}>
                      {t('admin.backups.download')}
                    </Button>
                    <Button variant="link" onClick={() => setRestoring(b)}>
                      {t('admin.backups.restore')}
                    </Button>
                  </td>
                </tr>
              ))}
              {list.data?.length === 0 && (
                <tr>
                  <td colSpan={6}>{t('admin.backups.none')}</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>
      {restoring && <RestoreDialog backup={restoring} onClose={() => setRestoring(null)} />}
    </main>
  );
}

function SettingsForm({ settings, onSaved }: { settings: BackupSettings; onSaved: () => void }) {
  const [time, setTime] = useState(settings.time);
  const [copyFolder, setCopyFolder] = useState(settings.copyFolder ?? '');
  const [encrypt, setEncrypt] = useState(settings.encrypt);
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  /** Any edit hides "Saved." again. */
  const edited =
    <T,>(set: (value: T) => void) =>
    (value: T) => {
      setSaved(false);
      set(value);
    };

  const save = async () => {
    setBusy(true);
    setErrors({});
    try {
      await api('/api/admin/backups/settings', {
        method: 'PUT',
        body: { time, copyFolder: copyFolder.trim() || null, encrypt, password: password || null },
      });
      setPassword('');
      setSaved(true);
      onSaved();
    } catch (e) {
      setErrors(
        e instanceof ApiError
          ? (e.problem.fieldErrors ?? { form: e.message })
          : { form: t('common.somethingWrong') },
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="system__panel" aria-label={t('admin.backups.settings')}>
      <h2 className="system__heading">{t('admin.backups.settings')}</h2>
      <div className="system__grid">
        <Field
          label={t('admin.backups.time')}
          hint={t('admin.backups.timeHint')}
          type="time"
          value={time}
          onChange={(e) => edited(setTime)(e.target.value)}
          error={errors.time}
        />
        <Field
          label={t('admin.backups.copyFolder')}
          hint={t('admin.backups.copyFolderHint')}
          value={copyFolder}
          onChange={(e) => edited(setCopyFolder)(e.target.value)}
          error={errors.copyFolder}
        />
      </div>
      <Toggle label={t('admin.backups.encrypt')} checked={encrypt} onChange={edited(setEncrypt)} />
      {encrypt && (
        <>
          <p className="system__warn" role="note">
            {t('admin.backups.encryptWarning')}
          </p>
          <Field
            label={
              settings.hasPassword ? t('admin.backups.newPassword') : t('admin.backups.password')
            }
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => edited(setPassword)(e.target.value)}
            error={errors.password}
          />
        </>
      )}
      <div className="system__buttons">
        <Button variant="primary" disabled={busy} onClick={() => void save()}>
          {t('admin.backups.save')}
        </Button>
      </div>
      {saved && <p role="status">{t('admin.backups.saved')}</p>}
      {errors.form && (
        <p className="nx-field__error" role="alert">
          {errors.form}
        </p>
      )}
    </section>
  );
}

/**
 * Restore: reads what the backup holds first (asks for the password of an encrypted one), warns that
 * everything after it is lost, and for another business asks for its name to be typed.
 */
function RestoreDialog({ backup, onClose }: { backup: BackupRecord; onClose: () => void }) {
  const [password, setPassword] = useState('');
  const [opened, setOpened] = useState<BackupContents | null>(null);
  const [confirmBusiness, setConfirmBusiness] = useState('');
  const [needsName, setNeedsName] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const base = `/api/admin/backups/${encodeURIComponent(backup.name)}`;
  // An unencrypted backup is read at once; an encrypted one when its password is given.
  const plain = useQuery({
    queryKey: ['backups', 'inspect', backup.name],
    queryFn: () =>
      api<BackupContents>(`${base}/inspect`, {
        method: 'POST',
        readOnly: true,
        body: { password: null },
      }),
    enabled: !backup.encrypted,
    retry: false,
  });
  const contents = opened ?? plain.data ?? null;
  const shownError = error ?? (plain.error instanceof ApiError ? plain.error.message : null);

  const inspect = async () => {
    setBusy(true);
    setError(null);
    try {
      setOpened(
        await api<BackupContents>(`${base}/inspect`, {
          method: 'POST',
          readOnly: true,
          body: { password: password || null },
        }),
      );
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const restore = async () => {
    setBusy(true);
    setError(null);
    try {
      await api(`${base}/restore`, {
        method: 'POST',
        readOnly: true,
        body: { password: password || null, confirmBusiness: confirmBusiness || null },
      });
      location.assign('/admin/settings/backups');
    } catch (e) {
      if (e instanceof ApiError && e.problem.code === 'backup_other_business') setNeedsName(true);
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
      setBusy(false);
    }
  };

  return (
    <Modal title={t('admin.backups.restoreTitle')} onClose={busy ? undefined : onClose} width={560}>
      <div className="restore">
        {!contents && backup.encrypted && (
          <Field
            label={t('admin.backups.password')}
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        )}
        {contents && (
          <dl className="system__facts">
            <dt>{t('admin.backups.business')}</dt>
            <dd>{contents.business}</dd>
            <dt>{t('admin.backups.date')}</dt>
            <dd>{when(contents.createdAt)}</dd>
            <dt>{t('admin.system.version')}</dt>
            <dd className="system__mono">{contents.version}</dd>
          </dl>
        )}
        {contents && <p className="system__warn">{t('admin.backups.restoreWarning')}</p>}
        {needsName && contents && (
          <Field
            label={t('admin.backups.typeBusiness', { name: contents.business })}
            value={confirmBusiness}
            autoComplete="off"
            onChange={(e) => setConfirmBusiness(e.target.value)}
          />
        )}
        {busy && contents && <p role="status">{t('admin.backups.restoring')}</p>}
        {shownError && (
          <p className="nx-field__error" role="alert">
            {shownError}
          </p>
        )}
        <div className="system__buttons">
          <Button onClick={onClose} disabled={busy}>
            {t('common.cancel')}
          </Button>
          {!contents ? (
            <Button
              variant="primary"
              disabled={busy || (backup.encrypted && !password)}
              onClick={() => void inspect()}
            >
              {t('admin.backups.open')}
            </Button>
          ) : (
            <Button
              variant="primary"
              disabled={busy || (needsName && !confirmBusiness)}
              onClick={() => void restore()}
            >
              {t('admin.backups.restoreNow')}
            </Button>
          )}
        </div>
      </div>
    </Modal>
  );
}
