using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CFRezManager;

internal static class ModelTextureIndexDiskCache
{
    private const int DatCacheVersion = 1;
    private const int CfgCacheVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly string CacheDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "ModelTextureIndexCache",
        "v1");

    public static bool TryLoadDatIndex(
        ExplorerItem root,
        out List<DatTextureIndexEntryModel> entries)
    {
        entries = [];

        try
        {
            string? fingerprint = ComputeFingerprint(root, ".dat");
            if (fingerprint is null)
            {
                return false;
            }

            string cachePath = GetCachePath("dat", fingerprint);
            if (!File.Exists(cachePath))
            {
                return false;
            }

            byte[] json = File.ReadAllBytes(cachePath);
            DatIndexCacheFile? cacheFile = JsonSerializer.Deserialize<DatIndexCacheFile>(json, JsonOptions);
            if (cacheFile is null ||
                cacheFile.Version != DatCacheVersion ||
                !string.Equals(cacheFile.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                cacheFile.Items is null)
            {
                return false;
            }

            entries = cacheFile.Items;
            return true;
        }
        catch
        {
            entries = [];
            return false;
        }
    }

    public static void TrySaveDatIndex(
        ExplorerItem root,
        IReadOnlyList<DatTextureIndexEntryModel> entries)
    {
        try
        {
            string? fingerprint = ComputeFingerprint(root, ".dat");
            if (fingerprint is null)
            {
                return;
            }

            var cacheFile = new DatIndexCacheFile
            {
                Version = DatCacheVersion,
                Fingerprint = fingerprint,
                Items = entries.ToList()
            };

            WriteCacheFile(GetCachePath("dat", fingerprint), cacheFile);
        }
        catch
        {
        }
    }

    public static bool TryLoadCfgIndex(
        ExplorerItem root,
        out Dictionary<string, List<string>> configTextures,
        out Dictionary<string, List<string>> modelTextureByName)
    {
        configTextures = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        modelTextureByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string? fingerprint = ComputeFingerprint(root, ".cfg", ".ini", ".txt");
            if (fingerprint is null)
            {
                return false;
            }

            string cachePath = GetCachePath("cfg", fingerprint);
            if (!File.Exists(cachePath))
            {
                return false;
            }

            byte[] json = File.ReadAllBytes(cachePath);
            CfgIndexCacheFile? cacheFile = JsonSerializer.Deserialize<CfgIndexCacheFile>(json, JsonOptions);
            if (cacheFile is null ||
                cacheFile.Version != CfgCacheVersion ||
                !string.Equals(cacheFile.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                cacheFile.ConfigTextures is null ||
                cacheFile.ModelTextureByName is null)
            {
                return false;
            }

            configTextures = new Dictionary<string, List<string>>(cacheFile.ConfigTextures, StringComparer.OrdinalIgnoreCase);
            modelTextureByName = new Dictionary<string, List<string>>(cacheFile.ModelTextureByName, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            configTextures = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            modelTextureByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    public static void TrySaveCfgIndex(
        ExplorerItem root,
        IReadOnlyDictionary<string, IReadOnlyList<string>> configTextures,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelTextureByName)
    {
        try
        {
            string? fingerprint = ComputeFingerprint(root, ".cfg", ".ini", ".txt");
            if (fingerprint is null)
            {
                return;
            }

            var cacheFile = new CfgIndexCacheFile
            {
                Version = CfgCacheVersion,
                Fingerprint = fingerprint,
                ConfigTextures = configTextures.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase),
                ModelTextureByName = modelTextureByName.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase)
            };

            WriteCacheFile(GetCachePath("cfg", fingerprint), cacheFile);
        }
        catch
        {
        }
    }

    private static void WriteCacheFile<T>(string cachePath, T cacheFile)
    {
        Directory.CreateDirectory(CacheDirectory);
        string tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(cacheFile, JsonOptions);
        File.WriteAllBytes(tempPath, json);
        File.Move(tempPath, cachePath, overwrite: true);
    }

    private static string GetCachePath(string kind, string fingerprint)
    {
        return Path.Combine(CacheDirectory, kind + "-" + fingerprint + ".json");
    }

    private static string? ComputeFingerprint(ExplorerItem root, params string[] extensions)
    {
        var builder = new StringBuilder();
        var seenArchives = new HashSet<RezArchive>();
        if (!AppendFingerprintEntries(root, extensions, builder, seenArchives))
        {
            return null;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool AppendFingerprintEntries(
        ExplorerItem item,
        string[] extensions,
        StringBuilder builder,
        HashSet<RezArchive> seenArchives)
    {
        if (item.IsFile)
        {
            if (MatchesExtension(item, extensions))
            {
                if (!TryAppendFileEntry(item, builder))
                {
                    return false;
                }
            }
        }

        if (item.Archive is not null && seenArchives.Add(item.Archive))
        {
            if (!TryAppendArchiveEntry(item.Archive, builder))
            {
                return false;
            }
        }

        foreach (ExplorerItem child in item.Children)
        {
            if (!AppendFingerprintEntries(child, extensions, builder, seenArchives))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesExtension(ExplorerItem item, string[] extensions)
    {
        string extension = "." + item.FileExtension.TrimStart('.');
        return extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryAppendFileEntry(ExplorerItem item, StringBuilder builder)
    {
        try
        {
            if (item.Kind == ExplorerItemKind.RezFile)
            {
                if (item.ArchiveFile is null)
                {
                    return false;
                }

                builder.Append("rez|")
                    .Append(item.OutputRelativePath)
                    .Append('|')
                    .Append(item.ArchiveFile.Size)
                    .Append('|')
                    .Append(item.ArchiveFile.Time)
                    .Append('\n');
                return true;
            }

            if (item.Kind == ExplorerItemKind.LocalFile)
            {
                var info = new FileInfo(item.SourcePath);
                if (!info.Exists)
                {
                    return false;
                }

                builder.Append("local|")
                    .Append(info.FullName.ToUpperInvariant())
                    .Append('|')
                    .Append(info.Length)
                    .Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks)
                    .Append('\n');
                return true;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool TryAppendArchiveEntry(RezArchive archive, StringBuilder builder)
    {
        try
        {
            var info = new FileInfo(archive.FilePath);
            if (!info.Exists)
            {
                return false;
            }

            builder.Append("archive|")
                .Append(info.FullName.ToUpperInvariant())
                .Append('|')
                .Append(info.Length)
                .Append('|')
                .Append(info.LastWriteTimeUtc.Ticks)
                .Append('\n');
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal sealed class DatTextureIndexEntryModel
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> Textures { get; set; } = [];
    }

    private sealed class DatIndexCacheFile
    {
        public int Version { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public List<DatTextureIndexEntryModel>? Items { get; set; }
    }

    private sealed class CfgIndexCacheFile
    {
        public int Version { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public Dictionary<string, List<string>>? ConfigTextures { get; set; }
        public Dictionary<string, List<string>>? ModelTextureByName { get; set; }
    }
}
