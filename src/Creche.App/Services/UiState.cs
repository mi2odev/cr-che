using System.Globalization;
using System.Text.Json;
using Creche.Application.Common;
using Microsoft.Extensions.Logging;

namespace Creche.App.Services;

/// <summary>Remembers filters, sort orders, view choices, theme and language in ui-state.json (System.Text.Json).</summary>
public sealed class UiState(IAppPaths paths, ILogger<UiState> log)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private Dictionary<string, string> _values = Load(paths.UiStatePath, log);

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public string Get(string key, string fallback) => _values.GetValueOrDefault(key, fallback);

    public bool GetBool(string key, bool fallback = false) => _values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public int? GetInt(string key) => _values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    public double? GetDouble(string key) => _values.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public T? GetEnum<T>(string key) where T : struct, Enum => _values.TryGetValue(key, out var v) && Enum.TryParse<T>(v, out var e) ? e : null;

    public void Set(string key, object? value)
    {
        string? text = value switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        if (text is null)
        {
            if (!_values.Remove(key)) return;
        }
        else
        {
            if (_values.TryGetValue(key, out var existing) && existing == text) return;
            _values[key] = text;
        }
        Save();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(paths.UiStatePath, JsonSerializer.Serialize(_values, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not save UI state");
        }
    }

    private static Dictionary<string, string> Load(string path, ILogger log)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "ui-state.json is unreadable; starting with defaults");
        }
        return [];
    }
}
