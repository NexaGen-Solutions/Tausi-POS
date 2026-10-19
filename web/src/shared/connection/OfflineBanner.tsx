import { t } from '../i18n/i18n';
import { Banner } from '../ui';
import { isOffline } from './status';
import { useConnection } from './useConnection';

/** Shown while the API or the live-update hub cannot be reached; clears by itself on reconnect. */
export function OfflineBanner() {
  const connection = useConnection();
  if (!isOffline(connection)) return null;
  return <Banner>{t('connection.offline')}</Banner>;
}
