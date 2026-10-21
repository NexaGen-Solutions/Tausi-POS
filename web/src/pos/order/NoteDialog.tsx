import { useState } from 'react';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal } from '../../shared/ui';

/** The ticket note, printed on the dispense slip. */
export function NoteDialog({
  note,
  onSave,
  onClose,
}: {
  note: string;
  onSave: (note: string) => void;
  onClose: () => void;
}) {
  const [text, setText] = useState(note);
  return (
    <Modal title={t('pos.order.ticketNote')} onClose={onClose}>
      <form
        className="order-leave"
        onSubmit={(e) => {
          e.preventDefault();
          onSave(text.trim());
        }}
      >
        <Field
          label={t('pos.order.noteLabel')}
          value={text}
          maxLength={1000}
          autoFocus
          onChange={(e) => setText(e.target.value)}
        />
        <Button type="submit" variant="primary" size="lg" block>
          {t('common.save')}
        </Button>
        <Button size="lg" block onClick={onClose}>
          {t('common.cancel')}
        </Button>
      </form>
    </Modal>
  );
}
