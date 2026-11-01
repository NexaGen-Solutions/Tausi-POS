import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';
import { api } from '../shared/api/client';
import type { DeviceInfo } from '../shared/api/types';
import { deviceToken } from '../shared/device/device';
import { t } from '../shared/i18n/i18n';

/** The band's height; the screens below take it off theirs (--training-band). */
const bandHeight = '32px';

/**
 * Training mode (Settings → Training mode): a striped orange band across the top of every terminal
 * screen, so nobody mistakes practice for real sales. Its slips say TRAINING too (the server adds it).
 */
export function TrainingBand() {
  const device = useQuery({
    queryKey: ['device'],
    queryFn: () => api<DeviceInfo>('/api/device'),
    enabled: !!deviceToken(),
  });
  const training = device.data?.training === true;

  useEffect(() => {
    if (!training) return;
    document.documentElement.style.setProperty('--training-band', bandHeight);
    return () => {
      document.documentElement.style.removeProperty('--training-band');
    };
  }, [training]);

  if (!training) return null;
  return (
    <div className="training-band" role="status" style={{ height: bandHeight }}>
      {t('pos.training.band')}
    </div>
  );
}
