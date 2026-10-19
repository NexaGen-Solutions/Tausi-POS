/** "20 Oct 09:14" in the terminal's local time. */
export function formatDayTime(iso: string): string {
  const d = new Date(iso);
  const day = d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
  const time = d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  return `${day} ${time}`;
}
