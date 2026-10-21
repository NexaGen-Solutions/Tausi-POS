/**
 * A short two-note chime for a new approval request. Browsers only allow sound after the user has
 * touched the page, so the audio is set up on the first tap or key press; before that, it is silent.
 */
let context: AudioContext | null = null;
let listening = false;

function unlock() {
  if (context || typeof window === 'undefined' || !('AudioContext' in window)) return;
  try {
    context = new AudioContext();
  } catch {
    context = null;
  }
}

/** Starts waiting for the first interaction. Safe to call more than once. */
export function prepareChime() {
  if (listening || typeof window === 'undefined') return;
  listening = true;
  window.addEventListener('pointerdown', unlock, { once: true, capture: true });
  window.addEventListener('keydown', unlock, { once: true, capture: true });
}

export function playChime() {
  if (!context) return;
  try {
    void context.resume();
    const now = context.currentTime;
    [880, 1175].forEach((frequency, i) => {
      const oscillator = context!.createOscillator();
      const gain = context!.createGain();
      oscillator.frequency.value = frequency;
      gain.gain.setValueAtTime(0.0001, now + i * 0.18);
      gain.gain.exponentialRampToValueAtTime(0.25, now + i * 0.18 + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, now + i * 0.18 + 0.16);
      oscillator.connect(gain).connect(context!.destination);
      oscillator.start(now + i * 0.18);
      oscillator.stop(now + i * 0.18 + 0.18);
    });
  } catch {
    // No sound is fine; the toast and badge still show.
  }
}
