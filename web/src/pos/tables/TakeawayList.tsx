import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import { age } from './tableState';
import type { TicketSummary } from './types';

/** Open takeaway tickets: number, customer, waiter, total and age. */
export function TakeawayList({
  tickets,
  now,
  currency,
  canAdd,
  disabled,
  onOpen,
  onNew,
}: {
  tickets: TicketSummary[];
  now: number;
  currency: string;
  canAdd: boolean;
  disabled: boolean;
  onOpen: (ticket: TicketSummary) => void;
  onNew: () => void;
}) {
  return (
    <section className="map-takeaway" aria-label={t('pos.map.takeaway')}>
      {canAdd && (
        <Button variant="primary" size="lg" disabled={disabled} onClick={onNew}>
          {t('pos.map.newTakeaway')}
        </Button>
      )}
      {tickets.length === 0 ? (
        <p className="map-empty">{t('pos.map.noTakeaway')}</p>
      ) : (
        <ul className="map-takeaway__list">
          {tickets.map((ticket) => (
            <li key={ticket.id}>
              <button
                type="button"
                className="map-takeaway__row"
                disabled={disabled}
                onClick={() => onOpen(ticket)}
              >
                <span className="map-takeaway__number">#{ticket.number}</span>
                <span>{ticket.customerName ?? t('pos.map.takeawayName')}</span>
                <span>{ticket.openedBy}</span>
                <span className="map-takeaway__total">{formatMoney(ticket.total, currency)}</span>
                <span className="map-takeaway__age">{age(ticket.openedAt, now)}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
