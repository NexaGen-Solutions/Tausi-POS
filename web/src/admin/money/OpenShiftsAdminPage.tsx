import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router';
import { api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { CloseOthersShift } from '../../pos/cash/CloseOthersShift';
import { OpenShiftsList } from '../../pos/cash/OpenShifts';
import '../../pos/cash/cash.css';
import './money.css';

/** Cash book → Open shifts (1.0.2): close a shift its cashier left open, from the office. */
export function OpenShiftsAdminPage() {
  const me = useQuery({
    queryKey: ['me'],
    queryFn: () => api<{ user: { id: string } }>('/api/auth/me'),
  });
  return (
    <main className="money-page">
      <div className="money-page__head">
        <h1 className="admin__title">{t('admin.openShifts.title')}</h1>
        <Link className="nx-btn nx-btn--link nx-btn--md" to="/admin/cash-book">
          {t('admin.openShifts.back')}
        </Link>
      </div>
      <p className="cash-note">{t('admin.openShifts.help')}</p>
      <OpenShiftsList
        myId={me.data?.user.id ?? null}
        closeHref={(id) => `/admin/cash-book/open-shifts/${id}`}
      />
    </main>
  );
}

/** Closing one shift from the admin site: pending payments can only be removed here (no drawer of mine). */
export function CloseShiftAdminPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  return (
    <main className="money-page">
      <Link className="nx-btn nx-btn--link nx-btn--md" to="/admin/cash-book/open-shifts">
        {t('admin.openShifts.title')}
      </Link>
      <CloseOthersShift
        shiftId={id}
        canMove={false}
        onDone={() => navigate('/admin/cash-book/open-shifts')}
      />
    </main>
  );
}
