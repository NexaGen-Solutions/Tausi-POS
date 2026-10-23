import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, TopBar, useToast } from '../../shared/ui';
import { usePinSession } from '../usePinSession';
import { CashCountForm } from './CashCountForm';
import { StartShift } from './StartShift';
import { currentShiftKey, type CashCount, type CurrentShift, type Shift } from './types';
import '../settle/settle.css';
import './cash.css';

type Move = 'PayOut' | 'Drop';

/** Wireframe 9: the cashier's shift, from the float to the counted, signed-off cash-up. */
export function ShiftPage() {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const toast = useToast();
  const session = usePinSession();
  const current = useQuery({
    queryKey: currentShiftKey,
    queryFn: () => api<CurrentShift>('/api/pos/shifts/current'),
    staleTime: 0,
  });
  const shift = current.data?.shift ?? null;
  const [count, setCount] = useState<CashCount | null>(null);
  const [moving, setMoving] = useState<Move | null>(null);
  const [busy, setBusy] = useState(false);

  const fail = (error: unknown) =>
    toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
      tone: 'warning',
    });
  const save = (s: Shift) =>
    queries.setQueryData<CurrentShift>(currentShiftKey, (old) =>
      old ? { ...old, shift: s } : old,
    );

  const shown: CashCount = count ?? {
    notes: shift?.countNotes ?? {},
    coins: shift?.countCoins ?? 0,
  };
  const countLocked = !!shift && shift.countSubmitted && shift.blind;

  const submitCount = async () => {
    if (!shift) return;
    setBusy(true);
    try {
      save(
        await api<Shift>(`/api/pos/shifts/${shift.id}/count`, {
          method: 'POST',
          body: { count: shown },
        }),
      );
      setCount(null);
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  // The server answers "approval required (day.open_close)": the approval pad asks for a supervisor's PIN.
  const close = async () => {
    if (!shift) return;
    setBusy(true);
    try {
      await api<Shift>(`/api/pos/shifts/${shift.id}/close`, { method: 'POST' });
      toast(t('pos.cash.closed'));
      await queries.invalidateQueries({ queryKey: currentShiftKey });
      navigate('/pos/tables');
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const user = session?.user;
  const difference = shift?.difference ?? null;
  return (
    <div className="pos-screen cash-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate('/pos/tables')}>
            {t('pos.order.tables')}
          </Button>
        }
        title={shift ? t('pos.cash.title') : t('pos.cash.startTitle')}
        end={
          user && (
            <span className="order-who">
              {t('pos.whoWhere', {
                terminal: user.terminalName ?? '',
                name: user.name,
                role: user.role,
              })}
            </span>
          )
        }
      />
      <OfflineBanner />
      {current.data && !shift && (
        <main className="cash-body cash-body--start">
          <StartShift
            title={t('pos.cash.startShift')}
            defaultFloat={current.data.start.defaultFloat}
            denominations={current.data.start.denominations}
          />
        </main>
      )}
      {shift && (
        <main className="cash-body">
          <section className="cash-panel" aria-label={t('pos.cash.expectedTitle')}>
            <h2 className="cash-heading">{t('pos.cash.expectedTitle')}</h2>
            {shift.expected === null ? (
              <>
                <Row label={t('pos.cash.openingFloat')} value={formatAmount(shift.openingFloat)} />
                <p className="cash-note cash-blind">{t('pos.cash.shownAfterCount')}</p>
              </>
            ) : (
              <>
                <Row label={t('pos.cash.openingFloat')} value={formatAmount(shift.openingFloat)} />
                <Row
                  label={t('pos.cash.cashSettled')}
                  value={`+ ${formatAmount(shift.cashSettled)}`}
                />
                <Row label={t('pos.cash.payOuts')} value={`− ${formatAmount(shift.payOuts)}`} />
                <Row label={t('pos.cash.drops')} value={`− ${formatAmount(shift.drops)}`} />
                <div className="cash-row cash-row--total">
                  <span>{t('pos.cash.expected')}</span>
                  <span className="cash-amount">{formatAmount(shift.expected)}</span>
                </div>
              </>
            )}
            <h3 className="cash-subheading">{t('pos.settle.type.Mpesa')}</h3>
            <Row label={t('pos.cash.mpesaCount')} value={String(shift.mpesaCount)} />
            <Row label={t('pos.cash.mpesaTotal')} value={formatAmount(shift.mpesaTotal)} />
            <p className="cash-note">{t('pos.cash.checkTill')}</p>
            <Row
              label={t('pos.cash.cardTotal', { count: shift.cardCount })}
              value={formatAmount(shift.cardTotal)}
            />
            {shift.movements.length > 0 && (
              <ul className="cash-moves">
                {shift.movements.map((m) => (
                  <li key={m.id} className="cash-row cash-row--small">
                    <span>
                      {m.kind === 'PayOut'
                        ? `${t('pos.cash.payOut')} · ${m.category}`
                        : t('pos.cash.drop')}
                    </span>
                    <span className="cash-amount">− {formatAmount(m.amount)}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="cash-panel" aria-label={t('pos.cash.countTitle')}>
            <h2 className="cash-heading">{t('pos.cash.countTitle')}</h2>
            <CashCountForm
              denominations={shift.denominations}
              value={shown}
              onChange={setCount}
              disabled={countLocked || busy}
              totalLabel={t('pos.cash.counted')}
            />
            {difference !== null && (
              <div
                className={`cash-row cash-row--total${difference !== 0 ? ' cash-row--off' : ''}`}
                role="status"
              >
                <span>{t('pos.cash.difference')}</span>
                <span className="cash-amount">
                  {difference > 0 ? '+' : difference < 0 ? '−' : '±'}{' '}
                  {formatAmount(Math.abs(difference))}
                </span>
              </div>
            )}
            <Button
              size="lg"
              block
              disabled={busy || countLocked}
              onClick={() => void submitCount()}
            >
              {shift.countSubmitted && !shift.blind
                ? t('pos.cash.countAgain')
                : t('pos.cash.submitCount')}
            </Button>
          </section>

          <section className="cash-panel cash-panel--side" aria-label={t('pos.cash.waitersTitle')}>
            <h2 className="cash-heading">{t('pos.cash.waitersTitle')}</h2>
            <table className="cash-table">
              <thead>
                <tr>
                  <th scope="col">{t('pos.cash.waiter')}</th>
                  <th scope="col">{t('pos.cash.cash')}</th>
                  <th scope="col">{t('pos.cash.openBills')}</th>
                </tr>
              </thead>
              <tbody>
                {shift.waiters.map((w) => (
                  <tr key={w.userId}>
                    <td>{w.name}</td>
                    <td className="cash-amount">{formatAmount(w.cash)}</td>
                    <td className={`cash-amount${w.openBills > 0 ? ' cash-warn' : ''}`}>
                      {w.openBills}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="cash-note">{t('pos.cash.openBillsNote')}</p>
            <div className="cash-actions">
              <Button
                size="lg"
                disabled={busy || shift.countSubmitted}
                onClick={() => setMoving('PayOut')}
              >
                {t('pos.cash.payOut')}
              </Button>
              <Button
                size="lg"
                disabled={busy || shift.countSubmitted}
                onClick={() => setMoving('Drop')}
              >
                {t('pos.cash.drop')}
              </Button>
            </div>
            <Button
              variant="primary"
              size="lg"
              block
              disabled={busy || !shift.countSubmitted}
              onClick={() => void close()}
            >
              {t('pos.cash.closeShift')}
            </Button>
          </section>
        </main>
      )}
      {moving && shift && (
        <MoveDialog
          kind={moving}
          shift={shift}
          onDone={(s) => {
            setMoving(null);
            if (s) save(s);
          }}
          onFail={fail}
        />
      )}
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="cash-row">
      <span>{label}</span>
      <span className="cash-amount">{value}</span>
    </div>
  );
}

/** Pay out (amount, what for, a note) or cash drop (amount, an optional note). */
function MoveDialog({
  kind,
  shift,
  onDone,
  onFail,
}: {
  kind: Move;
  shift: Shift;
  onDone: (shift: Shift | null) => void;
  onFail: (error: unknown) => void;
}) {
  const [amount, setAmount] = useState('');
  const [category, setCategory] = useState(shift.categories[0]?.id ?? '');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const value = Number(amount);
  const ready = value > 0 && (kind === 'Drop' || (category !== '' && note.trim() !== ''));

  const save = async () => {
    setBusy(true);
    try {
      onDone(
        await api<Shift>(`/api/pos/shifts/${shift.id}/${kind === 'PayOut' ? 'pay-out' : 'drop'}`, {
          method: 'POST',
          body: {
            amount: value,
            categoryId: kind === 'PayOut' ? category : null,
            note: note.trim() || null,
          },
        }),
      );
    } catch (error) {
      onFail(error);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      title={kind === 'PayOut' ? t('pos.cash.payOut') : t('pos.cash.drop')}
      onClose={() => onDone(null)}
      width={480}
    >
      <div className="cash-dialog">
        <label className="settle-field">
          <span>{t('pos.settle.amount')}</span>
          <input
            className="settle-money-input"
            inputMode="decimal"
            value={amount}
            onChange={(e) => setAmount(e.target.value.replace(/[^\d.]/g, ''))}
          />
        </label>
        {kind === 'PayOut' && (
          <label className="settle-field">
            <span>{t('pos.cash.category')}</span>
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              {shift.categories.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label className="settle-field">
          <span>{kind === 'PayOut' ? t('pos.cash.noteRequired') : t('pos.cash.noteOptional')}</span>
          <input value={note} maxLength={200} onChange={(e) => setNote(e.target.value)} />
        </label>
        <div className="settle-confirm">
          <Button size="lg" onClick={() => onDone(null)}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="lg" disabled={!ready || busy} onClick={() => void save()}>
            {kind === 'PayOut' ? t('pos.cash.payOutSave') : t('pos.cash.dropSave')}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
