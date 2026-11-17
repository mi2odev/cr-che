using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Application.Settings;
using Creche.Domain.Entities;
using Creche.Domain.Enums;

namespace Creche.App.Services;

/// <summary>Builds the printable FlowDocuments: receipts, invoices, enrolment forms, attendance sheets and reports.</summary>
public static class PrintDocuments
{
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush Shade = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));

    private static string Cash(decimal amount, CrecheSettings s) => Money.Format(amount, s.Currency);
    private static string Day(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
    private static string Month(DateOnly? period) => period?.ToString("MMMM yyyy", CultureInfo.CurrentCulture) ?? "";
    private static string Clock(TimeOnly? time) => time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";

    // ───────────── Documents ─────────────

    public static FlowDocument Receipt(ReceiptData r, CrecheSettings s)
    {
        bool a5 = !string.Equals(s.ReceiptPaper, "A4", StringComparison.OrdinalIgnoreCase);
        var doc = NewDocument(a5);
        Header(doc, s, s.ReceiptShowLogo);
        if (!string.IsNullOrWhiteSpace(s.ReceiptHeader)) doc.Blocks.Add(Text(s.ReceiptHeader, 11, Grey, TextAlignment.Center));
        doc.Blocks.Add(Heading($"{Loc.T("Print.Receipt")} {r.ReceiptNumber}"));
        if (r.IsVoided) doc.Blocks.Add(Text(Loc.T("Payments.Voided").ToUpper(CultureInfo.CurrentCulture), 14, Brushes.DarkRed, TextAlignment.Center, bold: true));

        doc.Blocks.Add(Pairs(
            (Loc.T("Common.Date"), Day(r.Date)),
            (Loc.T("Common.Family"), r.FamilyName),
            (Loc.T("Print.ReceivedFrom"), r.Guardian ?? r.FamilyName),
            (Loc.T("Payments.Method"), Loc.Enum(r.Method)),
            (Loc.T("Payments.Reference"), r.Reference ?? ""),
            (Loc.T("Print.ReceivedBy"), r.ReceivedBy ?? "")));

        if (r.Lines.Count > 0)
            doc.Blocks.Add(Grid([Loc.T("Billing.Invoice"), Loc.T("Common.Child"), Loc.T("Billing.Period"), Loc.T("Common.Amount")],
                r.Lines.Select(l => new[] { l.InvoiceNumber, l.ChildName, Month(l.Period), Cash(l.Amount, s) }), rightColumns: [3]));

        doc.Blocks.Add(Totals(s,
            (Loc.T("Print.AmountPaid"), r.Amount, true),
            (Loc.T("Print.Advance"), r.Unallocated, false),
            (Loc.T("Print.BalanceDue"), s.ReceiptShowBalance ? r.BalanceDueAfter : null, false)));
        if (!string.IsNullOrWhiteSpace(r.Notes)) doc.Blocks.Add(Text(r.Notes, 11, Grey));
        doc.Blocks.Add(Signature(Loc.T("Print.Signature")));
        if (!string.IsNullOrWhiteSpace(s.ReceiptFooter)) doc.Blocks.Add(Text(s.ReceiptFooter, 11, Grey, TextAlignment.Center));
        return doc;
    }

    public static FlowDocument Invoice(Invoice invoice, CrecheSettings s)
    {
        var doc = NewDocument();
        Header(doc, s, true);
        doc.Blocks.Add(Heading($"{Loc.T("Billing.Invoice")} {invoice.Number}"));
        doc.Blocks.Add(Pairs(
            (Loc.T("Common.Family"), invoice.Family?.Name ?? ""),
            (Loc.T("Common.Child"), invoice.Child?.FullName ?? ""),
            (Loc.T("Billing.Period"), Month(invoice.Period)),
            (Loc.T("Billing.IssueDate"), Day(invoice.IssueDate)),
            (Loc.T("Billing.DueDate"), Day(invoice.DueDate)),
            (Loc.T("Common.Status"), Loc.Enum(invoice.Status))));
        doc.Blocks.Add(Grid([Loc.T("Common.Description"), Loc.T("Common.Amount")],
            invoice.Lines.Select(l => new[] { $"{Loc.Enum(l.Kind)} {l.Description}".Trim(), Cash(l.Amount, s) }), rightColumns: [1]));
        doc.Blocks.Add(Totals(s,
            (Loc.T("Common.Total"), invoice.Total, true),
            (Loc.T("Billing.Paid"), invoice.PaidAmount, false),
            (Loc.T("Billing.Remaining"), invoice.Remaining, true)));
        return doc;
    }

    public static FlowDocument EnrolmentForm(Child child, CrecheSettings s)
    {
        var doc = NewDocument();
        Header(doc, s, true);
        doc.Blocks.Add(Heading(Loc.T("Print.EnrolmentForm")));
        doc.Blocks.Add(Text($"{Loc.T("Settings.SchoolYear")}: {child.SchoolYear ?? s.SchoolYear}", 12, Grey, TextAlignment.Center));

        doc.Blocks.Add(Section(Loc.T("Common.Child")));
        doc.Blocks.Add(Pairs(
            (Loc.T("Common.LastName"), child.LastName),
            (Loc.T("Common.FirstName"), child.FirstName),
            (Loc.T("Children.BirthDate"), Day(child.DateOfBirth)),
            (Loc.T("Children.Sex"), Loc.Enum(child.Sex)),
            (Loc.T("Common.Group"), child.Group?.Name ?? ""),
            (Loc.T("Children.FeePlan"), child.FeePlan?.Name ?? ""),
            (Loc.T("Children.EnrolmentDate"), child.EnrolmentDate is { } d ? Day(d) : ""),
            (Loc.T("Common.Status"), Loc.Enum(child.Status))));

        doc.Blocks.Add(Section(Loc.T("Children.Health")));
        doc.Blocks.Add(Pairs(
            (Loc.T("Children.Allergies"), child.Allergies ?? "—"),
            (Loc.T("Children.MedicalNotes"), child.MedicalNotes ?? "—"),
            (Loc.T("Children.Instructions"), child.SpecialInstructions ?? "—"),
            (Loc.T("Children.Vaccinations"), child.Vaccinations.Count == 0 ? "—" : string.Join(", ", child.Vaccinations.Select(v => v.Date is { } date ? $"{v.Name} ({Day(date)})" : v.Name)))));

        doc.Blocks.Add(Section(Loc.T("Families.Guardians")));
        var guardians = child.Family?.Guardians ?? [];
        doc.Blocks.Add(Grid([Loc.T("Common.Name"), Loc.T("Families.Relationship"), Loc.T("Common.Phone"), Loc.T("Families.Job")],
            guardians.Select(g => new[] { g.FullName, Loc.Enum(g.Relationship), g.Phone ?? "", g.Job ?? "" })));
        if (!string.IsNullOrWhiteSpace(child.Family?.Address))
            doc.Blocks.Add(Text($"{Loc.T("Common.Address")}: {child.Family.Address}", 12, Ink));

        doc.Blocks.Add(Section(Loc.T("Children.Pickups")));
        var pickups = guardians.Where(g => g.CanPickUp).Select(g => new[] { g.FullName, Loc.Enum(g.Relationship), g.Phone ?? "" })
            .Concat(child.AuthorizedPickups.Where(p => p.CanPickUp).Select(p => new[] { p.FullName, p.RelationshipNote ?? "", p.Phone ?? "" }));
        doc.Blocks.Add(Grid([Loc.T("Common.Name"), Loc.T("Families.Relationship"), Loc.T("Common.Phone")], pickups));

        doc.Blocks.Add(Text(Loc.T("Print.EnrolmentDeclaration"), 11, Grey));
        doc.Blocks.Add(Signature(Loc.T("Print.GuardianSignature"), Loc.T("Print.ManagementSignature")));
        return doc;
    }

    public static FlowDocument AttendanceSheet(MonthlySheet sheet, CrecheSettings s)
    {
        var doc = NewDocument();
        Header(doc, s, false);
        doc.Blocks.Add(Heading(Loc.T("Print.AttendanceSheet")));
        var month = new DateOnly(sheet.Year, sheet.Month, 1);
        doc.Blocks.Add(Text($"{sheet.ChildName} — {sheet.GroupName} — {Month(month)}", 13, Ink, TextAlignment.Center, bold: true));
        doc.Blocks.Add(Grid(
            [Loc.T("Common.Date"), Loc.T("Common.Status"), Loc.T("Attendance.CheckIn"), Loc.T("Attendance.CheckOut"), Loc.T("Attendance.Hours"), Loc.T("Attendance.Reason")],
            sheet.Days.Select(d => new[]
            {
                d.Date.ToString("ddd dd/MM", CultureInfo.CurrentCulture),
                d.Status is { } status ? Loc.Enum(status) : "",
                Clock(d.CheckIn), Clock(d.CheckOut),
                d.Hours > 0 ? d.Hours.ToString("0.##", CultureInfo.CurrentCulture) : "",
                d.AbsenceReason ?? "",
            }), rightColumns: [4], fontSize: 10.5, cellPadding: 3));
        doc.Blocks.Add(Pairs(
            (Loc.T("Attendance.PresentDays"), sheet.PresentDays.ToString(CultureInfo.CurrentCulture)),
            (Loc.T("Attendance.AbsentDays"), sheet.AbsentDays.ToString(CultureInfo.CurrentCulture)),
            (Loc.T("Attendance.Hours"), sheet.TotalHours.ToString("0.##", CultureInfo.CurrentCulture)),
            (Loc.T("Reports.AttendanceRate"), $"{sheet.AttendanceRate:0.#} %")));
        return doc;
    }

    public static FlowDocument Report(ReportData r, CrecheSettings s)
    {
        var doc = NewDocument();
        Header(doc, s, false);
        doc.Blocks.Add(Heading(Loc.T("Reports.Title")));
        doc.Blocks.Add(Text($"{Day(r.From)} → {Day(r.To)}", 12, Grey, TextAlignment.Center));

        doc.Blocks.Add(Section(Loc.T("Reports.Money")));
        doc.Blocks.Add(Pairs(
            (Loc.T("Reports.Invoiced"), Cash(r.Invoiced, s)),
            (Loc.T("Reports.Revenue"), Cash(r.Revenue, s)),
            (Loc.T("Reports.Expenses"), Cash(r.Expenses, s)),
            (Loc.T("Reports.NetProfit"), Cash(r.NetProfit, s)),
            (Loc.T("Reports.Unpaid"), Cash(r.Unpaid, s))));

        doc.Blocks.Add(Section(Loc.T("Reports.Activity")));
        doc.Blocks.Add(Pairs(
            (Loc.T("Reports.AttendanceRate"), $"{r.AttendanceRate:0.#} % ({r.PresentDays} / {r.PresentDays + r.AbsentDays})"),
            (Loc.T("Reports.Enrolments"), r.Enrolments.ToString(CultureInfo.CurrentCulture)),
            (Loc.T("Reports.Departures"), r.Departures.ToString(CultureInfo.CurrentCulture)),
            (Loc.T("Dashboard.ActiveChildren"), r.ActiveChildren.ToString(CultureInfo.CurrentCulture))));

        doc.Blocks.Add(Section(Loc.T("Reports.ChildrenPerGroup")));
        doc.Blocks.Add(Grid([Loc.T("Common.Group"), Loc.T("Nav.children"), Loc.T("Groups.Capacity")],
            r.ChildrenPerGroup.Select(g => new[] { g.GroupName, g.Children.ToString(CultureInfo.CurrentCulture), g.Capacity.ToString(CultureInfo.CurrentCulture) }), rightColumns: [1, 2]));

        doc.Blocks.Add(Section(Loc.T("Reports.ExpensesByCategory")));
        doc.Blocks.Add(Grid([Loc.T("Expenses.Category"), Loc.T("Common.Amount")],
            r.ExpensesByCategory.Select(e => new[] { Loc.Enum(e.Category), Cash(e.Amount, s) }), rightColumns: [1]));

        doc.Blocks.Add(Section(Loc.T("Reports.RevenueByMethod")));
        doc.Blocks.Add(Grid([Loc.T("Payments.Method"), Loc.T("Common.Amount")],
            r.RevenueByMethod.Select(e => new[] { Loc.Enum(e.Method), Cash(e.Amount, s) }), rightColumns: [1]));

        doc.Blocks.Add(Section(Loc.T("Payments.Debtors")));
        doc.Blocks.Add(DebtorGrid(r.Debtors, s));
        return doc;
    }

    public static FlowDocument Debtors(IReadOnlyList<DebtorItem> debtors, CrecheSettings s)
    {
        var doc = NewDocument();
        Header(doc, s, false);
        doc.Blocks.Add(Heading(Loc.T("Payments.Debtors")));
        doc.Blocks.Add(Text(Day(DateOnly.FromDateTime(DateTime.Today)), 12, Grey, TextAlignment.Center));
        doc.Blocks.Add(DebtorGrid(debtors, s));
        doc.Blocks.Add(Totals(s, (Loc.T("Common.Total"), debtors.Sum(d => d.AmountDue), true)));
        return doc;
    }

    private static Block DebtorGrid(IReadOnlyList<DebtorItem> debtors, CrecheSettings s)
        => Grid([Loc.T("Common.Family"), Loc.T("Common.Phone"), Loc.T("Payments.Since"), Loc.T("Payments.DaysLate"), Loc.T("Payments.AmountDue")],
            debtors.Select(d => new[] { d.FamilyName, d.Phone ?? "", Day(d.OldestDueDate), d.DaysLate.ToString(CultureInfo.CurrentCulture), Cash(d.AmountDue, s) }),
            rightColumns: [3, 4]);

    // ───────────── Building blocks ─────────────

    private static FlowDocument NewDocument(bool a5 = false) => new()
    {
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 12,
        Foreground = Ink,
        Background = Brushes.White,
        PageWidth = a5 ? 559 : 794,
        PageHeight = a5 ? 794 : 1123,
        PagePadding = new Thickness(a5 ? 30 : 52),
        ColumnWidth = 2000,
        FlowDirection = Loc.Instance.FlowDirection,
        TextAlignment = TextAlignment.Left,
    };

    private static void Header(FlowDocument doc, CrecheSettings s, bool showLogo)
    {
        if (showLogo && ImageStore.Load(s.LogoFile, 240) is { } logo)
            doc.Blocks.Add(new BlockUIContainer(new Image { Source = logo, Height = 56, HorizontalAlignment = HorizontalAlignment.Center }) { Margin = new Thickness(0, 0, 0, 4) });
        doc.Blocks.Add(Text(s.Name, 18, Ink, TextAlignment.Center, bold: true));
        string contact = string.Join("  ·  ", new[] { s.Address, s.Phone }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (contact.Length > 0) doc.Blocks.Add(Text(contact, 11, Grey, TextAlignment.Center));
        doc.Blocks.Add(new Paragraph { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 6, 0, 10), FontSize = 1 });
    }

    private static Paragraph Text(string text, double size, Brush brush, TextAlignment align = TextAlignment.Left, bool bold = false) => new(new Run(text))
    {
        FontSize = size,
        Foreground = brush,
        TextAlignment = align,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        Margin = new Thickness(0, 2, 0, 6),
    };

    private static Paragraph Heading(string text) => new(new Run(text))
    {
        FontSize = 17, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 10),
    };

    private static Paragraph Section(string text) => new(new Run(text))
    {
        FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4),
        BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 2),
    };

    private static TableCell Cell(string text, bool bold = false, bool right = false, Brush? background = null, double padding = 4) => new(new Paragraph(new Run(text)) { Margin = new Thickness(0) })
    {
        Padding = new Thickness(6, padding, 6, padding),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        Background = background,
        BorderBrush = Line,
        BorderThickness = new Thickness(0, 0, 0, 0.5),
    };

    /// <summary>Label / value pairs laid out two per row.</summary>
    private static Table Pairs(params (string Label, string Value)[] pairs)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 8) };
        for (int i = 0; i < 4; i++) table.Columns.Add(new TableColumn { Width = new GridLength(i % 2 == 0 ? 0.8 : 1.2, GridUnitType.Star) });
        var group = new TableRowGroup();
        var visible = pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToList();
        for (int i = 0; i < visible.Count; i += 2)
        {
            var row = new TableRow();
            for (int j = i; j < i + 2; j++)
            {
                bool has = j < visible.Count;
                row.Cells.Add(new TableCell(new Paragraph(new Run(has ? visible[j].Label : "")) { Margin = new Thickness(0), Foreground = Grey }) { Padding = new Thickness(0, 3, 6, 3) });
                row.Cells.Add(new TableCell(new Paragraph(new Run(has ? visible[j].Value : "")) { Margin = new Thickness(0), FontWeight = FontWeights.SemiBold }) { Padding = new Thickness(0, 3, 10, 3) });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static Table Grid(string[] headers, IEnumerable<string[]> rows, int[]? rightColumns = null, double fontSize = 11.5, double cellPadding = 4)
    {
        rightColumns ??= [];
        var table = new Table { CellSpacing = 0, FontSize = fontSize, Margin = new Thickness(0, 4, 0, 10) };
        foreach (var _ in headers) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        var head = new TableRow();
        for (int i = 0; i < headers.Length; i++) head.Cells.Add(Cell(headers[i], bold: true, right: rightColumns.Contains(i), background: Shade, padding: cellPadding));
        group.Rows.Add(head);
        bool any = false;
        foreach (var values in rows)
        {
            any = true;
            var row = new TableRow();
            for (int i = 0; i < headers.Length; i++) row.Cells.Add(Cell(i < values.Length ? values[i] : "", right: rightColumns.Contains(i), padding: cellPadding));
            group.Rows.Add(row);
        }
        if (!any)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run("—")) { Margin = new Thickness(0), Foreground = Grey }) { ColumnSpan = headers.Length, Padding = new Thickness(6, 4, 6, 4) });
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static Table Totals(CrecheSettings s, params (string Label, decimal? Amount, bool Strong)[] lines)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
        table.Columns.Add(new TableColumn { Width = new GridLength(1.4, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        foreach (var (label, amount, strong) in lines)
        {
            if (amount is null || (!strong && amount == 0m)) continue;
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph { Margin = new Thickness(0) }));
            row.Cells.Add(Cell(label, bold: strong, right: true));
            row.Cells.Add(Cell(Cash(amount.Value, s), bold: strong, right: true));
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static Table Signature(params string[] labels)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 28, 0, 8) };
        var row = new TableRow();
        foreach (string label in labels)
        {
            table.Columns.Add(new TableColumn());
            row.Cells.Add(new TableCell(new Paragraph(new Run(label)) { Margin = new Thickness(0), Foreground = Grey, FontSize = 11 })
            {
                BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 4, 0, 30), TextAlignment = TextAlignment.Center,
            });
        }
        var group = new TableRowGroup();
        group.Rows.Add(row);
        table.RowGroups.Add(group);
        return table;
    }
}
