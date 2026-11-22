using Khata.Application;

namespace Khata.UnitTests;

public class BalanceTransferTests
{
    private static readonly DateOnly Start = new(2023, 1, 1);
    private static readonly DateOnly End = new(2024, 1, 1);
    private static InvestmentPosition Unfunded(string id) =>
        new(id, InvestmentKind.FixedRate, InvestmentState.Unfunded, 10m, CompoundingModel.Annually, 0m, null);
    private static InvestmentPosition Active(string id) =>
        Unfunded(id) with { State = InvestmentState.Active, Principal = 100m, FundedOn = Start };

    [Fact]
    public void TransferFundsAnInvestmentAndWithdrawsItsFullCurrentValue()
    {
        var funded = BalanceTransfer.Apply(150m, null, Unfunded("a"), 100m, Start);
        Assert.Equal(50m, funded.Cash);
        Assert.Equal(InvestmentState.Active, funded.Destination!.State);
        var withdrawn = BalanceTransfer.Apply(funded.Cash, funded.Destination, null, 110m, End);
        Assert.Equal(160m, withdrawn.Cash);
        Assert.Equal(InvestmentState.Closed, withdrawn.Source!.State);
        Assert.Equal(110m, withdrawn.Source.ClosingValue);
    }

    [Fact]
    public void TransferBetweenInvestmentsClosesSourceAndFundsDestination()
    {
        var result = BalanceTransfer.Apply(-5m, Active("a"), Unfunded("b"), 110m, End);
        Assert.Equal(-5m, result.Cash);
        Assert.Equal(InvestmentState.Closed, result.Source!.State);
        Assert.Equal(110m, result.Destination!.Principal);
        Assert.Equal(End, result.Destination.FundedOn);
    }

    [Fact]
    public void RejectsPartialOrStaleValuesAndInvalidDestinations()
    {
        Assert.Throws<InvalidOperationException>(() => BalanceTransfer.Apply(0, Active("a"), null, 100m, End));
        Assert.Throws<InvalidOperationException>(() => BalanceTransfer.Apply(10m, null, Unfunded("b"), 20m, End));
        Assert.Throws<InvalidOperationException>(() => BalanceTransfer.Apply(100m, null, Active("b"), 20m, End));
        Assert.Throws<InvalidOperationException>(() => BalanceTransfer.Apply(0m, Active("a"), Unfunded("a"), 110m, End));
        var market = Unfunded("market") with { Kind = InvestmentKind.MarketLinked };
        Assert.Throws<InvalidOperationException>(() => BalanceTransfer.Apply(100m, null, market, 20m, End));
    }
}
