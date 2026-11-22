namespace Khata.Application;

public enum GigState { Planned, InProgress, Completed, Cancelled }

public sealed record GigPayoutChange(decimal CashChange, bool CreatesPayout, bool ReversesPayout);

public static class BalanceRules
{
    public static bool IsPositiveAmount(decimal amount) => amount > 0 && decimal.Round(amount, 2) == amount;

    public static GigPayoutChange ChangeGigState(GigState? previous, GigState next, decimal payout)
    {
        if ((previous is not null && !Enum.IsDefined(previous.Value)) || !Enum.IsDefined(next))
            throw new ArgumentOutOfRangeException(nameof(next));
        if (!IsPositiveAmount(payout))
            throw new ArgumentOutOfRangeException(nameof(payout));
        var createsPayout = previous != GigState.Completed && next == GigState.Completed;
        var reversesPayout = previous == GigState.Completed && next != GigState.Completed;
        return new GigPayoutChange(createsPayout ? payout : reversesPayout ? -payout : 0m,
            createsPayout, reversesPayout);
    }

    public static decimal ApplyCashChange(decimal cash, decimal change, bool isGigReversal)
    {
        if (decimal.Round(cash, 2) != cash || decimal.Round(change, 2) != change)
            throw new ArgumentOutOfRangeException(nameof(change));
        if (isGigReversal && change >= 0)
            throw new ArgumentOutOfRangeException(nameof(change));
        var updated = checked(cash + change);
        if (!isGigReversal && change < 0 && updated < 0)
            throw new InvalidOperationException("Insufficient available cash.");
        return updated;
    }
}
