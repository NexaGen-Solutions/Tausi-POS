import en from './en.json';

type Messages = { [key: string]: string | Messages };

const messages: Messages = en;

/**
 * Looks up a text by dotted key, e.g. t('pos.enterPin'), and fills {placeholders}.
 * English only for now; the JSON is structured so more languages can be added later.
 */
export function t(key: string, values?: Record<string, string | number>): string {
  let node: string | Messages | undefined = messages;
  for (const part of key.split('.')) {
    node = typeof node === 'object' ? node[part] : undefined;
  }
  if (typeof node !== 'string') return key;
  if (!values) return node;
  return node.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in values ? String(values[name]) : match,
  );
}

/** Permission keys in this table are the ones from AGENTS.md. */
export function permissionLabel(key: string): string {
  const text = (en.permissions as Record<string, string>)[key];
  return text ?? key;
}
