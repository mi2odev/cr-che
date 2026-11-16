using System.Globalization;
using System.Text;

namespace Creche.Application.Common;

/// <summary>Minimal RFC 4180 CSV writer (UTF-8 with BOM so Excel opens accents and Arabic correctly).</summary>
public sealed class CsvWriter(char separator = ';')
{
    private readonly StringBuilder _sb = new();

    public CsvWriter Row(params object?[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) _sb.Append(separator);
            _sb.Append(Escape(ToText(cells[i])));
        }
        _sb.Append("\r\n");
        return this;
    }

    public CsvWriter Blank()
    {
        _sb.Append("\r\n");
        return this;
    }

    private static string ToText(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private string Escape(string text)
    {
        // Neutralise spreadsheet formulas typed into free-text fields.
        if (text.Length > 0 && text[0] is '=' or '+' or '@' or '\t' or '\r'
            || (text.Length > 1 && text[0] == '-' && !char.IsDigit(text[1])))
            text = "'" + text;
        bool quote = text.Contains(separator) || text.Contains('"') || text.Contains('\n') || text.Contains('\r');
        return quote ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    public override string ToString() => _sb.ToString();

    public void Save(string path) => File.WriteAllText(path, _sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}
