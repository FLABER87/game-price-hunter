namespace GamePriceHunter.Core.Models;

/// <summary>An amount in a specific ISO 4217 currency.</summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Of(decimal amount, string currency) => new(amount, currency.ToUpperInvariant());

    public override string ToString() => $"{Amount:0.00} {Currency}";
}
