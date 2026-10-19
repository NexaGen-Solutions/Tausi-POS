import { t } from '../../shared/i18n/i18n';
import { Button, Modal } from '../../shared/ui';

/** Shows a temporary PIN once. Closing it forgets the PIN; it can't be shown again. */
export function OneTimePin({
  name,
  pin,
  onDone,
}: {
  name: string;
  pin: string;
  onDone: () => void;
}) {
  return (
    <Modal title={t('admin.staff.pinOnceTitle')} width={460}>
      <div className="staff-pin-once">
        <p>{t('admin.staff.pinOnce', { name })}</p>
        <p className="staff-pin-once__pin" aria-label={t('admin.staff.pinOnceTitle')}>
          {pin}
        </p>
        <p className="staff-pin-once__note">{t('admin.staff.pinOnceNote')}</p>
        <Button variant="primary" size="lg" block onClick={onDone}>
          {t('admin.staff.done')}
        </Button>
      </div>
    </Modal>
  );
}
