using Creche.Domain.Enums;

namespace Creche.Domain.Rules;

public sealed record FeeInput(
    FeePlanType PlanType,
    decimal MonthlyAmount,
    decimal HourlyRate,
    bool IncludesMeals,
    decimal MealsAmount,
    decimal? CustomMonthlyFee = null,
    decimal HoursAttended = 0m,
    int SiblingRank = 1,
    decimal SecondChildDiscountPercent = 0m,
    decimal ThirdChildDiscountPercent = 0m);

/// <param name="BaseFee">Monthly (or hourly × hours) fee before discount.</param>
/// <param name="Meals">Meal supplement.</param>
/// <param name="SiblingDiscount">Positive amount taken off the base fee.</param>
public sealed record FeeBreakdown(decimal BaseFee, decimal Meals, decimal SiblingDiscount, decimal DiscountPercent)
{
    public decimal Total => BaseFee + Meals - SiblingDiscount;
}

public sealed record LatePickupRule(bool Enabled, int GraceMinutes, int BlockMinutes, decimal FeePerBlock);

/// <summary>All fee arithmetic. Everything is <see cref="decimal"/>, rounded to 2 places, half away from zero.</summary>
public static class FeeCalculator
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static FeeBreakdown Calculate(FeeInput input)
    {
        decimal baseFee = input.PlanType == FeePlanType.Hourly
            ? Round(Math.Max(0m, input.HoursAttended) * input.HourlyRate)
            : Round(input.CustomMonthlyFee ?? input.MonthlyAmount);
        if (input.PlanType == FeePlanType.Hourly && input.CustomMonthlyFee is { } fixedFee)
            baseFee = Round(fixedFee);

        decimal meals = input.IncludesMeals ? Round(input.MealsAmount) : 0m;
        decimal percent = SiblingDiscountPercent(input.SiblingRank, input.SecondChildDiscountPercent, input.ThirdChildDiscountPercent);
        // The discount applies to the childcare fee only, never to meals or extras.
        decimal discount = Round(baseFee * percent / 100m);
        return new FeeBreakdown(baseFee, meals, discount, percent);
    }

    /// <summary>Rank 1 (eldest enrolled child) pays full price; rank 2 and rank 3+ get their own percentage.</summary>
    public static decimal SiblingDiscountPercent(int rank, decimal secondChildPercent, decimal thirdChildPercent)
    {
        decimal percent = rank switch
        {
            <= 1 => 0m,
            2 => secondChildPercent,
            _ => thirdChildPercent,
        };
        return Math.Clamp(percent, 0m, 100m);
    }

    /// <summary>Orders siblings eldest first (ties broken by id) and returns each child's 1-based rank.</summary>
    public static Dictionary<int, int> RankSiblings(IEnumerable<(int ChildId, DateOnly DateOfBirth)> siblings)
    {
        var ranks = new Dictionary<int, int>();
        int rank = 1;
        foreach (var (id, _) in siblings.OrderBy(s => s.DateOfBirth).ThenBy(s => s.ChildId))
            ranks[id] = rank++;
        return ranks;
    }

    /// <summary>Minutes after closing time, 0 when on time.</summary>
    public static int LateMinutes(TimeOnly checkOut, TimeOnly closingTime)
        => checkOut <= closingTime ? 0 : (int)Math.Floor((checkOut - closingTime).TotalMinutes);

    /// <summary>One fee per started block once the grace period is exceeded.</summary>
    public static decimal LatePickupFee(int lateMinutes, LatePickupRule rule)
    {
        if (!rule.Enabled || rule.FeePerBlock <= 0m || lateMinutes <= rule.GraceMinutes) return 0m;
        int billable = lateMinutes - rule.GraceMinutes;
        int block = Math.Max(1, rule.BlockMinutes);
        int blocks = (billable + block - 1) / block;
        return Round(blocks * rule.FeePerBlock);
    }
}
