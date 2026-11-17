using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Creche.App.Services;
using Creche.Application.Common;
using Creche.Domain.Enums;

namespace Creche.App.Converters;

/// <summary>Base for one-way converters; always formats with the application's current culture.</summary>
public abstract class OneWay : IValueConverter
{
    public abstract object? Convert(object? value, object? parameter);
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Convert(value, parameter);
    public virtual object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>decimal → "12 500,00 DA".</summary>
public sealed class MoneyConverter : OneWay
{
    public static string Currency { get; set; } = "DA";
    public override object? Convert(object? value, object? parameter) => value is decimal d ? Money.Format(d, Currency) : "";
}

/// <summary>Two-way decimal ↔ text for amount inputs; accepts ',' or '.'.</summary>
public sealed class DecimalConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is decimal d ? d.ToString("0.##", CultureInfo.CurrentCulture) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = (value as string ?? "").Trim();
        bool nullable = Nullable.GetUnderlyingType(targetType) is not null;
        if (text.Length == 0) return nullable ? null : 0m;
        return Money.TryParse(text, out var amount) ? amount : DependencyProperty.UnsetValue;
    }
}

/// <summary>Two-way int ↔ text.</summary>
public sealed class IntConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() ?? "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => int.TryParse((value as string ?? "").Trim(), out int number) ? number : DependencyProperty.UnsetValue;
}

/// <summary>DateOnly / DateTime → short date; parameter "month" → "November 2026", "long" → long date.</summary>
public sealed class DateConverter : OneWay
{
    public override object? Convert(object? value, object? parameter)
    {
        DateTime? date = value switch
        {
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            DateTime d => d,
            _ => null,
        };
        if (date is null) return "";
        return (parameter as string) switch
        {
            "month" => date.Value.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            "long" => date.Value.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture),
            "datetime" => date.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture),
            "day" => date.Value.ToString("ddd dd", CultureInfo.CurrentCulture),
            _ => date.Value.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture),
        };
    }
}

public sealed class TimeConverter : OneWay
{
    public override object? Convert(object? value, object? parameter) => value is TimeOnly t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
}

/// <summary>Enum value → translated label ("Enum.{Type}.{Value}").</summary>
public sealed class EnumConverter : OneWay
{
    public override object? Convert(object? value, object? parameter) => value is Enum e ? Loc.EnumObject(e) : "";
}

/// <summary>Truthiness → Visibility. Parameter "invert" flips it. Null, false, 0, "" and empty collections are false.</summary>
public sealed class VisibleConverter : OneWay
{
    public override object? Convert(object? value, object? parameter)
    {
        bool truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            decimal d => d != 0m,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "invert") truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>value == parameter (as text) → bool. Used by radio buttons and tabs; ConvertBack returns the parameter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type.IsEnum) return Enum.Parse(type, parameter.ToString()!);
        return System.Convert.ChangeType(parameter, type, CultureInfo.InvariantCulture);
    }
}

/// <summary>value == parameter → Visible.</summary>
public sealed class EqualsVisibleConverter : OneWay
{
    public override object? Convert(object? value, object? parameter)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Status values → theme brush. Parameter "soft" picks the translucent variant for chip backgrounds.</summary>
public sealed class StatusBrushConverter : OneWay
{
    public override object? Convert(object? value, object? parameter)
    {
        string tone = value switch
        {
            InvoiceStatus.Paid or ChildStatus.Active or AttendanceStatus.Present or StaffAttendanceStatus.Present or ToastKind.Success => "Success",
            InvoiceStatus.PartiallyPaid or ChildStatus.WaitingList or StaffAttendanceStatus.Leave or ToastKind.Warning => "Warning",
            InvoiceStatus.Unpaid or AttendanceStatus.Absent or StaffAttendanceStatus.Absent or ToastKind.Error => "Danger",
            ToastKind.Info => "Info",
            true => "Danger",
            false => "Success",
            _ => "TextMuted",
        };
        if (parameter as string == "soft") tone = tone == "TextMuted" ? "SurfaceAlt" : tone + "Soft";
        return System.Windows.Application.Current.TryFindResource(tone) as Brush ?? Brushes.Gray;
    }
}

/// <summary>Date of birth → "2 y 4 m".</summary>
public sealed class AgeConverter : OneWay
{
    public override object? Convert(object? value, object? parameter)
    {
        if (value is not DateOnly birth) return "";
        int months = Creche.Domain.Rules.GroupRules.AgeInMonths(birth, DateOnly.FromDateTime(DateTime.Today));
        return months < 24 ? Loc.T("Age.Months", months) : Loc.T("Age.Years", months / 12, months % 12);
    }
}

/// <summary>Formats a translated template: parameter is the key, value the single argument.</summary>
public sealed class FormatConverter : OneWay
{
    public override object? Convert(object? value, object? parameter) => parameter is string key ? Loc.T(key, value) : value?.ToString();
}

/// <summary>Two-way DateOnly / DateOnly? ↔ DateTime? for DatePicker.SelectedDate.</summary>
public sealed class DateOnlyPickerConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime d) return DateOnly.FromDateTime(d);
        return Nullable.GetUnderlyingType(targetType) is not null ? null : Binding.DoNothing;
    }
}

/// <summary>Tone name ("Danger", "Warning", "Info", "Success") → theme brush; parameter "soft" for backgrounds.</summary>
public sealed class ToneBrushConverter : OneWay
{
    public override object? Convert(object? value, object? parameter)
    {
        string key = (value as string ?? "Info") + (parameter as string == "soft" ? "Soft" : "");
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}
