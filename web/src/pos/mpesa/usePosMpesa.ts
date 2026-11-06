import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { api } from '../../shared/api/client';
import { onPosEvent } from '../../shared/connection/hub';
import { posMpesaKey, type PosMpesa } from './types';

/** What M-Pesa can do on this terminal, kept fresh as payments arrive and settings change. */
export function usePosMpesa(): PosMpesa | undefined {
  const queries = useQueryClient();
  const query = useQuery({ queryKey: posMpesaKey, queryFn: () => api<PosMpesa>('/api/pos/mpesa') });
  useEffect(() => {
    const refresh = () => void queries.invalidateQueries({ queryKey: posMpesaKey });
    const offReceived = onPosEvent('mpesa.received', refresh);
    const offSettings = onPosEvent('settings.changed', refresh);
    return () => {
      offReceived();
      offSettings();
    };
  }, [queries]);
  return query.data;
}
