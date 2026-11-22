using Khata.Application;

namespace Khata.UnitTests;

public class InvestmentGrowthTests
{
    [Theory]
    [InlineData(CompoundingModel.Daily, 110.52)]
    [InlineData(CompoundingModel.Monthly, 110.47)]
    [InlineData(CompoundingModel.Quarterly, 110.38)]
    [InlineData(CompoundingModel.Annually, 110)]
    [InlineData(CompoundingModel.Simple, 110)]
    public void CalculatesOneYear(CompoundingModel model, double expected)
    {
        var result = InvestmentGrowth.Value(100m, 10m, model,
            new DateOnly(2023, 1, 1), new DateOnly(2024, 1, 1));

        Assert.Equal((decimal)expected, result);
    }

    [Fact]
    public void MonthlyGrowthUsesCompletedCalendarPeriodsFromFundingDate()
    {
        var start = new DateOnly(2024, 1, 31);
        Assert.Equal(100m, InvestmentGrowth.Value(100m, 12m, CompoundingModel.Monthly, start, new DateOnly(2024, 2, 28)));
        Assert.Equal(101m, InvestmentGrowth.Value(100m, 12m, CompoundingModel.Monthly, start, new DateOnly(2024, 2, 29)));
        Assert.Equal(101m, InvestmentGrowth.Value(100m, 12m, CompoundingModel.Monthly, start, new DateOnly(2024, 3, 30)));
        Assert.Equal(102.01m, InvestmentGrowth.Value(100m, 12m, CompoundingModel.Monthly, start, new DateOnly(2024, 3, 31)));
    }

    [Fact]
    public void SimpleInterestUsesActualDaysWithA365DayYear()
    {
        Assert.Equal(110.03m, InvestmentGrowth.Value(100m, 10m, CompoundingModel.Simple,
            new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1)));
    }

    [Fact]
    public void RejectsInvalidAmountsRatesAndDates()
    {
        var today = new DateOnly(2024, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => InvestmentGrowth.Value(1.001m, 1m, CompoundingModel.Daily, today, today));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvestmentGrowth.Value(1m, 101m, CompoundingModel.Daily, today, today));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvestmentGrowth.Value(1m, 1.001m, CompoundingModel.Daily, today, today));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvestmentGrowth.Value(1m, 1m, CompoundingModel.Daily, today, today.AddDays(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvestmentGrowth.Value(0m, 0m, (CompoundingModel)99, today, today));
        Assert.Equal(0m, InvestmentGrowth.Value(0m, 100m, CompoundingModel.Daily, today, DateOnly.MaxValue));
    }
}
