using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CFRezManager;

internal sealed record LithTechSpritePreviewDocument(
    IReadOnlyList<ImagePreviewFrame> Frames,
    int FrameRate,
    string Info);

internal static class LithTechSpritePreviewLoader
{
    private const int MaxSpriteBytes = 8 * 1024 * 1024;
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private const int MaxPreviewFrames = 512;
    private static readonly ConditionalWeakTable<RezArchive, Dictionary<string, RezFileNode>> ArchiveFileLookupCache = new();

    public static bool TryLoadFromArchive(
        RezArchive archive,
        RezFileNode spriteFile,
        string displayName,
        out LithTechSpritePreviewDocument? preview,
        out string? errorMessage)
    {
        preview = null;
        errorMessage = null;

        byte[]? spriteBytes = ReadArchiveFileBytes(archive, spriteFile, MaxSpriteBytes);
        if (spriteBytes is null)
        {
            errorMessage = $"SPR file is too large or unreadable: {displayName}";
            return false;
        }

        if (!LithTechSpriteDecoder.TryDecode(spriteBytes, displayName, out LithTechSpriteDocument? sprite, out errorMessage) ||
            sprite is null)
        {
            return false;
        }

        Dictionary<string, RezFileNode> filesByPath = GetArchiveFileLookup(archive);
        var frames = new List<ImagePreviewFrame>(Math.Min(sprite.FramePaths.Count, MaxPreviewFrames));
        var missingPaths = new List<string>();
        int skippedFrames = 0;

        for (int i = 0; i < sprite.FramePaths.Count && frames.Count < MaxPreviewFrames; i++)
        {
            string framePath = sprite.FramePaths[i];
            if (!TryFindFrame(filesByPath, framePath, out RezFileNode? frameFile) || frameFile is null)
            {
                missingPaths.Add(framePath);
                continue;
            }

            byte[]? frameBytes = ReadArchiveFileBytes(archive, frameFile, MaxFrameBytes);
            ImageSource? image = frameBytes is null ? null : DtxThumbnailDecoder.TryDecodeOriginal(frameBytes);
            if (image is null)
            {
                skippedFrames++;
                continue;
            }

            frames.Add(new ImagePreviewFrame($"Frame {i:0000}", image));
        }

        if (frames.Count == 0)
        {
            errorMessage = missingPaths.Count > 0
                ? $"Unable to find SPR DTX frames in this REZ. First missing frame: {missingPaths[0]}"
                : $"Unable to decode SPR DTX frames: {displayName}";
            return false;
        }

        string info = FormatInfo(sprite, frames.Count, missingPaths.Count, skippedFrames);
        preview = new LithTechSpritePreviewDocument(frames, sprite.FrameRate, info);
        return true;
    }

    public static bool TryLoadFromLocalFile(
        string spriteFilePath,
        string displayName,
        out LithTechSpritePreviewDocument? preview,
        out string? errorMessage)
    {
        preview = null;
        errorMessage = null;

        byte[]? spriteBytes = ReadLocalFileBytes(spriteFilePath, MaxSpriteBytes);
        if (spriteBytes is null)
        {
            errorMessage = $"SPR file is too large or unreadable: {displayName}";
            return false;
        }

        if (!LithTechSpriteDecoder.TryDecode(spriteBytes, displayName, out LithTechSpriteDocument? sprite, out errorMessage) ||
            sprite is null)
        {
            return false;
        }

        var frames = new List<ImagePreviewFrame>(Math.Min(sprite.FramePaths.Count, MaxPreviewFrames));
        var missingPaths = new List<string>();
        int skippedFrames = 0;

        for (int i = 0; i < sprite.FramePaths.Count && frames.Count < MaxPreviewFrames; i++)
        {
            string framePath = sprite.FramePaths[i];
            if (!TryFindLocalFrame(spriteFilePath, framePath, out string? localFramePath) ||
                localFramePath is null)
            {
                missingPaths.Add(framePath);
                continue;
            }

            ImageSource? image = TryReadLocalDtxFrame(localFramePath, decodeOriginal: true);
            if (image is null)
            {
                skippedFrames++;
                continue;
            }

            frames.Add(new ImagePreviewFrame($"Frame {i:0000}", image));
        }

        if (frames.Count == 0)
        {
            errorMessage = missingPaths.Count > 0
                ? $"Unable to find SPR DTX frames next to this file. First missing frame: {missingPaths[0]}"
                : $"Unable to decode SPR DTX frames: {displayName}";
            return false;
        }

        string info = FormatInfo(sprite, frames.Count, missingPaths.Count, skippedFrames);
        preview = new LithTechSpritePreviewDocument(frames, sprite.FrameRate, info);
        return true;
    }

