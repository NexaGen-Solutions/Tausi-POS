using Nexagen.Domain.Common;

namespace Nexagen.Domain.People;

public static class Pin
{
    public const int MinLength = 4;
    public const int MaxLength = 6;

    public static bool IsWellFormed(string pin) =>
        pin.Length is >= MinLength and <= MaxLength && pin.All(char.IsAsciiDigit);

    public static void EnsureValid(string pin)
    {
        if (!IsWellFormed(pin))
            throw new DomainException($"A PIN must be {MinLength} to {MaxLength} digits.");
    }

    /// <summary>
    /// PINs too easy to guess: all the same digit (0000), or a run up or down (1234, 9876).
    /// </summary>
    public static bool IsTooSimple(string pin)
    {
        if (pin.Length < 2) return true;
        if (pin.All(c => c == pin[0])) return true;
        bool Run(int step)
        {
            for (var i = 1; i < pin.Length; i++)
                if (pin[i] - pin[i - 1] != step) return false;
            return true;
        }
        return Run(1) || Run(-1);
    }
}
