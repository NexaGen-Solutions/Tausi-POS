import { t } from '../../shared/i18n/i18n';
import { Button, Modal } from '../../shared/ui';

/** "2 items haven't been printed. Print now / Leave them unprinted / Stay". */
export function LeaveDialog({
  count,
  printLabel,
  busy,
  onPrint,
  onLeave,
  onStay,
}: {
  count: number;
  printLabel: string;
  busy: boolean;
  onPrint: () => void;
  onLeave: () => void;
  onStay: () => void;
}) {
  return (
    <Modal
      title={count === 1 ? t('pos.order.unprintedOne') : t('pos.order.unprinted', { count })}
      onClose={onStay}
    >
      <div className="order-leave">
        <Button variant="primary" size="lg" block disabled={busy} onClick={onPrint}>
          {printLabel}
        </Button>
        <Button size="lg" block disabled={busy} onClick={onLeave}>
          {t('pos.order.leaveUnprinted')}
        </Button>
        <Button size="lg" block onClick={onStay}>
          {t('pos.order.stay')}
        </Button>
      </div>
    </Modal>
  );
}
