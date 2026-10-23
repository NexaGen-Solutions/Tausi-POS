import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, useToast } from '../../shared/ui';
import { CashCountForm } from './CashCountForm';
import { countTotal, currentShiftKey, type CashCount, type Shift } from './types';

/** "Start shift": the opening float, counted by note or left at the default. */
export function StartShift({
  defaultFloat,
  denominations,
  title,
  onStarted,
}: {
  defaultFloat: number;
  denominations: number[];
  title: string;
  onStarted?: (shift: Shift) => void;
}) {
  const queries = useQueryClient();
  const toast = useToast();
  const [count, setCount] = useState<CashCount>({ notes: {}, coins: 0 });
  const [busy, setBusy] = useState(false);
  const counted = countTotal(count);

  const start = async () => {
    setBusy(true);
    try {
      const shift = await api<Shift>('/api/pos/shifts', {
        method: 'POST',
        body: { float: counted > 0 ? count : { total: defaultFloat } },
      });
      await queries.invalidateQueries({ queryKey: currentShiftKey });
      onStarted?.(shift);
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="cash-start" aria-label={title}>
      <h2 className="cash-heading">{title}</h2>
      <p className="cash-note">
        {t('pos.cash.floatHelp', { amount: defaultFloat.toLocaleString('en-KE') })}
      </p>
      <CashCountForm
        denominations={denominations}
        value={count}
        onChange={setCount}
        totalLabel={t('pos.cash.openingFloat')}
      />
      <Button variant="primary" size="lg" block disabled={busy} onClick={() => void start()}>
        {t('pos.cash.startShift')}
      </Button>
    </section>
  );
}
