using Tausi.Domain.Common;

namespace Tausi.Domain.Money;

/// <summary>An amount in one currency. Arithmetic across currencies is refused.</summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money a, Money b) => new(a.Amount + b.SameCurrency(a).Amount, a.Currency);
    public static Money operator -(Money a, Money b) => new(a.Amount - b.SameCurrency(a).Amount, a.Currency);
    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor, a.Currency);
    public static Money operator -(Money a) => new(-a.Amount, a.Currency);

    /// <summary>Rounds half away from zero, the rule used for all money in Tausi.</summary>
    public Money Round(int decimals = 2) => new(MoneyMath.Round(Amount, decimals), Currency);

    public override string ToString() => $"{Currency} {Amount:0.00}";

    private Money SameCurrency(Money other) =>
        Currency == other.Currency
            ? this
            : throw new DomainException($"Cannot combine {Currency} with {other.Currency}.");
}

public static class MoneyMath
{
    public static decimal Round(decimal amount, int decimals = 2) =>
        Math.Round(amount, decimals, MidpointRounding.AwayFromZero);
}
