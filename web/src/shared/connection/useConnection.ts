import { useSyncExternalStore } from 'react';
import { connectionState, onConnectionChange } from './status';

export function useConnection() {
  return useSyncExternalStore(onConnectionChange, connectionState);
}
