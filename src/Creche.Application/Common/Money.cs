using System.Globalization;
using Creche.Domain.Rules;

namespace Creche.Application.Common;

/// <summary>Money helpers. Amounts are always <see cref="decimal"/>.</summary>
public static class Money
{
    public static decimal Round(decimal value) => FeeCalculator.Round(value);

    public static string Format(decimal amount, string currency, CultureInfo? culture = null)
    {
        string number = amount.ToString("N2", culture ?? CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(currency) ? number : $"{number} {currency}";
    }

    /// <summary>Parses user input, accepting both ',' and '.' as decimal separator.</summary>
    public static bool TryParse(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string cleaned = text.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(',', '.');
        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return false;
        amount = Round(value);
        return true;
    }
}
