using Nexagen.Domain.Common;

namespace Nexagen.Domain.People;

public static class Pin
{
    public const int MinLength = 4;
    public const int MaxLength = 6;

    public static void EnsureValid(string pin)
    {
        if (pin.Length is < MinLength or > MaxLength || !pin.All(char.IsAsciiDigit))
            throw new DomainException($"A PIN must be {MinLength} to {MaxLength} digits.");
    }
}
