import { useQuery } from '@tanstack/react-query';
import { api } from '../shared/api/client';
import type { Brand } from '../shared/api/types';
import { t } from '../shared/i18n/i18n';
import { Button, TopBar } from '../shared/ui';
import { useLogout } from './useLogout';
import { usePinSession } from './usePinSession';

/** Business name, "<terminal> · <user> (<role>)" and Log out. */
export function PosTopBar() {
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
        <Button variant="ghost-dark" onClick={() => void logout()}>
          {t('common.logOut')}
        </Button>
      }
    />
  );
}
