import { Navigate, type RouteObject } from 'react-router';
import { AdminLayout } from '../admin/AdminLayout';
import { AdminLoginPage } from '../admin/AdminLoginPage';
import { ComingSoonPage } from '../admin/ComingSoonPage';
import { adminNav } from '../admin/nav';
import { PinLoginPage } from '../pos/PinLoginPage';
import { PosHomePage } from '../pos/PosHomePage';
import { SetupPage } from '../pos/SetupPage';
import { t } from '../shared/i18n/i18n';
import { RootRedirect } from './RootRedirect';

export const routes: RouteObject[] = [
  { path: '/', element: <RootRedirect /> },
  { path: '/setup', element: <SetupPage /> },
  { path: '/pos', element: <PinLoginPage /> },
  { path: '/pos/home', element: <PosHomePage /> },
  { path: '/admin/login', element: <AdminLoginPage /> },
  {
    path: '/admin',
    element: <AdminLayout />,
    children: [
      { index: true, element: <Navigate to="menu" replace /> },
      ...adminNav.map((item) => ({
        path: item.path,
        element: <ComingSoonPage title={t(item.label)} />,
      })),
    ],
  },
  { path: '*', element: <RootRedirect /> },
];
