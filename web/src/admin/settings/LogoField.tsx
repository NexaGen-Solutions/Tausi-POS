import { useState } from 'react';
import { ApiError, api, apiUrl } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import type { Settings } from './types';

const maxBytes = 2 * 1024 * 1024;
const types = ['image/png', 'image/jpeg'];

/** The logo in colour (as screens show it) and in 1-bit (as receipts print it), with upload and remove. */
export function LogoField({
  settings,
  onSaved,
}: {
  settings: Settings;
  onSaved: (s: Settings) => void;
}) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<Settings>) => {
    setBusy(true);
    setError(null);
    try {
      onSaved(await action());
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const upload = (file: File | undefined) => {
    if (!file) return;
    if (!types.includes(file.type)) return setError(t('admin.settings.logoType'));
    if (file.size > maxBytes) return setError(t('admin.settings.logoSize'));
    const form = new FormData();
    form.append('file', file);
    void run(() => api<Settings>('/api/admin/settings/logo', { method: 'POST', body: form }));
  };

  return (
    <div className="nx-field settings-logo">
      <span className="nx-field__label">{t('admin.settings.logo')}</span>
      <div className="settings-logo__row">
        <div className="settings-logo__box">
          {settings.logoFileId ? (
            <img
              src={apiUrl(`/files/${settings.logoFileId}`)}
              alt={t('admin.settings.logoColour')}
            />
          ) : (
            <span>{t('admin.settings.noLogo')}</span>
          )}
        </div>
        {settings.logoPrintUrl && (
          <div className="settings-logo__box settings-logo__box--print">
            <img src={apiUrl(settings.logoPrintUrl)} alt={t('admin.settings.logoPrint')} />
          </div>
        )}
        <div className="settings-logo__actions">
          <label className={`nx-btn nx-btn--secondary nx-btn--md${busy ? ' settings-busy' : ''}`}>
            {t('admin.settings.uploadLogo')}
            <input
              type="file"
              accept="image/png,image/jpeg"
              className="visually-hidden"
              disabled={busy}
              onChange={(e) => {
                upload(e.target.files?.[0]);
                e.target.value = '';
              }}
            />
          </label>
          {settings.logoFileId && (
            <Button
              disabled={busy}
              onClick={() =>
                void run(() => api<Settings>('/api/admin/settings/logo', { method: 'DELETE' }))
              }
            >
              {t('admin.settings.removeLogo')}
            </Button>
          )}
          <span className="nx-field__hint">{t('admin.settings.logoHelp')}</span>
        </div>
      </div>
      {error && (
        <span className="nx-field__error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}
