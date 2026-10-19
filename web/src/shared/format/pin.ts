/** Same rules as the server (Domain Pin): 4-6 digits, not all one digit, not a run up or down. */
export function isWellFormedPin(pin: string): boolean {
  return /^\d{4,6}$/.test(pin);
}

export function isTooSimplePin(pin: string): boolean {
  if (pin.length < 2) return true;
  if ([...pin].every((c) => c === pin[0])) return true;
  const run = (step: number) =>
    [...pin].every((c, i) => i === 0 || c.charCodeAt(0) - pin.charCodeAt(i - 1) === step);
  return run(1) || run(-1);
}
