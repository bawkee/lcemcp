namespace LceMcp;

internal sealed class AppPaths
{
    public string ConfigDirectory { get; }
    public string ConfigPath => Path.Combine(ConfigDirectory, "config.toml");

    private AppPaths(string configDirectory)
    {
        ConfigDirectory = configDirectory;
    }

    public static AppPaths FromEnvironment()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("LCEMCP_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return new AppPaths(Path.GetFullPath(overrideDirectory));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.CurrentDirectory;

        return new AppPaths(Path.Combine(appData, "lcemcp"));
    }
}
