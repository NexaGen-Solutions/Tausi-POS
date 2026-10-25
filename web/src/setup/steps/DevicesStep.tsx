import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import { DevicesPage } from '../../admin/devices/DevicesPage';
import { NextButton, type StepProps } from '../SetupWizard';
import type { ServerAddress } from '../types';

/**
 * Step 3, Terminals and printers: the server's address for the counter PCs (with copy), then the
 * Task 4 screen for printers, pairing and routing. The licence's terminal limit applies there.
 */
export function DevicesStep({ status, onNext }: StepProps) {
  const addresses = useQuery({
    queryKey: ['setup', 'addresses'],
    queryFn: () => api<ServerAddress[]>('/api/setup/addresses'),
  });
  const [copied, setCopied] = useState<string | null>(null);
  const copy = async (address: string) => {
    try {
      await navigator.clipboard.writeText(address);
      setCopied(address);
    } catch {
      setCopied(null);
    }
  };
  const done = async () => {
    await api('/api/setup/steps/devices/done', { method: 'POST', body: { summary: null } });
    onNext();
  };
  return (
    <div className="wizard__form">
      <section className="wizard__panel" aria-labelledby="server-address">
        <h3 id="server-address" className="wizard__subtitle">
          {t('wizard.devices.addressTitle')}
        </h3>
        <p>{t('wizard.devices.pairHelp')}</p>
        <ul className="wizard__addresses">
          {(addresses.data ?? []).map((a) => (
            <li key={a.address}>
              <span className="mono">{a.address}</span>{' '}
              <span className="wizard__note">({a.interface})</span>{' '}
              <Button variant="link" onClick={() => void copy(a.address)}>
                {copied === a.address ? t('wizard.devices.copied') : t('wizard.devices.copy')}
              </Button>
            </li>
          ))}
        </ul>
        {(addresses.data?.length ?? 0) > 1 && (
          <p className="wizard__note">{t('wizard.devices.several')}</p>
        )}
        {status.businessType && status.businessType !== 'Bar' && (
          <p className="wizard__note">{t('wizard.devices.kitchen')}</p>
        )}
      </section>
      <div className="wizard__embedded">
        <DevicesPage />
      </div>
      <NextButton next="menu" onClick={() => void done()} />
    </div>
  );
}
