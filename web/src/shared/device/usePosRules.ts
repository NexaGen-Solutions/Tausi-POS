import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { defaultPosRules, type DeviceInfo, type PosRules } from '../api/types';

/** The business's rules for this terminal's screens; reloaded when settings change. */
export function usePosRules(): PosRules {
  const device = useQuery({ queryKey: ['device'], queryFn: () => api<DeviceInfo>('/api/device') });
  return device.data?.rules ?? defaultPosRules;
}
