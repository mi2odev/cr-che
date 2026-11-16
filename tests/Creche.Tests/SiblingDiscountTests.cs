using Creche.Application.Services;
using Creche.Domain.Enums;

namespace Creche.Tests;

public class SiblingDiscountTests
{
    [Fact]
    public async Task Second_and_third_child_of_a_family_get_their_discount()
    {
        using var host = await TestHost.CreateAsync();
        await host.UpdateSettingsAsync(s => { s.SecondChildDiscountPercent = 10m; s.ThirdChildDiscountPercent = 20m; });
        var plan = await host.AddPlanAsync(10000m, meals: 2000m);
        var eldest = await host.EnrolAsync("Eldest", planId: plan.Id, dateOfBirth: new DateOnly(2022, 3, 1));
        var middle = await host.EnrolAsync("Middle", familyId: eldest.FamilyId, planId: plan.Id, dateOfBirth: new DateOnly(2023, 6, 1));
        var youngest = await host.EnrolAsync("Youngest", familyId: eldest.FamilyId, planId: plan.Id, dateOfBirth: new DateOnly(2025, 1, 1));
        var billing = host.Get<IBillingService>();

        await billing.GenerateMonthlyInvoicesAsync(2026, 11);

        var totals = (await billing.ListInvoicesAsync(new InvoiceFilter())).ToDictionary(i => i.ChildId, i => i.Total);
        Assert.Equal(12000m, totals[eldest.Id]);              // full price
        Assert.Equal(12000m - 1000m, totals[middle.Id]);      // 10 % of the 10 000 fee, meals untouched
        Assert.Equal(12000m - 2000m, totals[youngest.Id]);    // 20 %
    }

    [Fact]
    public async Task Children_of_different_families_get_no_discount()
    {
        using var host = await TestHost.CreateAsync();
        var plan = await host.AddPlanAsync(10000m);
        await host.EnrolAsync("A", planId: plan.Id);
        await host.EnrolAsync("B", planId: plan.Id);
        var billing = host.Get<IBillingService>();

        await billing.GenerateMonthlyInvoicesAsync(2026, 11);

        Assert.All(await billing.ListInvoicesAsync(new InvoiceFilter()), i => Assert.Equal(10000m, i.Total));
    }

    [Fact]
    public async Task A_sibling_who_left_or_is_waiting_does_not_count_for_the_rank()
    {
        using var host = await TestHost.CreateAsync();
        var plan = await host.AddPlanAsync(10000m);
        var eldest = await host.EnrolAsync("Eldest", planId: plan.Id, dateOfBirth: new DateOnly(2022, 3, 1));
        var younger = await host.EnrolAsync("Younger", familyId: eldest.FamilyId, planId: plan.Id, dateOfBirth: new DateOnly(2024, 3, 1));
        await host.EnrolAsync("Baby", familyId: eldest.FamilyId, planId: plan.Id, dateOfBirth: new DateOnly(2026, 3, 1), waitingList: true);
        await host.Get<IEnrolmentService>().LeaveAsync(eldest.Id, new DateOnly(2026, 10, 31), null);
        var billing = host.Get<IBillingService>();

        await billing.GenerateMonthlyInvoicesAsync(2026, 11);

        var invoice = Assert.Single(await billing.ListInvoicesAsync(new InvoiceFilter()));
        Assert.Equal(younger.Id, invoice.ChildId);
        Assert.Equal(10000m, invoice.Total);
    }

    [Fact]
    public async Task The_discount_appears_as_its_own_negative_invoice_line()
    {
        using var host = await TestHost.CreateAsync();
        var plan = await host.AddPlanAsync(10000m);
        var eldest = await host.EnrolAsync("Eldest", planId: plan.Id, dateOfBirth: new DateOnly(2022, 3, 1));
        var second = await host.EnrolAsync("Second", familyId: eldest.FamilyId, planId: plan.Id, dateOfBirth: new DateOnly(2024, 3, 1));
        var billing = host.Get<IBillingService>();
        await billing.GenerateMonthlyInvoicesAsync(2026, 11);

        var item = (await billing.ListInvoicesAsync(new InvoiceFilter())).Single(i => i.ChildId == second.Id);
        var invoice = await billing.GetInvoiceAsync(item.Id);

        var discount = Assert.Single(invoice!.Lines, l => l.Kind == ChargeKind.SiblingDiscount);
        Assert.Equal(-1000m, discount.Amount);
        Assert.Equal(invoice.Total, invoice.Lines.Sum(l => l.Amount));
    }
}
