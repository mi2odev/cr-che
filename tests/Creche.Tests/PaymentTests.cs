using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Enums;
using Creche.Domain.Rules;

namespace Creche.Tests;

public class PaymentTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    private static async Task<(TestHost Host, int FamilyId, IBillingService Billing, IPaymentService Payments)> FamilyWithInvoiceAsync(decimal monthly = 10000m)
    {
        var host = await TestHost.CreateAsync();
        var plan = await host.AddPlanAsync(monthly);
        var child = await host.EnrolAsync("Ines", planId: plan.Id);
        var billing = host.Get<IBillingService>();
        await billing.GenerateMonthlyInvoicesAsync(2026, 11);
        return (host, child.FamilyId, billing, host.Get<IPaymentService>());
    }

    [Fact]
    public async Task A_partial_payment_leaves_the_invoice_partially_paid()
    {
        var (host, familyId, billing, payments) = await FamilyWithInvoiceAsync();
        using var _ = host;

        var payment = await payments.RecordPaymentAsync(new PaymentRequest(familyId, 4000m, PaymentMethod.Cash, Today));

        var invoice = Assert.Single(await billing.ListInvoicesAsync(new InvoiceFilter()));
        Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);
        Assert.Equal(4000m, invoice.PaidAmount);
        Assert.Equal(6000m, invoice.Remaining);
        Assert.StartsWith("R-2026-", payment.ReceiptNumber);
        Assert.Equal(6000m, (await payments.GetFamilyBalanceAsync(familyId)).Due);
    }

    [Fact]
    public async Task Several_partial_payments_settle_the_invoice()
    {
        var (host, familyId, billing, payments) = await FamilyWithInvoiceAsync();
        using var _ = host;

        await payments.RecordPaymentAsync(new PaymentRequest(familyId, 4000m, PaymentMethod.Cash, Today));
        await payments.RecordPaymentAsync(new PaymentRequest(familyId, 6000m, PaymentMethod.Card, Today));

        var invoice = Assert.Single(await billing.ListInvoicesAsync(new InvoiceFilter()));
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(0m, (await payments.GetFamilyBalanceAsync(familyId)).Due);
        Assert.Empty(await payments.GetDebtorsAsync());
    }

    [Fact]
    public async Task A_payment_settles_the_oldest_invoice_first()
    {
        var (host, familyId, billing, payments) = await FamilyWithInvoiceAsync();
        using var _ = host;
        host.Clock.Now = new DateTime(2026, 12, 2);
        await billing.GenerateMonthlyInvoicesAsync(2026, 12);

        await payments.RecordPaymentAsync(new PaymentRequest(familyId, 12000m, PaymentMethod.Cash, new DateOnly(2026, 12, 2)));

        var invoices = (await billing.ListInvoicesAsync(new InvoiceFilter())).OrderBy(i => i.Period).ToList();
        Assert.Equal(InvoiceStatus.Paid, invoices[0].Status);            // November
        Assert.Equal(InvoiceStatus.PartiallyPaid, invoices[1].Status);   // December
        Assert.Equal(2000m, invoices[1].PaidAmount);
        Assert.Equal(8000m, (await payments.GetFamilyBalanceAsync(familyId)).Due);
    }

    [Fact]
    public async Task An_advance_payment_becomes_credit_and_pays_the_next_invoice()
    {
        var (host, familyId, billing, payments) = await FamilyWithInvoiceAsync();
        using var _ = host;

        await payments.RecordPaymentAsync(new PaymentRequest(familyId, 15000m, PaymentMethod.Cash, Today));
        var balance = await payments.GetFamilyBalanceAsync(familyId);
        Assert.Equal(0m, balance.Due);
        Assert.Equal(5000m, balance.Credit);

        host.Clock.Now = new DateTime(2026, 12, 2);
        await billing.GenerateMonthlyInvoicesAsync(2026, 12);

        var december = (await billing.ListInvoicesAsync(new InvoiceFilter(Period: new DateOnly(2026, 12, 1)))).Single();
        Assert.Equal(InvoiceStatus.PartiallyPaid, december.Status);
        Assert.Equal(5000m, december.PaidAmount);
        Assert.Equal(5000m, (await payments.GetFamilyBalanceAsync(familyId)).Due);
    }

    [Fact]
    public async Task Voiding_a_payment_reopens_the_invoice_and_keeps_the_payment_in_history()
    {
        var (host, familyId, billing, payments) = await FamilyWithInvoiceAsync();
        using var _ = host;
        var payment = await payments.RecordPaymentAsync(new PaymentRequest(familyId, 10000m, PaymentMethod.Cash, Today));

        await payments.VoidPaymentAsync(payment.Id, "Entered twice");

        var invoice = Assert.Single(await billing.ListInvoicesAsync(new InvoiceFilter()));
        Assert.Equal(InvoiceStatus.Unpaid, invoice.Status);
        Assert.Equal(0m, invoice.PaidAmount);
        var history = Assert.Single(await payments.ListPaymentsAsync(familyId: familyId));
        Assert.True(history.IsVoided);
        Assert.Equal("Entered twice", history.VoidReason);
        Assert.Equal(10000m, (await payments.GetFamilyBalanceAsync(familyId)).Due);
        await Assert.ThrowsAsync<BusinessRuleException>(() => payments.VoidPaymentAsync(payment.Id, "again"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task A_payment_must_be_positive(int amount)
    {
        var (host, familyId, _, payments) = await FamilyWithInvoiceAsync();
        using var __ = host;
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => payments.RecordPaymentAsync(new PaymentRequest(familyId, amount, PaymentMethod.Cash, Today)));
        Assert.Equal("PaymentAmountInvalid", error.Code);
    }

    [Fact]
    public async Task A_payment_cannot_be_dated_in_the_future()
    {
        var (host, familyId, _, payments) = await FamilyWithInvoiceAsync();
        using var __ = host;
        await Assert.ThrowsAsync<BusinessRuleException>(() => payments.RecordPaymentAsync(new PaymentRequest(familyId, 100m, PaymentMethod.Cash, Today.AddDays(1))));
    }

    [Fact]
    public async Task Debtors_list_shows_how_much_each_family_owes_and_since_when()
    {
        var (host, familyId, billing, payments) = await FamilyWithInvoiceAsync();
        using var _ = host;
        await payments.RecordPaymentAsync(new PaymentRequest(familyId, 2500m, PaymentMethod.Cash, Today));
        var dueDate = (await billing.ListInvoicesAsync(new InvoiceFilter())).Single().DueDate;
        host.Clock.Now = dueDate.AddDays(12).ToDateTime(new TimeOnly(9, 0));

        var debtor = Assert.Single(await payments.GetDebtorsAsync());

        Assert.Equal(familyId, debtor.FamilyId);
        Assert.Equal(7500m, debtor.AmountDue);
        Assert.Equal(dueDate, debtor.OldestDueDate);
        Assert.Equal(12, debtor.DaysLate);
        Assert.Equal("Parent Test", debtor.Guardian);
        Assert.True((await billing.ListInvoicesAsync(new InvoiceFilter())).Single().IsOverdue);
    }

    [Fact]
    public async Task The_receipt_lists_what_the_payment_settled_and_what_is_still_due()
    {
        var (host, familyId, _, payments) = await FamilyWithInvoiceAsync();
        using var __ = host;
        var payment = await payments.RecordPaymentAsync(new PaymentRequest(familyId, 4000m, PaymentMethod.Card, Today, "CB-123"));

        var receipt = await payments.GetReceiptAsync(payment.Id);

        Assert.NotNull(receipt);
        Assert.Equal(4000m, receipt.Amount);
        Assert.Equal(4000m, Assert.Single(receipt.Lines).Amount);
        Assert.Equal(6000m, receipt.BalanceDueAfter);
        Assert.Equal(0m, receipt.Unallocated);
    }

    [Fact]
    public async Task An_invoice_with_payments_cannot_be_cancelled_but_an_unpaid_one_can()
    {
        var (host, familyId, billing, payments) = await FamilyWithInvoiceAsync();
        using var _ = host;
        var invoice = (await billing.ListInvoicesAsync(new InvoiceFilter())).Single();
        var payment = await payments.RecordPaymentAsync(new PaymentRequest(familyId, 1000m, PaymentMethod.Cash, Today));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => billing.CancelInvoiceAsync(invoice.Id));
        Assert.Equal("InvoiceHasPayments", error.Code);

        await payments.VoidPaymentAsync(payment.Id, "mistake");
        await billing.CancelInvoiceAsync(invoice.Id);
        Assert.Empty(await billing.ListInvoicesAsync(new InvoiceFilter()));
        Assert.Equal(InvoiceStatus.Cancelled, (await billing.ListInvoicesAsync(new InvoiceFilter(IncludeCancelled: true))).Single().Status);
        Assert.Equal(0m, (await payments.GetFamilyBalanceAsync(familyId)).Due);
    }

    [Fact]
    public async Task Registration_fee_is_invoiced_at_enrolment()
    {
        using var host = await TestHost.CreateAsync();
        var child = await host.EnrolAsync("Ines", registrationFee: 5000m);

        var invoice = Assert.Single(await host.Get<IBillingService>().ListInvoicesAsync(new InvoiceFilter()));

        Assert.Equal(5000m, invoice.Total);
        Assert.Null(invoice.Period);
        Assert.Equal(child.FamilyId, invoice.FamilyId);
    }

    [Fact]
    public void Allocation_rule_spreads_money_in_order_and_returns_the_rest()
    {
        var result = BillingRules.Allocate(250m, [(1, 100m), (2, 0m), (3, 100m)]);
        Assert.Equal([(1, 100m), (3, 100m)], result.Allocations);
        Assert.Equal(50m, result.Unallocated);
    }

    [Theory]
    [InlineData(100, 0, InvoiceStatus.Unpaid)]
    [InlineData(100, 40, InvoiceStatus.PartiallyPaid)]
    [InlineData(100, 100, InvoiceStatus.Paid)]
    public void Invoice_status_follows_the_paid_amount(int total, int paid, InvoiceStatus expected)
        => Assert.Equal(expected, BillingRules.StatusFor(total, paid));
}
