import type { TicketDetail } from '../tables/types';

/** Every line voided or cancelled, nothing to pay and no payments: it can close without a receipt. */
export function isFullyVoided(ticket: TicketDetail | null | undefined): boolean {
  return (
    !!ticket &&
    ticket.status === 'Open' &&
    ticket.lines.length > 0 &&
    ticket.lines.every((l) => l.voided || l.status === 'Voided' || l.status === 'Cancelled') &&
    ticket.total === 0 &&
    (ticket.payments ?? []).length === 0
  );
}
