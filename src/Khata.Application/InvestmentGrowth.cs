namespace Khata.Application;

public enum CompoundingModel
{
    Daily,
    Monthly,
    Quarterly,
    Annually,
    Simple
}

public static class InvestmentGrowth
{
    public static decimal Value(decimal principal, decimal annualRatePercent, CompoundingModel model,
        DateOnly fundedOn, DateOnly asOf)
    {
        if (principal < 0 || decimal.Round(principal, 2) != principal)
            throw new ArgumentOutOfRangeException(nameof(principal));
        if (annualRatePercent is < 0 or > 100 || decimal.Round(annualRatePercent, 2) != annualRatePercent)
            throw new ArgumentOutOfRangeException(nameof(annualRatePercent));
        if (asOf < fundedOn)
            throw new ArgumentOutOfRangeException(nameof(asOf));
        if (!Enum.IsDefined(model))
            throw new ArgumentOutOfRangeException(nameof(model));
        if (principal == 0 || annualRatePercent == 0)
            return principal;

        var days = asOf.DayNumber - fundedOn.DayNumber;
        var rate = annualRatePercent / 100m;
        if (model == CompoundingModel.Simple)
            return decimal.Round(principal * (1m + rate * days / 365m), 2, MidpointRounding.AwayFromZero);

        var (periodsPerYear, periods) = model switch
        {
            CompoundingModel.Daily => (365, days),
            CompoundingModel.Monthly => (12, CompletedMonths(fundedOn, asOf)),
            CompoundingModel.Quarterly => (4, CompletedMonths(fundedOn, asOf) / 3),
            CompoundingModel.Annually => (1, CompletedMonths(fundedOn, asOf) / 12),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        var factor = 1m + rate / periodsPerYear;
        var growth = 1m;
        while (periods > 0)
        {
            if ((periods & 1) != 0) growth *= factor;
            periods >>= 1;
            if (periods != 0) factor *= factor;
        }
        return decimal.Round(principal * growth, 2, MidpointRounding.AwayFromZero);
    }

    private static int CompletedMonths(DateOnly start, DateOnly end)
    {
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month;
        return start.AddMonths(months) > end ? months - 1 : months;
    }
}
