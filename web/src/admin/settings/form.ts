import type { SettingEntry } from './types';

/** What a form control holds: a switch is true/false, everything else is the text typed. */
export type Edit = string | boolean;

/** A saved value as the form shows it. */
export function toEdit(entry: SettingEntry, value: unknown): Edit {
  if (entry.type === 'Bool') return value === true;
  if (entry.type === 'List') return Array.isArray(value) ? value.join(', ') : '';
  return value === null || value === undefined ? '' : String(value);
}

/**
 * The value to send for what was typed. Text that isn't a number goes as it is, so the server
 * answers with the field's own message.
 */
export function fromEdit(entry: SettingEntry, edit: Edit): unknown {
  if (typeof edit === 'boolean') return edit;
  const text = edit.trim();
  if (entry.type === 'Int' || entry.type === 'Decimal') {
    const number = Number(text.replace(/,/g, ''));
    return text !== '' && Number.isFinite(number) ? number : edit;
  }
  if (entry.type === 'List') {
    const parts = text.split(/[\s,]+/).filter(Boolean);
    const numbers = parts.map(Number);
    return numbers.every(Number.isFinite) ? numbers : edit;
  }
  return edit;
}

/** The changed keys and their values to send. */
export function changedValues(
  entries: SettingEntry[],
  edits: Record<string, Edit>,
): Record<string, unknown> {
  const values: Record<string, unknown> = {};
  for (const entry of entries) {
    if (!(entry.key in edits)) continue;
    if (edits[entry.key] === toEdit(entry, entry.value)) continue;
    values[entry.key] = fromEdit(entry, edits[entry.key]);
  }
  return values;
}

/** IANA time zone names for the searchable list; the current one at least. */
export function timeZones(current: string): string[] {
  const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const all = supported ? supported('timeZone') : [];
  return all.includes(current) || !current ? all : [current, ...all];
}
