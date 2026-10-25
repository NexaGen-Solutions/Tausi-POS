import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { Me } from '../api/types';
import { t } from '../i18n/i18n';
import type { LicenceStatus } from '../../setup/types';

/**
 * The trial countdown and licence warnings, for people who can enter a key (settings.manage):
 * "Trial: 23 days left · Enter licence key". Stronger after the expiry. Selling never stops.
 */
export function LicenceBanner({ admin }: { admin: boolean }) {
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/auth/me') });
  const allowed = me.data?.permissions['settings.manage'] === 'Allowed';
  const licence = useQuery({
    queryKey: ['licence'],
    queryFn: () => api<LicenceStatus>('/api/licence'),
    enabled: allowed,
  });
  const banner = licence.data?.banner;
  if (!allowed || !banner) return null;
  const strong = licence.data!.status === 'Grace' || licence.data!.status === 'Expired';
  return (
    <div
      className={`nx-banner nx-banner--${strong ? 'warning' : 'info'} licence-banner`}
      role={strong ? 'alert' : 'status'}
    >
      <span className="nx-banner__text">{banner}</span>
      {admin && (
        <a className="nx-btn nx-btn--secondary nx-btn--md" href="/admin/settings/licence">
          {t('admin.licence.enterKey')}
        </a>
      )}
    </div>
  );
}
