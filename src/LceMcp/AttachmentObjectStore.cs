using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LceMcp;

internal sealed class AttachmentObjectStore
{
    private readonly AppPaths _paths;

    public AttachmentObjectStore(AppPaths paths)
    {
        _paths = paths;
    }

    public StoredAttachmentObject Store(byte[] content)
    {
        _paths.EnsureDataDirectories();
        Directory.CreateDirectory(ObjectsDirectory);
        Directory.CreateDirectory(TempDirectory);

        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var objectDirectory = Path.Combine(ObjectsDirectory, hash[..2]);
        var objectPath = Path.Combine(objectDirectory, hash);
        var storageKey = $"objects/{hash[..2]}/{hash}";

        if (File.Exists(objectPath))
            return new(storageKey, hash, content.LongLength);

        Directory.CreateDirectory(objectDirectory);
        var tempPath = Path.Combine(TempDirectory, $"{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tempPath, content);

        if (!File.Exists(objectPath))
            File.Move(tempPath, objectPath);
        else
            File.Delete(tempPath);

        return new(storageKey, hash, content.LongLength);
    }

    public string PrepareManagedExport(StoredAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.StorageKey))
            return null;

        var sourcePath = ResolveStoragePath(attachment.StorageKey);
        if (!File.Exists(sourcePath))
            return null;

        var exportDirectory = Path.Combine(ExportsDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportDirectory);

        var filename = SanitizeFilename(attachment.Filename ?? attachment.DisplayPath ?? $"attachment-{attachment.AttachmentId}");
        var targetPath = Path.Combine(exportDirectory, filename);
        File.Copy(sourcePath, targetPath, overwrite: false);
        return targetPath;
    }

    private string ResolveStoragePath(string storageKey)
    {
        var normalized = storageKey.Replace('\\', '/');
        if (!StorageKeyRegex.IsMatch(normalized))
            throw new CliException("Attachment storage key is invalid.", 2);

        return Path.Combine(_paths.AttachmentsDirectory, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string SanitizeFilename(string value)
    {
        var filename = Path.GetFileName(value.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(filename))
            filename = "attachment";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            filename = filename.Replace(invalid, '_');

        filename = filename.Trim();
        if (filename.Length > 160)
        {
            var extension = Path.GetExtension(filename);
            var stemLength = Math.Max(1, 160 - extension.Length);
            filename = filename[..Math.Min(filename.Length, stemLength)] + extension;
        }

        return string.IsNullOrWhiteSpace(filename) ? "attachment" : filename;
    }

    private string ObjectsDirectory => Path.Combine(_paths.AttachmentsDirectory, "objects");
    private string TempDirectory => Path.Combine(_paths.AttachmentsDirectory, "tmp");
    private string ExportsDirectory => Path.Combine(_paths.AttachmentsDirectory, "exports");

    private static readonly Regex StorageKeyRegex = new(
        "^objects/[a-f0-9]{2}/[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
