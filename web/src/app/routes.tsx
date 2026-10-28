import { Navigate, type RouteObject } from 'react-router';
import { AboutPage } from '../admin/AboutPage';
import { AdminLayout } from '../admin/AdminLayout';
import { AdminLoginPage } from '../admin/AdminLoginPage';
import { ComingSoonPage } from '../admin/ComingSoonPage';
import { adminNav } from '../admin/nav';
import { DevicesPage } from '../admin/devices/DevicesPage';
import { MenuPage } from '../admin/menu/MenuPage';
import { StaffPage } from '../admin/staff/StaffPage';
import { PermissionsPage } from '../admin/permissions/PermissionsPage';
import { SettingsPage } from '../admin/settings/SettingsPage';
import { StockPage } from '../admin/stock/StockPage';
import { DeliveriesPage } from '../admin/stock/DeliveriesPage';
import { CountsPage } from '../admin/stock/CountsPage';
import { CountPage } from '../admin/stock/CountPage';
import { CashBookPage } from '../admin/money/CashBookPage';
import { AccountsPage } from '../admin/money/AccountsPage';
import { ReportsPage } from '../admin/reports/ReportsPage';
import { TicketPage } from '../admin/reports/TicketPage';
import { LicencePage } from '../admin/settings/LicencePage';
import { BackupsPage } from '../admin/system/BackupsPage';
import { SystemPage } from '../admin/system/SystemPage';
import { RecoverPage } from '../setup/RecoverPage';
import { LocationsPage } from '../admin/stock/LocationsPanel';
import { ChangePinPage } from '../pos/ChangePinPage';
import { PinLoginPage } from '../pos/PinLoginPage';
import { PosLayout } from '../pos/PosLayout';
import { RequireSession } from '../pos/RequireSession';
import { SetupPage } from '../pos/SetupPage';
import { SplashPage } from '../pos/SplashPage';
import { SetupWizard } from '../setup/SetupWizard';
import { MySalesPage } from '../pos/MySalesPage';
import { TablesPage } from '../pos/tables/TablesPage';
import { OrderPage } from '../pos/order/OrderPage';
import { SettlePage } from '../pos/settle/SettlePage';
import { ShiftPage } from '../pos/cash/ShiftPage';
import { DayPage } from '../pos/cash/DayPage';
import { ApprovalsPage } from '../pos/approvals/ApprovalsPage';
import { TablesAdminPage } from '../admin/tables/TablesAdminPage';
import { t } from '../shared/i18n/i18n';
import { RootRedirect } from './RootRedirect';

export const routes: RouteObject[] = [
  { path: '/', element: <RootRedirect /> },
  // The terminal's own screen: the server address and pairing (it was /setup before the wizard).
  { path: '/connect', element: <SetupPage /> },
  { path: '/splash', element: <SplashPage /> },
  { path: '/about', element: <AboutPage standalone /> },
  { path: '/setup', element: <SetupWizard /> },
  { path: '/setup/recover', element: <RecoverPage /> },
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
      // The table map shows "The day isn't open yet" itself now.
      {
        path: 'day',
        element: (
          <RequireSession>
            <DayPage />
          </RequireSession>
        ),
      },
      {
        path: 'shift',
        element: (
          <RequireSession>
            <ShiftPage />
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
      {
        path: 'ticket/:id',
        element: (
          <RequireSession>
            <OrderPage />
          </RequireSession>
        ),
      },
      {
        path: 'approvals',
        element: (
          <RequireSession>
            <ApprovalsPage />
          </RequireSession>
        ),
      },
      {
        path: 'ticket/:id/settle',
        element: (
          <RequireSession>
            <SettlePage />
          </RequireSession>
        ),
      },
      {
        path: 'my-sales',
        element: (
          <RequireSession>
            <MySalesPage />
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
      { path: 'menu', element: <MenuPage /> },
      { path: 'about', element: <AboutPage /> },
      { path: 'terminals', element: <DevicesPage /> },
      { path: 'staff', element: <StaffPage /> },
      { path: 'tables', element: <TablesAdminPage /> },
      { path: 'permissions', element: <PermissionsPage /> },
      { path: 'settings', element: <SettingsPage /> },
      { path: 'stock', element: <StockPage /> },
      { path: 'deliveries', element: <DeliveriesPage /> },
      { path: 'stock-counts', element: <CountsPage /> },
      { path: 'stock-counts/:id', element: <CountPage /> },
      { path: 'cash-book', element: <CashBookPage /> },
      { path: 'cashbook', element: <Navigate to="/admin/cash-book" replace /> },
      { path: 'accounts', element: <AccountsPage /> },
      { path: 'reports', element: <ReportsPage /> },
      { path: 'tickets/:id', element: <TicketPage /> },
      { path: 'settings/licence', element: <LicencePage /> },
      { path: 'settings/system', element: <SystemPage /> },
      { path: 'settings/backups', element: <BackupsPage /> },
      { path: 'stock/locations', element: <LocationsPage /> },
      ...adminNav
        .filter(
          (item) =>
            ![
              'staff',
              'menu',
              'terminals',
              'tables',
              'permissions',
              'settings',
              'stock',
              'deliveries',
              'stock-counts',
              'cash-book',
              'reports',
            ].includes(item.path),
        )
        .map((item) => ({
          path: item.path,
          element: <ComingSoonPage title={t(item.label)} />,
        })),
    ],
  },
  { path: '*', element: <RootRedirect /> },
];
