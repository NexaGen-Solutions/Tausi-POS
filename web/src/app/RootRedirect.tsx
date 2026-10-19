import { Navigate } from 'react-router';
import { deviceToken } from '../shared/device/device';

/** A paired terminal opens the POS; any other browser opens the admin site. */
export function RootRedirect() {
  return <Navigate to={deviceToken() ? '/pos' : '/admin'} replace />;
}
