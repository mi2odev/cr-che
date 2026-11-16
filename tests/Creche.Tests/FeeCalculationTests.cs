using Creche.Application.Services;
using Creche.Domain.Enums;
using Creche.Domain.Rules;

namespace Creche.Tests;

public class FeeCalculationTests
{
    [Fact]
    public void Full_day_plan_charges_the_monthly_amount()
    {
        var fee = FeeCalculator.Calculate(new FeeInput(FeePlanType.FullDay, 18000m, 0m, false, 0m));
        Assert.Equal(18000m, fee.BaseFee);
        Assert.Equal(0m, fee.Meals);
        Assert.Equal(0m, fee.SiblingDiscount);
        Assert.Equal(18000m, fee.Total);
    }

    [Fact]
    public void Meals_are_added_only_when_the_plan_includes_them()
    {
        var with = FeeCalculator.Calculate(new FeeInput(FeePlanType.HalfDay, 11000m, 0m, true, 4000m));
        var without = FeeCalculator.Calculate(new FeeInput(FeePlanType.HalfDay, 11000m, 0m, false, 4000m));
        Assert.Equal(15000m, with.Total);
        Assert.Equal(11000m, without.Total);
    }

    [Fact]
    public void Hourly_plan_multiplies_attended_hours_by_the_rate()
    {
        var fee = FeeCalculator.Calculate(new FeeInput(FeePlanType.Hourly, 0m, 250m, false, 0m, HoursAttended: 41.5m));
        Assert.Equal(10375m, fee.Total);
    }

    [Fact]
    public void Custom_monthly_fee_overrides_the_plan()
    {
        var fee = FeeCalculator.Calculate(new FeeInput(FeePlanType.FullDay, 18000m, 0m, false, 0m, CustomMonthlyFee: 15000m));
        Assert.Equal(15000m, fee.Total);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(5, 20)]
    public void Sibling_discount_depends_on_the_rank(int rank, int expectedPercent)
        => Assert.Equal(expectedPercent, FeeCalculator.SiblingDiscountPercent(rank, 10m, 20m));

    [Fact]
    public void Sibling_discount_applies_to_the_fee_but_not_to_meals()
    {
        var fee = FeeCalculator.Calculate(new FeeInput(FeePlanType.FullDay, 18000m, 0m, true, 4000m, SiblingRank: 2, SecondChildDiscountPercent: 10m, ThirdChildDiscountPercent: 20m));
        Assert.Equal(1800m, fee.SiblingDiscount);
        Assert.Equal(18000m + 4000m - 1800m, fee.Total);
    }

    [Fact]
    public void Amounts_are_rounded_to_two_decimals_half_away_from_zero()
    {
        var fee = FeeCalculator.Calculate(new FeeInput(FeePlanType.FullDay, 333.33m, 0m, false, 0m, SiblingRank: 2, SecondChildDiscountPercent: 12.5m));
        Assert.Equal(41.67m, fee.SiblingDiscount); // 41.66625 → 41.67
        Assert.Equal(0.13m, FeeCalculator.Round(0.125m));
    }

    [Fact]
    public void Siblings_are_ranked_eldest_first()
    {
        var ranks = FeeCalculator.RankSiblings([(7, new DateOnly(2024, 5, 1)), (3, new DateOnly(2022, 1, 1)), (9, new DateOnly(2025, 2, 1))]);
        Assert.Equal(1, ranks[3]);
        Assert.Equal(2, ranks[7]);
        Assert.Equal(3, ranks[9]);
    }

    [Theory]
    [InlineData("17:00", 0, 0)]      // on time
    [InlineData("17:10", 10, 0)]     // inside the 10-minute grace period
    [InlineData("17:11", 11, 200)]   // first started block
    [InlineData("17:25", 25, 200)]   // 15 billable minutes = one block
    [InlineData("17:26", 26, 400)]   // second block started
    public void Late_pick_up_fee_is_charged_per_started_block_after_the_grace_period(string checkOut, int expectedLate, int expectedFee)
    {
        var rule = new LatePickupRule(true, GraceMinutes: 10, BlockMinutes: 15, FeePerBlock: 200m);
        int late = FeeCalculator.LateMinutes(TimeOnly.Parse(checkOut), new TimeOnly(17, 0));
        Assert.Equal(expectedLate, late);
        Assert.Equal(expectedFee, FeeCalculator.LatePickupFee(late, rule));
    }

