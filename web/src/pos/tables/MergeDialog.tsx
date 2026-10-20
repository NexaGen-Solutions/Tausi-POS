import { useState } from 'react';
import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal } from '../../shared/ui';
import { oldest, tableLabel } from './tableState';
import type { TableTile } from './types';

/** Pick two or more tickets on one table; the oldest is kept. */
export function MergeDialog({
  table,
  currency,
  busy,
  onMerge,
  onClose,
}: {
  table: TableTile;
  currency: string;
  busy: boolean;
  onMerge: (intoId: string, otherIds: string[]) => void;
  onClose: () => void;
}) {
  const [picked, setPicked] = useState<string[]>(table.tickets.map((k) => k.id));
  const chosen = table.tickets.filter((k) => picked.includes(k.id));
  const kept = chosen.length >= 2 ? oldest(chosen) : null;
  const others = kept ? chosen.filter((k) => k.id !== kept.id) : [];

  return (
    <Modal
      title={t('pos.map.mergeTitle', { table: tableLabel(table.name) })}
      subtitle={t('pos.map.mergePick')}
      onClose={onClose}
    >
      <div className="map-merge">
        <ul className="map-merge__list">
          {table.tickets.map((ticket) => (
            <li key={ticket.id}>
              <label className="map-merge__check">
                <input
                  type="checkbox"
                  checked={picked.includes(ticket.id)}
                  onChange={(e) =>
                    setPicked((list) =>
                      e.target.checked
                        ? [...list, ticket.id]
                        : list.filter((id) => id !== ticket.id),
                    )
                  }
                />
                #{ticket.number} · {ticket.openedBy} · {formatMoney(ticket.total, currency)}
              </label>
            </li>
          ))}
        </ul>
        {kept && (
          <p className="map-merge__confirm">
            {t('pos.map.mergeConfirm', {
              others: others.map((k) => `#${k.number}`).join(', '),
              kept: kept.number,
            })}
          </p>
        )}
        <Button
          variant="primary"
          size="lg"
          block
          disabled={!kept || busy}
          onClick={() =>
            kept &&
            onMerge(
              kept.id,
              others.map((k) => k.id),
            )
          }
        >
          {t('pos.map.mergeButton')}
        </Button>
        <Button size="lg" block onClick={onClose}>
          {t('common.cancel')}
        </Button>
      </div>
    </Modal>
  );
}
