import { useState } from 'react';
import { t } from '../../shared/i18n/i18n';
import { Modal } from '../../shared/ui';
import { SambaImport } from '../../setup/steps/MenuStep';
import '../../setup/setup.css';

/** The setup wizard's SambaPOS import, from the Menu page: import again, or tidy an earlier import. */
export function SambaPosDialog({
  onClose,
  onApplied,
}: {
  onClose: () => void;
  onApplied: () => void;
}) {
  const [summary, setSummary] = useState<string | null>(null);
  return (
    <Modal title={t('admin.menu.importSambaPos')} onClose={onClose} width={980}>
      {summary && (
        <p className="wizard__ok" role="status">
          {summary}
        </p>
      )}
      <SambaImport
        onImported={(s) => {
          setSummary(s);
          onApplied();
        }}
      />
    </Modal>
  );
}
