import { Navigate, type RouteObject } from 'react-router';
import { AdminLayout } from '../admin/AdminLayout';
import { AdminLoginPage } from '../admin/AdminLoginPage';
import { ComingSoonPage } from '../admin/ComingSoonPage';
import { adminNav } from '../admin/nav';
import { StaffPage } from '../admin/staff/StaffPage';
import { ChangePinPage } from '../pos/ChangePinPage';
import { OpenDayPage } from '../pos/OpenDayPage';
import { PinLoginPage } from '../pos/PinLoginPage';
import { PosLayout } from '../pos/PosLayout';
import { RequireSession } from '../pos/RequireSession';
import { SetupPage } from '../pos/SetupPage';
import { TablesPage } from '../pos/TablesPage';
import { t } from '../shared/i18n/i18n';
import { RootRedirect } from './RootRedirect';

export const routes: RouteObject[] = [
  { path: '/', element: <RootRedirect /> },
  { path: '/setup', element: <SetupPage /> },
  {
    path: '/pos',
    element: <PosLayout />,
    children: [
      { index: true, element: <PinLoginPage /> },
      {
        path: 'change-pin',
        element: (
          <RequireSession>
            <ChangePinPage />
          </RequireSession>
        ),
      },
      {
        path: 'day',
        element: (
          <RequireSession>
            <OpenDayPage />
          </RequireSession>
        ),
      },
      {
        path: 'tables',
        element: (
          <RequireSession>
            <TablesPage />
          </RequireSession>
        ),
      },
      { path: 'home', element: <Navigate to="/pos/tables" replace /> },
    ],
  },
  { path: '/admin/login', element: <AdminLoginPage /> },
  {
    path: '/admin',
    element: <AdminLayout />,
    children: [
      { index: true, element: <Navigate to="menu" replace /> },
      { path: 'staff', element: <StaffPage /> },
      ...adminNav
        .filter((item) => item.path !== 'staff')
        .map((item) => ({
          path: item.path,
          element: <ComingSoonPage title={t(item.label)} />,
        })),
    ],
  },
  { path: '*', element: <RootRedirect /> },
];
