import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { api } from '../shared/api/client';
import type { Brand } from '../shared/api/types';
import { t } from '../shared/i18n/i18n';
import { Button, TopBar } from '../shared/ui';
import { ApprovalsButton } from './approvals/ApprovalsButton';
import { useLogout } from './useLogout';
import { usePinSession } from './usePinSession';

/** Business name, "<terminal> · <user> (<role>)", Approvals (for approvers), optional buttons (e.g. My sales) and Log out. */
export function PosTopBar({ extra }: { extra?: ReactNode } = {}) {
  const session = usePinSession();
  const logout = useLogout();
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const user = session?.user;

  return (
    <TopBar
      title={brand.data?.name ?? ''}
      subtitle={
        user &&
        t('pos.whoWhere', { terminal: user.terminalName ?? '', name: user.name, role: user.role })
      }
      end={
        <>
          <ApprovalsButton />
          {extra}
          <Button variant="ghost-dark" onClick={() => void logout()}>
            {t('common.logOut')}
          </Button>
        </>
      }
    />
  );
}
