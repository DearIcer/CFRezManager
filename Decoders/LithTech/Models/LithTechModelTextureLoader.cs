using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class LithTechModelTextureLoader
{
    private const int MaxTextureBytes = 64 * 1024 * 1024;
    private const int MaxLocalTextureSearchParentDepth = 5;
    private const int MaxLocalTextureSearchVisitedFiles = 200_000;
    private static readonly string[] TextureExtensions = [".dtx", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".bin"];
    private static readonly ConditionalWeakTable<RezArchive, ArchiveTextureIndex> ArchiveTextureIndexCache = new();
    private static readonly ConditionalWeakTable<ExplorerItem, TextureIndex> GlobalTextureIndexCache = new();

    public static Func<string, ImageSource?>? CreateResolver(ExplorerItem item)
    {
        return item.Kind switch
        {
            ExplorerItemKind.RezFile when item.Archive is not null => CreateArchiveResolver(item.Archive),
            ExplorerItemKind.LocalFile when !string.IsNullOrWhiteSpace(item.SourcePath) => CreateLocalResolver(item.SourcePath),
            _ => null
        };
    }

    public static Func<string, ImageSource?>? CreateLocalResolver(string modelFilePath)
    {
        string? modelDirectory = Path.GetDirectoryName(modelFilePath);
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            return null;
        }

        var cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        var fileNameFallbackCache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        return texturePath =>
        {
            string normalized = NormalizeTexturePath(texturePath);
            if (cache.TryGetValue(normalized, out ImageSource? cached))
            {
                return cached;
            }

            ImageSource? image = TryResolveLocalTexture(modelDirectory, normalized, fileNameFallbackCache);
            cache[normalized] = image;
            return image;
        };
    }

    public static Func<string, ImageSource?>? CreateGlobalResolver(ExplorerItem root)
    {
        TextureIndex textureIndex = GlobalTextureIndexCache.GetValue(root, BuildTextureIndex);
        if (textureIndex.IsEmpty)
        {
            return null;
        }

        var cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        return texturePath =>
        {
            string normalized = NormalizeTexturePath(texturePath);
            if (cache.TryGetValue(normalized, out ImageSource? cached))
            {
                return cached;
            }

            ImageSource? image = TryResolveIndexedTexture(textureIndex, normalized);
            cache[normalized] = image;
            return image;
        };
    }

    private static Func<string, ImageSource?> CreateArchiveResolver(RezArchive archive)
    {
        ArchiveTextureIndex textureIndex = GetArchiveTextureIndex(archive);
        var cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        return texturePath =>
        {
            string normalized = NormalizeTexturePath(texturePath);
            if (cache.TryGetValue(normalized, out ImageSource? cached))
            {
                return cached;
            }

            ImageSource? image = TryResolveArchiveTexture(archive, textureIndex, normalized);
            cache[normalized] = image;
            return image;
        };
    }

    private static ImageSource? TryResolveArchiveTexture(
        RezArchive archive,
        ArchiveTextureIndex textureIndex,
        string texturePath)
    {
        foreach (string candidate in GetTexturePathCandidates(texturePath))
        {
            string normalizedCandidate = NormalizeTexturePath(candidate);
            if (textureIndex.FilesByPath.TryGetValue(normalizedCandidate, out RezFileNode? file))
            {
                return TryReadArchiveTexture(archive, textureIndex, file);
            }

            if (textureIndex.UniqueFilesBySuffix.TryGetValue(normalizedCandidate, out file) &&
                file is not null)
            {
                return TryReadArchiveTexture(archive, textureIndex, file);
            }
        }

        foreach (string candidateName in GetTextureFileNameCandidates(texturePath))
        {
            if (textureIndex.UniqueFilesByName.TryGetValue(candidateName, out RezFileNode? match) &&
                match is not null)
            {
                return TryReadArchiveTexture(archive, textureIndex, match);
            }
        }

        return null;
    }

    private static ImageSource? TryResolveIndexedTexture(TextureIndex textureIndex, string texturePath)
    {
        foreach (string candidate in GetTexturePathCandidates(texturePath))
        {
            string normalizedCandidate = NormalizeTexturePath(candidate);
            if (textureIndex.FilesByPath.TryGetValue(normalizedCandidate, out ExplorerItem? exactMatch))
            {
                return TryReadTextureItem(textureIndex, exactMatch);
            }

            if (textureIndex.UniqueFilesBySuffix.TryGetValue(normalizedCandidate, out ExplorerItem? suffixMatch) &&
                suffixMatch is not null)
            {
                return TryReadTextureItem(textureIndex, suffixMatch);
            }
        }

        foreach (string candidateName in GetTextureFileNameCandidates(texturePath))
        {
            if (textureIndex.UniqueFilesByName.TryGetValue(candidateName, out ExplorerItem? item) &&
                item is not null)
            {
                return TryReadTextureItem(textureIndex, item);
            }
        }

        return null;
    }

    private static ImageSource? TryResolveLocalTexture(
        string modelDirectory,
        string texturePath,
        Dictionary<string, ImageSource?> fileNameFallbackCache)
    {
        foreach (string candidate in GetLocalTexturePathCandidates(modelDirectory, texturePath))
        {
            if (File.Exists(candidate))
            {
                return TryReadLocalTexture(candidate);
            }
        }

        foreach (string candidateName in GetTextureFileNameCandidates(texturePath))
        {
            if (fileNameFallbackCache.TryGetValue(candidateName, out ImageSource? cached))
            {
                if (cached is not null)
                {
                    return cached;
                }

                continue;
            }

            ImageSource? image = null;
            string? match = TryFindUniqueLocalTextureByFileName(modelDirectory, candidateName);
            if (match is not null)
            {
                image = TryReadLocalTexture(match);
            }

            fileNameFallbackCache[candidateName] = image;
            if (image is not null)
            {
                return image;
            }
        }

        return null;
    }

    private static string? TryFindUniqueLocalTextureByFileName(string modelDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (string root in GetLocalTextureSearchRoots(modelDirectory))
        {
            string? match = TryFindUniqueFileByName(root, fileName);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetLocalTextureSearchRoots(string modelDirectory)
    {
        string? current = modelDirectory;
        for (int depth = 0;
             depth < MaxLocalTextureSearchParentDepth && !string.IsNullOrWhiteSpace(current);
             depth++)
        {
            yield return current;

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null || parent.Parent is null)
            {
                break;
            }

            current = parent.FullName;
        }
    }

    private static string? TryFindUniqueFileByName(string root, string fileName)
    {
        string? match = null;
        int visitedFiles = 0;
        foreach (string filePath in SafeEnumerateFilesRecursive(root))
        {
            visitedFiles++;
            if (visitedFiles > MaxLocalTextureSearchVisitedFiles)
            {
                return null;
            }

            if (!string.Equals(Path.GetFileName(filePath), fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = filePath;
        }

        return match;
    }

    private static IEnumerable<string> SafeEnumerateFilesRecursive(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch
            {
                files = [];
            }

            foreach (string file in files)
            {
                yield return file;
            }

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).ToList();
            }
            catch
            {
                childDirectories = [];
            }

            foreach (string childDirectory in childDirectories)
            {
                pending.Push(childDirectory);
            }
        }
    }

    private static IEnumerable<string> GetLocalTexturePathCandidates(string modelDirectory, string texturePath)
    {
        foreach (string candidate in GetTexturePathCandidates(texturePath))
        {
            string platformPath = candidate.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathFullyQualified(platformPath))
            {
                yield return platformPath;
                continue;
            }

            for (string? directory = modelDirectory; !string.IsNullOrWhiteSpace(directory); directory = Directory.GetParent(directory)?.FullName)
            {
                yield return Path.Combine(directory, platformPath);
            }
        }
    }

    private static IEnumerable<string> GetTexturePathCandidates(string texturePath)
    {
        string normalized = NormalizeTexturePath(texturePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return normalized;

        string extension = Path.GetExtension(normalized);
        if (string.IsNullOrWhiteSpace(extension))
        {
            foreach (string textureExtension in TextureExtensions)
            {
                yield return normalized + textureExtension;
            }
        }
        else
        {
            string withoutExtension = normalized[..^extension.Length];
            foreach (string textureExtension in TextureExtensions)
            {
                if (!string.Equals(extension, textureExtension, StringComparison.OrdinalIgnoreCase))
                {
                    yield return withoutExtension + textureExtension;
                }
            }
        }
    }

    private static IEnumerable<string> GetTextureFileNameCandidates(string texturePath)
    {
        foreach (string candidate in GetTexturePathCandidates(texturePath))
        {
            string fileName = Path.GetFileName(candidate);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                yield return fileName;
            }
        }
    }

    private static ImageSource? TryReadArchiveTexture(RezArchive archive, RezFileNode file)
    {
        if (file.Size < 0 || file.Size > MaxTextureBytes)
        {
            return null;
        }

        try
        {
            byte[] data = new byte[file.Size];
            using Stream source = RezArchiveReader.OpenFileData(archive, file);
            source.ReadExactly(data);
            return TryDecodeTexture(file.Extension, data);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryReadArchiveTexture(
        RezArchive archive,
        ArchiveTextureIndex textureIndex,
        RezFileNode file)
    {
        lock (textureIndex.ImageCache)
        {
            if (textureIndex.ImageCache.TryGetValue(file, out ImageSource? cached))
            {
                return cached;
            }
        }

        ImageSource? image = TryReadArchiveTexture(archive, file);
        lock (textureIndex.ImageCache)
        {
            textureIndex.ImageCache[file] = image;
        }

        return image;
    }

    private static ImageSource? TryReadTextureItem(TextureIndex textureIndex, ExplorerItem item)
    {
        lock (textureIndex.ImageCache)
        {
            if (textureIndex.ImageCache.TryGetValue(item, out ImageSource? cached))
            {
                return cached;
            }
        }

        ImageSource? image = TryReadTextureItem(item);
        lock (textureIndex.ImageCache)
        {
            textureIndex.ImageCache[item] = image;
        }

        return image;
    }

    private static ImageSource? TryReadTextureItem(ExplorerItem item)
    {
        try
        {
            if (item.Kind == ExplorerItemKind.LocalFile)
            {
                return TryReadLocalTexture(item.SourcePath);
            }

            if (item.Kind == ExplorerItemKind.RezFile &&
                item.Archive is not null &&
                item.ArchiveFile is not null)
            {
                return TryReadArchiveTexture(item.Archive, item.ArchiveFile);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static ImageSource? TryReadLocalTexture(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 0 || info.Length > MaxTextureBytes || info.Length > int.MaxValue)
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(filePath);
            return TryDecodeTexture(info.Extension.TrimStart('.'), data);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryDecodeTexture(string extension, byte[] data)
    {
        if (CrossFireImageBinDecoder.IsCandidate(extension) ||
            CrossFireImageBinDecoder.HasEncodedHeader(data))
        {
            ImageSource? image = CrossFireImageBinDecoder.TryDecodeOriginal(data);
            if (image is not null)
            {
                return image;
            }
        }

        if (string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = DtxThumbnailDecoder.TryDecodeOriginal(data);
            if (image is not null)
            {
                return image;
            }
        }

        if (string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = DdsThumbnailDecoder.TryDecodeOriginal(data);
            if (image is not null)
            {
                return image;
            }
        }

        if (string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = TgaThumbnailDecoder.TryDecodeOriginal(data);
            if (image is not null)
            {
                return image;
            }
        }

        if (!string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = DtxThumbnailDecoder.TryDecodeOriginal(data);
            if (image is not null)
            {
                return image;
            }
        }

        if (!string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = DdsThumbnailDecoder.TryDecodeOriginal(data);
            if (image is not null)
            {
                return image;
            }
        }

        if (!string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = TgaThumbnailDecoder.TryDecodeOriginal(data);
            if (image is not null)
            {
                return image;
            }
        }

        return TryLoadBitmapImage(data);
    }

    private static ImageSource? TryLoadBitmapImage(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ArchiveTextureIndex GetArchiveTextureIndex(RezArchive archive)
    {
        return ArchiveTextureIndexCache.GetValue(archive, static archiveValue =>
        {
            var filesByPath = new Dictionary<string, RezFileNode>(StringComparer.OrdinalIgnoreCase);
            var filesByName = new Dictionary<string, RezFileNode?>(StringComparer.OrdinalIgnoreCase);
            var filesBySuffix = new Dictionary<string, RezFileNode?>(StringComparer.OrdinalIgnoreCase);
            AddArchiveTextureFiles(archiveValue.Root, filesByPath, filesByName, filesBySuffix);
            return new ArchiveTextureIndex(filesByPath, filesByName, filesBySuffix, []);
        });
    }

    private static TextureIndex BuildTextureIndex(ExplorerItem root)
    {
        var filesByPath = new Dictionary<string, ExplorerItem>(StringComparer.OrdinalIgnoreCase);
        var filesByName = new Dictionary<string, ExplorerItem?>(StringComparer.OrdinalIgnoreCase);
        var filesBySuffix = new Dictionary<string, ExplorerItem?>(StringComparer.OrdinalIgnoreCase);
        CollectTextureItems(root, filesByPath, filesByName, filesBySuffix);
        return new TextureIndex(filesByPath, filesByName, filesBySuffix, []);
    }

    private static void CollectTextureItems(
        ExplorerItem item,
        Dictionary<string, ExplorerItem> filesByPath,
        Dictionary<string, ExplorerItem?> filesByName,
        Dictionary<string, ExplorerItem?> filesBySuffix)
    {
        if (item.IsFile && IsTextureExtension(item.FileExtension))
        {
            string normalizedPath = NormalizeTexturePath(item.OutputRelativePath);
            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                filesByPath.TryAdd(normalizedPath, item);
            }

            if (LithTechResourceHeuristics.IsLikelyModelTexturePath(normalizedPath, item.FileExtension))
            {
                AddSuffixIndexCandidates(filesBySuffix, normalizedPath, item);

                string fileName = GetResourceFileName(normalizedPath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    AddUniqueIndexCandidate(filesByName, fileName, item);
                }
            }
        }

        foreach (ExplorerItem child in item.Children)
        {
            CollectTextureItems(child, filesByPath, filesByName, filesBySuffix);
        }
    }

    private static void AddArchiveTextureFiles(
        RezDirectoryNode directory,
        Dictionary<string, RezFileNode> filesByPath,
        Dictionary<string, RezFileNode?> filesByName,
        Dictionary<string, RezFileNode?> filesBySuffix)
    {
        foreach (RezNode child in directory.Children)
        {
            if (child is RezFileNode file)
            {
                if (!IsTextureExtension(file.Extension))
                {
                    continue;
                }

                string normalizedPath = NormalizeTexturePath(file.FullPath);
                if (!string.IsNullOrWhiteSpace(normalizedPath))
                {
                    filesByPath.TryAdd(normalizedPath, file);
                }

                if (LithTechResourceHeuristics.IsLikelyModelTexturePath(normalizedPath, file.Extension))
                {
                    AddSuffixIndexCandidates(filesBySuffix, normalizedPath, file);

                    string fileName = GetResourceFileName(normalizedPath);
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        AddUniqueIndexCandidate(filesByName, fileName, file);
                    }
                }
            }
            else if (child is RezDirectoryNode childDirectory)
            {
                AddArchiveTextureFiles(childDirectory, filesByPath, filesByName, filesBySuffix);
            }
        }
    }

    private static bool IsTextureExtension(string extension)
    {
        string normalized = extension.Trim().TrimStart('.');
        return normalized.Length > 0 &&
               TextureExtensions.Contains("." + normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddSuffixIndexCandidates<T>(
        Dictionary<string, T?> index,
        string normalizedPath,
        T item)
        where T : class
    {
        string[] segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int startIndex = 1; startIndex < segments.Length - 1; startIndex++)
        {
            AddUniqueIndexCandidate(index, string.Join("/", segments.Skip(startIndex)), item);
        }
    }

    private static void AddUniqueIndexCandidate<T>(
        Dictionary<string, T?> index,
        string key,
        T item)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!index.TryGetValue(key, out T? existing))
        {
            index[key] = item;
            return;
        }

        if (existing is not null && ReferenceEquals(existing, item))
        {
            return;
        }

        index[key] = null;
    }

    private static string GetResourceFileName(string normalizedPath)
    {
        int slashIndex = normalizedPath.LastIndexOf('/');
        return slashIndex >= 0 ? normalizedPath[(slashIndex + 1)..] : normalizedPath;
    }

    private static string NormalizeTexturePath(string path)
    {
        return path
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private sealed record TextureIndex(
        Dictionary<string, ExplorerItem> FilesByPath,
        Dictionary<string, ExplorerItem?> UniqueFilesByName,
        Dictionary<string, ExplorerItem?> UniqueFilesBySuffix,
        Dictionary<ExplorerItem, ImageSource?> ImageCache)
    {
        public bool IsEmpty => FilesByPath.Count == 0 && UniqueFilesByName.Count == 0 && UniqueFilesBySuffix.Count == 0;
    }

    private sealed record ArchiveTextureIndex(
        Dictionary<string, RezFileNode> FilesByPath,
        Dictionary<string, RezFileNode?> UniqueFilesByName,
        Dictionary<string, RezFileNode?> UniqueFilesBySuffix,
        Dictionary<RezFileNode, ImageSource?> ImageCache);
}
