import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { usePinSession } from './usePinSession';

/** Screens after login: back to the PIN pad without a session; to change-PIN when one is due. */
export function RequireSession({ children }: { children: ReactNode }) {
  const session = usePinSession();
  const location = useLocation();
  if (!session) return <Navigate to="/pos" replace />;
  if (session.mustChangePin && location.pathname !== '/pos/change-pin')
    return <Navigate to="/pos/change-pin" replace />;
  return children;
}
