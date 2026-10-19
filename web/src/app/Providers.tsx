import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { ApprovalProvider } from '../shared/approval/ApprovalProvider';
import { ConnectionWatcher } from '../shared/connection/ConnectionWatcher';
import { ToastProvider } from '../shared/ui';

export function Providers({ client, children }: { client: QueryClient; children: ReactNode }) {
  return (
    <QueryClientProvider client={client}>
      <ToastProvider>
        <ApprovalProvider>
          <ConnectionWatcher />
          {children}
        </ApprovalProvider>
      </ToastProvider>
    </QueryClientProvider>
  );
}
