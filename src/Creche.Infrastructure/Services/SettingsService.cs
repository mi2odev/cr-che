using System.Text.Json;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Application.Settings;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public sealed class SettingsService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user) : ServiceBase(factory, user), ISettingsService
{
    private const string Key = "creche";
    private CrecheSettings? _cache;

    public async Task<CrecheSettings> GetAsync()
    {
        if (_cache is not null) return Clone(_cache);
        await using var db = await OpenAsync();
        var entry = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key);
        _cache = entry is null ? new CrecheSettings() : JsonSerializer.Deserialize<CrecheSettings>(entry.Value) ?? new CrecheSettings();
        return Clone(_cache);
    }

    public async Task SaveAsync(CrecheSettings settings)
    {
        User.Demand(Permission.Settings);
        settings.Name = Required(settings.Name, "Name");
        settings.Currency = (settings.Currency ?? "").Trim();
        settings.SchoolYear = Required(settings.SchoolYear, "SchoolYear");
        if (settings.ClosingTime <= settings.OpeningTime)
            throw new BusinessRuleException("OpeningHoursInvalid", "Closing time must be after opening time.");
        if (settings.SecondChildDiscountPercent is < 0m or > 100m || settings.ThirdChildDiscountPercent is < 0m or > 100m)
            throw new BusinessRuleException("DiscountInvalid", "A discount must be between 0 and 100 %.");
        if (settings.RegistrationFee < 0m || settings.ReEnrolmentFee < 0m || settings.LatePickupFeePerBlock < 0m)
            throw new BusinessRuleException("AmountNegative", "Amounts cannot be negative.");
        settings.PaymentDueDay = Math.Clamp(settings.PaymentDueDay, 1, 28);
        settings.LatePickupGraceMinutes = Math.Max(0, settings.LatePickupGraceMinutes);
        settings.LatePickupBlockMinutes = Math.Max(1, settings.LatePickupBlockMinutes);
        settings.BackupRetentionCount = Math.Clamp(settings.BackupRetentionCount, 1, 365);

        await using var db = await OpenAsync();
        string json = JsonSerializer.Serialize(settings);
        var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key);
        if (entry is null) db.Settings.Add(new SettingEntry { Key = Key, Value = json });
        else entry.Value = json;
        await db.SaveChangesAsync();
        _cache = Clone(settings);
    }

    /// <summary>Forget the cached copy (after a restore replaced the database).</summary>
    public void Invalidate() => _cache = null;

    private static CrecheSettings Clone(CrecheSettings s) => JsonSerializer.Deserialize<CrecheSettings>(JsonSerializer.Serialize(s))!;
}
