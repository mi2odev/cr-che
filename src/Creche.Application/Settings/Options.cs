using Creche.Domain.Rules;

namespace Creche.Application.Settings;

public static class AppInfo
{
    /// <summary>Display name of the product.</summary>
    public const string Name = "Creche Manager";
    /// <summary>Folder name used under %LOCALAPPDATA%.</summary>
    public const string FolderName = "CrecheManager";
}

/// <summary>"Storage" section of appsettings.json.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Overrides the default %LOCALAPPDATA%\CrecheManager folder when set.</summary>
    public string? DataFolder { get; set; }
}

/// <summary>"Database" section of appsettings.json.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public bool SeedDemoData { get; set; }
}

/// <summary>Settings edited inside the application and stored in the database.</summary>
public sealed class CrecheSettings
{
    public string Name { get; set; } = "My Crèche";
    public string? LogoFile { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string Currency { get; set; } = "DA";
    public string SchoolYear { get; set; } = DefaultSchoolYear(DateTime.Today);
    public TimeOnly OpeningTime { get; set; } = new(7, 30);
    public TimeOnly ClosingTime { get; set; } = new(17, 0);

    public bool LatePickupEnabled { get; set; } = true;
    public int LatePickupGraceMinutes { get; set; } = 10;
    public int LatePickupBlockMinutes { get; set; } = 15;
    public decimal LatePickupFeePerBlock { get; set; } = 200m;

    public decimal SecondChildDiscountPercent { get; set; } = 10m;
    public decimal ThirdChildDiscountPercent { get; set; } = 20m;
    public decimal RegistrationFee { get; set; } = 5000m;
    public decimal ReEnrolmentFee { get; set; } = 3000m;
    /// <summary>Day of the month by which monthly invoices must be paid.</summary>
    public int PaymentDueDay { get; set; } = 10;

    /// <summary>true: refuse to add a child to a full group. false: warn and let the user confirm.</summary>
    public bool BlockWhenGroupFull { get; set; }

    public string? ReceiptHeader { get; set; }
    public string? ReceiptFooter { get; set; } = "Merci / Thank you";
    public bool ReceiptShowLogo { get; set; } = true;
    public bool ReceiptShowBalance { get; set; } = true;
    /// <summary>"A5" or "A4".</summary>
    public string ReceiptPaper { get; set; } = "A5";

    public bool AutoBackupEnabled { get; set; } = true;
    public int BackupRetentionCount { get; set; } = 14;

    /// <summary>
    /// Minutes without keyboard or mouse activity before the application locks itself and returns to the sign-in
    /// screen, so the next person at the shared PC cannot use the previous account. 0 turns it off.
    /// </summary>
    public int AutoLockMinutes { get; set; } = DefaultAutoLockMinutes;

    public const int DefaultAutoLockMinutes = 10;
    public const int MaxAutoLockMinutes = 240;

    public LatePickupRule LatePickupRule => new(LatePickupEnabled, LatePickupGraceMinutes, LatePickupBlockMinutes, LatePickupFeePerBlock);

    /// <summary>School years start in September: 2026-11 → "2026/2027", 2027-03 → "2026/2027".</summary>
    public static string DefaultSchoolYear(DateTime date)
    {
        int start = date.Month >= 9 ? date.Year : date.Year - 1;
        return $"{start}/{start + 1}";
    }
}
