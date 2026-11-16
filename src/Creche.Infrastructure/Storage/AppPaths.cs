using Creche.Application.Common;
using Creche.Application.Settings;

namespace Creche.Infrastructure.Storage;

public sealed class AppPaths : IAppPaths
{
    public AppPaths(StorageOptions options)
    {
        DataFolder = string.IsNullOrWhiteSpace(options.DataFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName)
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.DataFolder));
        foreach (var folder in new[] { DataFolder, ImagesFolder, DocumentsFolder, BackupsFolder, LogsFolder })
            Directory.CreateDirectory(folder);
    }

    public string DataFolder { get; }
    public string DatabasePath => Path.Combine(DataFolder, "creche.db");
    public string ImagesFolder => Path.Combine(DataFolder, "images");
    public string DocumentsFolder => Path.Combine(DataFolder, "documents");
    public string BackupsFolder => Path.Combine(DataFolder, "backups");
    public string LogsFolder => Path.Combine(DataFolder, "logs");
    public string UiStatePath => Path.Combine(DataFolder, "ui-state.json");
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}
