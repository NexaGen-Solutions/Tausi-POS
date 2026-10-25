import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { t } from '../i18n/i18n';

export interface BackupHealth {
  state: 'ok' | 'failed' | 'overdue' | 'copyFailed' | 'none';
  lastSuccessAt: string | null;
  message: string | null;
}

/**
 * "Last night's backup failed", no backup for 48 hours, or the second copy failed: a red banner on
 * the admin site and the Close day screen until a backup works again.
 */
export function BackupBanner({ admin }: { admin: boolean }) {
  const status = useQuery({
    queryKey: ['backups', 'status'],
    queryFn: () => api<BackupHealth>('/api/backups/status'),
    refetchInterval: 10 * 60 * 1000,
  });
  const s = status.data;
  if (!s || !(s.state === 'failed' || s.state === 'overdue' || s.state === 'copyFailed'))
    return null;
  return (
    <div className="nx-banner nx-banner--warning backup-banner" role="alert">
      <span className="nx-banner__text">{s.message ?? t('admin.backups.problem')}</span>
      {admin && (
        <a className="nx-btn nx-btn--secondary nx-btn--md" href="/admin/settings/backups">
          {t('admin.backups.title')}
        </a>
      )}
    </div>
  );
}
