import { useSyncExternalStore } from 'react';
import { onPinSessionChange, pinSession } from '../shared/session/session';

export function usePinSession() {
  return useSyncExternalStore(onPinSessionChange, pinSession);
}