    public static ImageSource? TryLoadThumbnailFromArchive(RezArchive archive, LithTechSpriteDocument sprite)
    {
        Dictionary<string, RezFileNode> filesByPath = GetArchiveFileLookup(archive);
        foreach (string framePath in sprite.FramePaths)
        {
            if (!TryFindFrame(filesByPath, framePath, out RezFileNode? frameFile) || frameFile is null)
            {
                continue;
            }

            byte[]? frameBytes = ReadArchiveFileBytes(archive, frameFile, MaxFrameBytes);
            ImageSource? image = frameBytes is null ? null : DtxThumbnailDecoder.TryDecode(frameBytes);
            if (image is not null)
            {
                return image;
            }
        }

        return null;
    }

    public static ImageSource? TryLoadThumbnailFromLocalFile(string spriteFilePath, LithTechSpriteDocument sprite)
    {
        foreach (string framePath in sprite.FramePaths)
        {
            if (!TryFindLocalFrame(spriteFilePath, framePath, out string? localFramePath) ||
                localFramePath is null)
            {
                continue;
            }

            ImageSource? image = TryReadLocalDtxFrame(localFramePath, decodeOriginal: false);
            if (image is not null)
            {
                return image;
            }
        }

        return null;
    }

    private static string FormatInfo(
        LithTechSpriteDocument sprite,
        int loadedFrames,
        int missingFrames,
        int skippedFrames)
    {
        string info = $"{sprite.StorageDescription}, {sprite.FrameCount:N0} frames @ {sprite.FrameRate:N0} fps";
        if (loadedFrames != sprite.FrameCount)
        {
            info += $", loaded {loadedFrames:N0}";
        }

        if (missingFrames > 0)
        {
            info += $", missing {missingFrames:N0}";
        }

        if (skippedFrames > 0)
        {
            info += $", skipped {skippedFrames:N0}";
        }

        if (sprite.FrameCount > MaxPreviewFrames)
        {
            info += $", capped at {MaxPreviewFrames:N0}";
        }

        return info;
    }

    private static Dictionary<string, RezFileNode> BuildFileLookup(RezDirectoryNode root)
    {
        var filesByPath = new Dictionary<string, RezFileNode>(StringComparer.OrdinalIgnoreCase);
        AddFiles(root, filesByPath);
        return filesByPath;
    }

    private static Dictionary<string, RezFileNode> GetArchiveFileLookup(RezArchive archive)
    {
        return ArchiveFileLookupCache.GetValue(archive, static archiveValue => BuildFileLookup(archiveValue.Root));
    }

    private static void AddFiles(RezDirectoryNode directory, Dictionary<string, RezFileNode> filesByPath)
    {
        foreach (RezNode child in directory.Children)
        {
            if (child is RezFileNode file)
            {
                filesByPath.TryAdd(NormalizeRezPath(file.FullPath), file);
            }
            else if (child is RezDirectoryNode childDirectory)
            {
                AddFiles(childDirectory, filesByPath);
            }
        }
    }

    private static bool TryFindFrame(
        Dictionary<string, RezFileNode> filesByPath,
        string framePath,
        out RezFileNode? frameFile)
    {
        string normalized = NormalizeRezPath(framePath);
        if (filesByPath.TryGetValue(normalized, out frameFile))
        {
            return true;
        }

        string fileName = Path.GetFileName(normalized);
        RezFileNode? match = null;
        foreach ((string path, RezFileNode candidate) in filesByPath)
        {
            if (!path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                frameFile = null;
                return false;
            }

            match = candidate;
        }

        frameFile = match;
        return frameFile is not null;
    }

    private static string NormalizeRezPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static bool TryFindLocalFrame(string spriteFilePath, string spriteFramePath, out string? framePath)
    {
        framePath = null;
        string? startDirectory = Path.GetDirectoryName(spriteFilePath);
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return false;
        }

        string relativePath = spriteFramePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (string? directory = startDirectory; !string.IsNullOrWhiteSpace(directory); directory = Directory.GetParent(directory)?.FullName)
        {
            string candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
            {
                framePath = candidate;
                return true;
            }
        }

        return false;
    }

    private static byte[]? ReadArchiveFileBytes(RezArchive archive, RezFileNode file, int maxBytes)
    {
        if (file.Size < 0 || file.Size > maxBytes)
        {
            return null;
        }

        byte[] data = new byte[file.Size];
        using Stream source = RezArchiveReader.OpenFileData(archive, file);
        source.ReadExactly(data);
        return data;
    }

    private static byte[]? ReadLocalFileBytes(string filePath, int maxBytes)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length < 0 || info.Length > maxBytes || info.Length > int.MaxValue)
        {
            return null;
        }

        return File.ReadAllBytes(filePath);
    }

    private static ImageSource? TryReadLocalDtxFrame(string framePath, bool decodeOriginal)
    {
        try
        {
            var info = new FileInfo(framePath);
            if (!info.Exists || info.Length < 0 || info.Length > MaxFrameBytes || info.Length > int.MaxValue)
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(framePath);
            return decodeOriginal
                ? DtxThumbnailDecoder.TryDecodeOriginal(data)
                : DtxThumbnailDecoder.TryDecode(data);
        }
        catch
        {
            return null;
        }
    }
}
