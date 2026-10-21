import { useState } from 'react';
import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal } from '../../shared/ui';
import { choiceComplete, defaultChoice, priced, type PosGroup, type PosItem } from './menu';

function choiceText(g: PosGroup) {
  return g.minSelect === g.maxSelect
    ? t('pos.order.chooseN', { count: g.minSelect })
    : g.minSelect === 0
      ? t('pos.order.chooseUpTo', { count: g.maxSelect })
      : t('pos.order.chooseRange', { min: g.minSelect, max: g.maxSelect });
}

/** Wireframe 4: required choices first (e.g. Temperature), optional ones under them, then quantity. */
export function OptionsDialog({
  item,
  currency,
  onAdd,
  onCancel,
}: {
  item: PosItem;
  currency: string;
  onAdd: (optionIds: string[], quantity: number) => void;
  onCancel: () => void;
}) {
  const [chosen, setChosen] = useState<string[]>(() => defaultChoice(item));
  const [quantity, setQuantity] = useState(1);
  const groups = [...item.groups].sort((a, b) => Number(b.minSelect > 0) - Number(a.minSelect > 0));
  const price = priced(item, chosen);
  const complete = choiceComplete(item, chosen);

  const toggle = (group: PosGroup, optionId: string) =>
    setChosen((list) => {
      const inGroup = group.options.map((o) => o.id);
      if (list.includes(optionId)) return list.filter((id) => id !== optionId);
      // A single choice swaps; a multiple choice adds while there is room.
      if (group.maxSelect === 1) return [...list.filter((id) => !inGroup.includes(id)), optionId];
      return list.filter((id) => inGroup.includes(id)).length < group.maxSelect
        ? [...list, optionId]
        : list;
    });

  const label = t('pos.order.addButton', {
    quantity,
    name: item.shortName || item.name,
    options: price.options ? ` (${price.options})` : '',
    amount: formatMoney(price.unitPrice * quantity, currency),
  });

  return (
    <Modal title={item.name} onClose={onCancel} width={640}>
      <div className="order-options">
        {groups.map((group) => (
          <fieldset key={group.id} className="order-options__group">
            <legend className="order-options__legend">
              {group.name} · {choiceText(group)}{' '}
              {group.minSelect > 0 ? t('pos.order.required') : t('pos.order.optional')}
            </legend>
            <div className="order-options__choices">
              {group.options.map((option) => (
                <button
                  key={option.id}
                  type="button"
                  className={`order-options__choice${chosen.includes(option.id) ? ' order-options__choice--on' : ''}`}
                  aria-pressed={chosen.includes(option.id)}
                  onClick={() => toggle(group, option.id)}
                >
                  {option.name}
                  {option.priceAddOn !== 0 && (
                    <span className="order-options__addon">
                      {' '}
                      +{formatMoney(option.priceAddOn, currency)}
                    </span>
                  )}
                </button>
              ))}
            </div>
          </fieldset>
        ))}
        <div className="order-options__quantity" role="group" aria-label={t('pos.order.quantity')}>
          <span>{t('pos.order.quantity')}</span>
          <Button
            size="lg"
            aria-label={t('pos.order.less')}
            disabled={quantity <= 1}
            onClick={() => setQuantity((q) => q - 1)}
          >
            −
          </Button>
          <output className="order-options__count" aria-live="polite">
            {quantity}
          </output>
          <Button
            size="lg"
            aria-label={t('pos.order.more')}
            disabled={quantity >= 99}
            onClick={() => setQuantity((q) => q + 1)}
          >
            +
          </Button>
        </div>
        <div className="order-options__actions">
          <Button size="lg" onClick={onCancel}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="primary"
            size="lg"
            disabled={!complete}
            onClick={() => onAdd(chosen, quantity)}
          >
            {label}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
