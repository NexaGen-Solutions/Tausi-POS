import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { t } from '../i18n/i18n';

/** Below this the server calls itself degraded (SystemEndpoints.LowDisk): 1 GB. */
export const lowDisk = 1024 ** 3;

/**
 * The server's disk is nearly full: an orange banner on the admin site, before writes and backups
 * start failing (a full disk answers 503 "disk_full" and saves nothing).
 */
export function DiskBanner() {
  const health = useQuery({
    queryKey: ['health'],
    queryFn: () => api<{ diskFreeBytes: number | null }>('/health'),
    refetchInterval: 10 * 60 * 1000,
  });
  const free = health.data?.diskFreeBytes;
  if (free == null || free >= lowDisk) return null;
  const shown =
    free >= 1024 ** 2 ? `${(free / 1024 ** 2).toFixed(0)} MB` : `${Math.round(free / 1024)} KB`;
  return (
    <div className="nx-banner nx-banner--warning" role="alert">
      <span className="nx-banner__text">{t('admin.diskLow', { free: shown })}</span>
    </div>
  );
}
