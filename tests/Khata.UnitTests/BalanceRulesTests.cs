using Khata.Application;

namespace Khata.UnitTests;

public class BalanceRulesTests
{
    [Theory]
    [InlineData(GigState.Planned, GigState.Completed, 25, true, false)]
    [InlineData(GigState.Completed, GigState.InProgress, -25, false, true)]
    [InlineData(GigState.Completed, GigState.Cancelled, -25, false, true)]
    [InlineData(GigState.Completed, GigState.Completed, 0, false, false)]
    [InlineData(GigState.Cancelled, GigState.Planned, 0, false, false)]
    public void GigStateChangesPostOnlyPayoutsAndReversals(
        GigState previous, GigState next, int change, bool creates, bool reverses)
    {
        var result = BalanceRules.ChangeGigState(previous, next, 25m);
        Assert.Equal(change, result.CashChange);
        Assert.Equal(creates, result.CreatesPayout);
        Assert.Equal(reverses, result.ReversesPayout);
    }

    [Fact]
    public void CreatingACompletedGigCreditsItsPayout()
    {
        var result = BalanceRules.ChangeGigState(null, GigState.Completed, 12.50m);
        Assert.True(result.CreatesPayout);
        Assert.Equal(12.50m, result.CashChange);
    }

    [Fact]
    public void ReversalCanOverdrawCashButOtherDebitsCannot()
    {
        Assert.Equal(-20m, BalanceRules.ApplyCashChange(5m, -25m, true));
        Assert.Throws<InvalidOperationException>(() => BalanceRules.ApplyCashChange(5m, -25m, false));
        Assert.Throws<InvalidOperationException>(() => BalanceRules.ApplyCashChange(-20m, -1m, false));
        Assert.Equal(-10m, BalanceRules.ApplyCashChange(-20m, 10m, false));
    }

    [Fact]
    public void RejectsInvalidPayoutsAndStates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BalanceRules.ChangeGigState(null, GigState.Completed, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => BalanceRules.ChangeGigState(null, GigState.Completed, 1.001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => BalanceRules.ChangeGigState(null, (GigState)99, 1m));
    }
}
