namespace Khata.Application;

public static class FinancialAmounts
{
    // Cosmos JSON numbers must remain exactly representable as IEEE-754 integers.
    public const long MaxCents = 9_007_199_254_740_991L;

    public static bool TryCents(decimal amount, out long cents)
    {
        cents = 0;
        if (amount <= 0 || amount > MaxCents / 100m || decimal.Round(amount, 2) != amount)
            return false;
        cents = decimal.ToInt64(amount * 100m);
        return true;
    }

    public static bool IsRepresentable(long cents) => cents is >= -MaxCents and <= MaxCents;
}
