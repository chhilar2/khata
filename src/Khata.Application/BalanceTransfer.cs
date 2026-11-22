namespace Khata.Application;

public enum InvestmentState { Unfunded, Active, Closed }
public enum InvestmentKind { FixedRate, MarketLinked }

public sealed record InvestmentPosition(
    string Id, InvestmentKind Kind, InvestmentState State, decimal AnnualRatePercent,
    CompoundingModel Compounding, decimal Principal, DateOnly? FundedOn,
    DateOnly? ClosedOn = null, decimal? ClosingValue = null);

public sealed record BalanceTransferResult(decimal Cash, InvestmentPosition? Source,
    InvestmentPosition? Destination, decimal Amount);

public static class BalanceTransfer
{
    public static BalanceTransferResult Apply(decimal cash, InvestmentPosition? source,
        InvestmentPosition? destination, decimal amount, DateOnly asOf)
    {
        if (!BalanceRules.IsPositiveAmount(amount))
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (source is null && destination is null ||
            source is not null && destination is not null && source.Id == destination.Id)
            throw new InvalidOperationException("Source and destination must be different.");
        if (destination is not null)
        {
            if (destination.State != InvestmentState.Unfunded || destination.Principal != 0 ||
                destination.FundedOn is not null || destination.ClosedOn is not null || destination.ClosingValue is not null)
                throw new InvalidOperationException("The destination investment is already funded.");
            if (destination.Kind != InvestmentKind.FixedRate)
                throw new InvalidOperationException("Market-linked investments are not available for funding.");
            _ = InvestmentGrowth.Value(0m, destination.AnnualRatePercent, destination.Compounding, asOf, asOf);
        }

        if (source is null)
        {
            cash = BalanceRules.ApplyCashChange(cash, -amount, false);
        }
        else
        {
            if (source.State != InvestmentState.Active || source.FundedOn is null ||
                !BalanceRules.IsPositiveAmount(source.Principal) || source.ClosedOn is not null || source.ClosingValue is not null)
                throw new InvalidOperationException("The source investment is not active.");
            if (source.Kind != InvestmentKind.FixedRate)
                throw new InvalidOperationException("The source investment value is unavailable.");
            var value = InvestmentGrowth.Value(source.Principal, source.AnnualRatePercent,
                source.Compounding, source.FundedOn.Value, asOf);
            if (value != amount)
                throw new InvalidOperationException("The investment value changed; refresh before transferring.");
            source = source with { State = InvestmentState.Closed, ClosedOn = asOf, ClosingValue = value };
        }

        if (destination is null)
            cash = BalanceRules.ApplyCashChange(cash, amount, false);
        else
            destination = destination with { State = InvestmentState.Active, Principal = amount, FundedOn = asOf };
        return new BalanceTransferResult(cash, source, destination, amount);
    }
}