    [Fact]
    public void Late_pick_up_fee_is_zero_when_the_rule_is_disabled()
        => Assert.Equal(0m, FeeCalculator.LatePickupFee(90, new LatePickupRule(false, 0, 15, 200m)));

    [Fact]
    public async Task Monthly_invoices_contain_fee_meals_and_pending_extras()
    {
        using var host = await TestHost.CreateAsync();
        var plan = await host.AddPlanAsync(18000m, meals: 4000m);
        var child = await host.EnrolAsync("Ines", planId: plan.Id);
        var billing = host.Get<IBillingService>();
        await billing.AddExtraChargeAsync(child.Id, new DateOnly(2026, 11, 5), ChargeKind.Activity, "Piscine", 1500m);

        var result = await billing.GenerateMonthlyInvoicesAsync(2026, 11);

        Assert.Equal(1, result.Created);
        Assert.Equal(23500m, result.TotalAmount);
        var invoice = await billing.GetInvoiceAsync((await billing.ListInvoicesAsync(new InvoiceFilter())).Single().Id);
        Assert.Equal([ChargeKind.MonthlyFee, ChargeKind.Meals, ChargeKind.Activity], invoice!.Lines.Select(l => l.Kind).ToArray());
        Assert.StartsWith("F-2026-", invoice.Number);
        Assert.Empty(await billing.ListExtraChargesAsync(pendingOnly: true));
    }

    [Fact]
    public async Task Generating_twice_never_bills_a_child_twice()
    {
        using var host = await TestHost.CreateAsync();
        var plan = await host.AddPlanAsync();
        await host.EnrolAsync("Ines", planId: plan.Id);
        var billing = host.Get<IBillingService>();

        await billing.GenerateMonthlyInvoicesAsync(2026, 11);
        var second = await billing.GenerateMonthlyInvoicesAsync(2026, 11);

        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Skipped);
        Assert.Single(await billing.ListInvoicesAsync(new InvoiceFilter()));
    }

    [Fact]
    public async Task Children_on_the_waiting_list_or_gone_before_the_month_are_not_billed()
    {
        using var host = await TestHost.CreateAsync();
        var plan = await host.AddPlanAsync();
        await host.EnrolAsync("Waiting", planId: plan.Id, waitingList: true);
        var gone = await host.EnrolAsync("Gone", planId: plan.Id);
        await host.Get<IEnrolmentService>().LeaveAsync(gone.Id, new DateOnly(2026, 10, 20), "Moved");
        var notYet = await host.EnrolAsync("Later", planId: plan.Id, enrolmentDate: new DateOnly(2026, 12, 1));
        host.Clock.Now = new DateTime(2026, 12, 1);

        var result = await host.Get<IBillingService>().GenerateMonthlyInvoicesAsync(2026, 11);

        Assert.Equal(0, result.Created);
        Assert.NotEqual(0, notYet.Id);
    }

    [Fact]
    public async Task Hourly_plans_bill_the_hours_actually_attended()
    {
        using var host = await TestHost.CreateAsync();
        var group = await host.AddGroupAsync();
        var plan = await host.AddPlanAsync(0m, FeePlanType.Hourly, hourlyRate: 300m);
        var child = await host.EnrolAsync("Nour", groupId: group.Id, planId: plan.Id);
        var attendance = host.Get<IAttendanceService>();
        foreach (int day in new[] { 2, 3 })
        {
            var date = new DateOnly(2026, 11, day);
            await attendance.CheckInAsync(child.Id, date, new TimeOnly(8, 0), null);
            await attendance.CheckOutAsync(child.Id, date, new TimeOnly(12, 30), null);
        }

        var result = await host.Get<IBillingService>().GenerateMonthlyInvoicesAsync(2026, 11);

        Assert.Equal(9m * 300m, result.TotalAmount);
    }
}
