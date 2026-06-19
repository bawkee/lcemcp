namespace LceMcp.Tests;

internal sealed class TempWorkspace : IDisposable
{
    private TempWorkspace(string directory)
    {
        Directory = directory;
        Paths = AppPaths.FromDirectory(directory);
    }

    public string Directory { get; }
    public AppPaths Paths { get; }

    public static TempWorkspace Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lcemcp-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new(directory);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);
    }
}
