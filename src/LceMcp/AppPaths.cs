namespace LceMcp;

internal sealed class AppPaths
{
    public string ConfigDirectory { get; }
    public string ConfigPath => Path.Combine(ConfigDirectory, "config.toml");
    public string DatabasePath => Path.Combine(ConfigDirectory, "email.db");
    public string AttachmentsDirectory => Path.Combine(ConfigDirectory, "attachments");
    public string LogsDirectory => Path.Combine(ConfigDirectory, "logs");
    public string OcrDirectory => Path.Combine(ConfigDirectory, "ocr");
    public string TessdataDirectory => Path.Combine(OcrDirectory, "tessdata");

    private AppPaths(string configDirectory)
    {
        ConfigDirectory = configDirectory;
    }

    public void EnsureDataDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TessdataDirectory);
    }

    public static AppPaths FromEnvironment()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("LCEMCP_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return FromDirectory(overrideDirectory);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.CurrentDirectory;

        return FromDirectory(Path.Combine(appData, "lcemcp"));
    }

    public static AppPaths FromDirectory(string configDirectory) =>
        new(Path.GetFullPath(configDirectory));
}
