using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record FmodBankAudioSource(
    string FileName,
    byte[] BankData,
    IReadOnlyList<FmodBankFsbBlock> FsbBlocks,
    bool Compressed,
    int SourceByteCount,
    int DecodedByteCount,
    bool Partial = false,
    int? StreamCountLimit = null)
{
    public int TotalStreamCount => FsbBlocks.Sum(block => block.StreamCount > int.MaxValue ? int.MaxValue : (int)block.StreamCount);

    public int StreamCount => StreamCountLimit is > 0
        ? Math.Min(StreamCountLimit.Value, TotalStreamCount)
        : TotalStreamCount;

    public IReadOnlyList<string> GetStreamNames()
    {
        int totalStreams = StreamCount;
        var names = new List<string>(totalStreams);
        foreach (FmodBankFsbBlock block in FsbBlocks)
        {
            if (block.StreamCount > int.MaxValue)
            {
                break;
            }

            int streamCount = (int)block.StreamCount;
            for (int localIndex = 0; localIndex < streamCount; localIndex++)
            {
                if (names.Count >= totalStreams)
                {
                    return names;
                }

                string name = localIndex < block.SampleNames.Count
                    ? block.SampleNames[localIndex]
                    : string.Empty;
                names.Add(string.IsNullOrWhiteSpace(name)
                    ? $"Stream {names.Count + 1:N0}"
                    : name);
            }
        }

        return names;
    }
}

internal static class FmodBankAudioPreviewDocumentFactory
{
    private const int FsbHeaderLength = 0x3C;
    private const int SmallCompleteFsbBlockBytes = 16 * 1024 * 1024;
    private const int VgmstreamTimeoutMilliseconds = 120_000;
    private static string? _cachedExecutablePath;
    private static bool _cachedExecutableResolved;

    public static bool IsAvailable => true;

    public static bool TryCreateSource(
        string fileName,
        byte[] data,
        out FmodBankAudioSource? source,
        out string? errorMessage)
    {
        source = null;
        errorMessage = null;

        if (!FmodBankDecoder.TryPrepareDecodedData(
                data,
                out byte[]? bankData,
                out bool compressed,
                out long decodedBytes,
                out errorMessage) ||
            bankData is null)
        {
            return false;
        }

        return TryCreateSourceFromPreparedData(
            fileName,
            bankData,
            compressed,
            data.Length,
            checked((int)Math.Min(decodedBytes, int.MaxValue)),
            partial: false,
            out source,
            out errorMessage);
    }

    public static bool TryCreateSourceFromPreparedData(
        string fileName,
        byte[] bankData,
        bool compressed,
        int sourceByteCount,
        int decodedByteCount,
        bool partial,
        out FmodBankAudioSource? source,
        out string? errorMessage)
    {
        return TryCreateSourceFromPreparedData(
            fileName,
            bankData,
            bankData.Length,
            compressed,
            sourceByteCount,
            decodedByteCount,
            partial,
            out source,
            out errorMessage);
    }

    public static bool TryCreateSourceFromPreparedData(
        string fileName,
        byte[] bankData,
        int bankDataLength,
        bool compressed,
        int sourceByteCount,
        int decodedByteCount,
        bool partial,
        out FmodBankAudioSource? source,
        out string? errorMessage)
    {
        source = null;
        errorMessage = null;
        bankDataLength = Math.Clamp(bankDataLength, 0, bankData.Length);

        IReadOnlyList<FmodBankFsbBlock> fsbBlocks = FmodBankDecoder.ExtractFsbBlocks(
            bankData,
            bankDataLength,
            includeMetadataOnlyBlocks: true);
        if (fsbBlocks.Count == 0 || !fsbBlocks.Any(block => block.StreamCount > 0))
        {
            errorMessage = "BANK does not contain playable FSB5 streams.";
            return false;
        }

        source = new FmodBankAudioSource(
            fileName,
            bankData,
            fsbBlocks,
            compressed,
            sourceByteCount,
            Math.Min(decodedByteCount, bankDataLength),
            partial);
        return true;
    }

    public static bool TryCreate(
        FmodBankAudioSource source,
        int streamIndex,
        out AudioPreviewDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!TryResolveStream(source, streamIndex, out FmodBankFsbBlock? block, out int localStreamIndex))
        {
            errorMessage = $"BANK stream index is out of range: {streamIndex + 1}.";
            return false;
        }

        if (TryCreateWithFmod5Sharp(source, block, localStreamIndex, streamIndex, out document, out string? fmodError))
        {
            return true;
        }

        if (!TryResolveVgmstream(out string? vgmstreamPath) || string.IsNullOrWhiteSpace(vgmstreamPath))
        {
            errorMessage = string.IsNullOrWhiteSpace(fmodError)
                ? "BANK stream codec is not supported by the built-in decoder, and vgmstream-cli.exe was not found."
                : $"{fmodError} vgmstream-cli.exe was not found for fallback.";
            return false;
        }

        if (TryCreateWithVgmstream(source, block, localStreamIndex, streamIndex, vgmstreamPath, out document, out errorMessage))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(fmodError) && !string.IsNullOrWhiteSpace(errorMessage))
        {
            errorMessage = $"{fmodError} Fallback failed: {errorMessage}";
        }

        return false;
    }

    public static bool TryDecodeStreamToWaveData(
        FmodBankAudioSource source,
        int streamIndex,
        out byte[]? waveData,
        out string streamName,
        out string? errorMessage)
    {
        waveData = null;
        streamName = string.Empty;
        errorMessage = null;

        if (!TryResolveStream(source, streamIndex, out FmodBankFsbBlock? block, out int localStreamIndex))
        {
            errorMessage = $"BANK stream index is out of range: {streamIndex + 1}.";
            return false;
        }

        streamName = ResolveStreamName(block, localStreamIndex);
        string? oggError = null;
        if (TryRebuildWithFmod5Sharp(
                source,
                block,
                localStreamIndex,
                streamIndex,
                out byte[]? rebuiltData,
                out string? extension,
                out streamName,
                out string? fmodError) &&
            rebuiltData is not null)
        {
            if (string.Equals(extension, "wav", StringComparison.OrdinalIgnoreCase))
            {
                waveData = rebuiltData;
                return true;
            }

            if (string.Equals(extension, "ogg", StringComparison.OrdinalIgnoreCase) &&
                OggVorbisWaveDecoder.TryDecodeToWaveBytes(rebuiltData, out byte[] oggWaveData, out oggError))
            {
                waveData = oggWaveData;
                return true;
            }

            fmodError = string.IsNullOrWhiteSpace(oggError)
                ? $"BANK stream {streamIndex + 1:N0} rebuilt to an unsupported format."
                : $"BANK stream {streamIndex + 1:N0} Ogg to WAVE conversion failed: {oggError}";
        }

        if (!TryResolveVgmstream(out string? vgmstreamPath) || string.IsNullOrWhiteSpace(vgmstreamPath))
        {
            errorMessage = string.IsNullOrWhiteSpace(fmodError)
                ? "BANK stream codec is not supported by the built-in decoder, and vgmstream-cli.exe was not found."
                : $"{fmodError} vgmstream-cli.exe was not found for fallback.";
            return false;
        }

        if (TryDecodeStreamToWaveWithVgmstream(source, block, localStreamIndex, vgmstreamPath, out waveData, out errorMessage))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(fmodError) && !string.IsNullOrWhiteSpace(errorMessage))
        {
            errorMessage = $"{fmodError} Fallback failed: {errorMessage}";
        }

        return false;
    }

    private static bool TryDecodeStreamToWaveWithVgmstream(
        FmodBankAudioSource source,
        FmodBankFsbBlock block,
        int localStreamIndex,
        string vgmstreamPath,
        out byte[]? waveData,
        out string? errorMessage)
    {
        waveData = null;
        errorMessage = null;

        string fsbPath = AudioPreviewDocumentFactory.CreateTemporaryAudioPath("fsb");
        string wavPath = AudioPreviewDocumentFactory.CreateTemporaryAudioPath("wav");
        try
        {
            if (!TryCreateFsbDataForStream(
                    source,
                    block,
                    localStreamIndex,
                    out byte[]? fsbData,
                    out int fsbStreamIndex,
                    out _,
                    out errorMessage) ||
                fsbData is null)
            {
                return false;
            }

            File.WriteAllBytes(fsbPath, fsbData);
            if (!RunVgmstream(vgmstreamPath, fsbPath, wavPath, fsbStreamIndex, out errorMessage))
            {
                return false;
            }

            waveData = File.ReadAllBytes(wavPath);
            return waveData.Length > 0;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        finally
        {
            AudioPreviewDocumentFactory.TryDeleteFile(fsbPath);
            AudioPreviewDocumentFactory.TryDeleteFile(wavPath);
        }
    }

    public static bool TryGetRequiredDecodedByteCount(
        FmodBankAudioSource source,
        int streamIndex,
        out int requiredByteCount)
    {
        requiredByteCount = 0;
        if (!TryResolveStream(source, streamIndex, out FmodBankFsbBlock? block, out int localStreamIndex))
        {
            return false;
        }

        int sampleIndex = localStreamIndex - 1;
        if (sampleIndex >= 0 && sampleIndex < block.Samples.Count)
        {
            FmodBankFsbSample sample = block.Samples[sampleIndex];
            requiredByteCount = checked(block.Offset + block.MetadataLength + sample.DataOffset + sample.DataLength);
            return requiredByteCount > 0;
        }

        requiredByteCount = checked(block.Offset + block.Length);
        return requiredByteCount > 0;
    }

    public static bool TryCreateThumbnailAudioData(
        string fileName,
        byte[] data,
        out byte[]? audioData,
        out string? audioTitle,
        out string? errorMessage)
    {
        audioData = null;
        audioTitle = null;
        errorMessage = null;

        if (!TryCreateSource(fileName, data, out FmodBankAudioSource? source, out errorMessage) ||
            source is null)
        {
            return false;
        }

        string? lastError = null;
        int probeCount = Math.Min(source.StreamCount, 8);
        for (int streamIndex = 0; streamIndex < probeCount; streamIndex++)
        {
            if (!TryResolveStream(source, streamIndex, out FmodBankFsbBlock? block, out int localStreamIndex))
            {
                break;
            }

            if (TryRebuildWithFmod5Sharp(
                    source,
                    block,
                    localStreamIndex,
                    streamIndex,
                    out byte[]? rebuiltData,
                    out _,
                    out string streamName,
                    out lastError) &&
                rebuiltData is not null)
            {
                audioData = rebuiltData;
                audioTitle = BuildAudioName(source, streamIndex, streamName);
                return true;
            }
        }

        errorMessage = lastError ?? "BANK does not contain a stream supported by the built-in thumbnail decoder.";
        return false;
    }

    private static bool TryCreateWithFmod5Sharp(
        FmodBankAudioSource source,
        FmodBankFsbBlock block,
        int localStreamIndex,
        int streamIndex,
        out AudioPreviewDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!TryRebuildWithFmod5Sharp(
                source,
                block,
                localStreamIndex,
                streamIndex,
                out byte[]? rebuiltData,
                out string? extension,
                out string streamName,
                out errorMessage) ||
            rebuiltData is null)
        {
            return false;
        }

        string audioName = BuildAudioName(source, streamIndex, streamName);
        string rebuiltFileName = BuildRebuiltFileName(audioName, extension);
        if (!AudioPreviewDocumentFactory.TryCreate(
                rebuiltFileName,
                sourcePath: null,
                rebuiltData,
                canUseSourcePath: false,
                out AudioPreviewDocument? rebuiltDocument,
                out errorMessage) ||
            rebuiltDocument is null)
        {
            errorMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? $"Built-in BANK audio rebuild produced unsupported data for stream {streamIndex + 1:N0}."
                : errorMessage;
            return false;
        }

        string info = $"{BuildStorageInfo(source)} - FSB5 stream {streamIndex + 1:N0}/{source.StreamCount:N0}, block #{block.Index:D2}, Fmod5Sharp -> {rebuiltDocument.AudioInfo}";
        document = new AudioPreviewDocument(audioName, rebuiltDocument.AudioPath, info, rebuiltDocument.TemporaryPaths, "BANK");
        return true;
    }

    private static bool TryRebuildWithFmod5Sharp(
        FmodBankAudioSource source,
        FmodBankFsbBlock block,
        int localStreamIndex,
        int streamIndex,
        out byte[]? rebuiltData,
        out string? extension,
        out string streamName,
        out string? errorMessage)
    {
        rebuiltData = null;
        extension = null;
        streamName = string.Empty;
        errorMessage = null;

        try
        {
            if (!TryCreateFsbDataForStream(
                    source,
                    block,
                    localStreamIndex,
                    out byte[]? fsbData,
                    out int fsbStreamIndex,
                    out string resolvedName,
                    out errorMessage) ||
                fsbData is null)
            {
                return false;
            }

            Fmod5Sharp.FmodTypes.FmodSoundBank soundBank = Fmod5Sharp.FsbLoader.LoadFsbFromByteArray(fsbData);
            int sampleIndex = fsbStreamIndex - 1;
            if (sampleIndex < 0 || sampleIndex >= soundBank.Samples.Count)
            {
                errorMessage = $"Fmod5Sharp did not expose BANK stream {streamIndex + 1:N0}.";
                return false;
            }

            Fmod5Sharp.FmodTypes.FmodSample sample = soundBank.Samples[sampleIndex];
            if (!sample.RebuildAsStandardFileFormat(out rebuiltData, out extension) ||
                rebuiltData is null ||
                rebuiltData.Length == 0)
            {
                errorMessage = $"Fmod5Sharp could not rebuild BANK stream {streamIndex + 1:N0}.";
                return false;
            }

            streamName = string.IsNullOrWhiteSpace(sample.Name)
                ? resolvedName
                : sample.Name;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Built-in BANK audio decode failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryCreateWithVgmstream(
        FmodBankAudioSource source,
        FmodBankFsbBlock block,
        int localStreamIndex,
        int streamIndex,
        string vgmstreamPath,
        out AudioPreviewDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        string fsbPath = AudioPreviewDocumentFactory.CreateTemporaryAudioPath("fsb");
        string wavPath = AudioPreviewDocumentFactory.CreateTemporaryAudioPath("wav");
        try
        {
            if (!TryCreateFsbDataForStream(
                    source,
                    block,
                    localStreamIndex,
                    out byte[]? fsbData,
                    out int fsbStreamIndex,
                    out string streamName,
                    out errorMessage) ||
                fsbData is null)
            {
                return false;
            }

            File.WriteAllBytes(fsbPath, fsbData);
            if (!RunVgmstream(vgmstreamPath, fsbPath, wavPath, fsbStreamIndex, out errorMessage))
            {
                AudioPreviewDocumentFactory.TryDeleteFile(fsbPath);
                AudioPreviewDocumentFactory.TryDeleteFile(wavPath);
                return false;
            }

            string audioName = BuildAudioName(source, streamIndex, streamName);
            string info = $"{BuildStorageInfo(source)} - FSB5 stream {streamIndex + 1:N0}/{source.StreamCount:N0}, block #{block.Index:D2}, vgmstream fallback";

            document = new AudioPreviewDocument(audioName, wavPath, info, [fsbPath, wavPath], "BANK");
            return true;
        }
        catch (Exception ex)
        {
            AudioPreviewDocumentFactory.TryDeleteFile(fsbPath);
            AudioPreviewDocumentFactory.TryDeleteFile(wavPath);
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryCreateFsbDataForStream(
        FmodBankAudioSource source,
        FmodBankFsbBlock block,
        int localStreamIndex,
        out byte[]? fsbData,
        out int fsbStreamIndex,
        out string streamName,
        out string? errorMessage)
    {
        fsbData = null;
        fsbStreamIndex = localStreamIndex;
        streamName = ResolveStreamName(block, localStreamIndex);
        errorMessage = null;

        if (block.Offset + (long)block.Length <= source.DecodedByteCount &&
            block.Length <= SmallCompleteFsbBlockBytes)
        {
            fsbData = source.BankData.AsSpan(block.Offset, block.Length).ToArray();
            fsbStreamIndex = localStreamIndex;
            return true;
        }

        int sampleIndex = localStreamIndex - 1;
        if (sampleIndex >= 0 && sampleIndex < block.Samples.Count)
        {
            FmodBankFsbSample sample = block.Samples[sampleIndex];
            long sampleHeaderStart = block.Offset + (long)FsbHeaderLength + sample.HeaderOffset;
            long sampleDataStart = block.Offset + (long)block.MetadataLength + sample.DataOffset;
            long sampleHeaderEnd = sampleHeaderStart + sample.HeaderLength;
            long sampleDataEnd = sampleDataStart + sample.DataLength;
            if (sampleHeaderEnd <= source.DecodedByteCount &&
                sampleDataEnd <= source.DecodedByteCount)
            {
                fsbData = BuildSingleSampleFsb(source.BankData, block, sample, streamName);
                fsbStreamIndex = 1;
                return true;
            }
        }

        if (block.Offset + (long)block.Length <= source.DecodedByteCount)
        {
            fsbData = source.BankData.AsSpan(block.Offset, block.Length).ToArray();
            fsbStreamIndex = localStreamIndex;
            return true;
        }

        errorMessage = $"BANK stream data is still loading.";
        return false;
    }

    private static byte[] BuildSingleSampleFsb(
        byte[] bankData,
        FmodBankFsbBlock block,
        FmodBankFsbSample sample,
        string streamName)
    {
        byte[] nameBytes = string.IsNullOrWhiteSpace(streamName)
            ? []
            : Encoding.UTF8.GetBytes(streamName);
        int nameTableSize = nameBytes.Length == 0 ? 0 : sizeof(uint) + nameBytes.Length + 1;
        int fsbLength = checked(FsbHeaderLength + sample.HeaderLength + nameTableSize + sample.DataLength);
        byte[] fsbData = new byte[fsbLength];

        bankData.AsSpan(block.Offset, FsbHeaderLength).CopyTo(fsbData);
        BinaryPrimitives.WriteUInt32LittleEndian(fsbData.AsSpan(8, sizeof(uint)), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fsbData.AsSpan(12, sizeof(uint)), checked((uint)sample.HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(fsbData.AsSpan(16, sizeof(uint)), checked((uint)nameTableSize));
        BinaryPrimitives.WriteUInt32LittleEndian(fsbData.AsSpan(20, sizeof(uint)), checked((uint)sample.DataLength));

        int headerSourceOffset = block.Offset + FsbHeaderLength + sample.HeaderOffset;
        Span<byte> sampleHeader = fsbData.AsSpan(FsbHeaderLength, sample.HeaderLength);
        bankData.AsSpan(headerSourceOffset, sample.HeaderLength).CopyTo(sampleHeader);
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(sampleHeader[..sizeof(uint)]);
        BinaryPrimitives.WriteUInt32LittleEndian(sampleHeader[..sizeof(uint)], first & 0x3F);

        int writeOffset = FsbHeaderLength + sample.HeaderLength;
        if (nameTableSize > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fsbData.AsSpan(writeOffset, sizeof(uint)), sizeof(uint));
            writeOffset += sizeof(uint);
            nameBytes.CopyTo(fsbData.AsSpan(writeOffset, nameBytes.Length));
            writeOffset += nameBytes.Length + 1;
        }

        int sampleDataSourceOffset = block.Offset + block.MetadataLength + sample.DataOffset;
        bankData.AsSpan(sampleDataSourceOffset, sample.DataLength).CopyTo(fsbData.AsSpan(writeOffset, sample.DataLength));
        return fsbData;
    }

    private static string BuildAudioName(FmodBankAudioSource source, int streamIndex, string streamName)
    {
        string streamCountText = source.Partial
            ? $"{source.StreamCount:N0}+"
            : source.StreamCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(streamName)
            ? $"{source.FileName} [{streamIndex + 1:N0}/{streamCountText}]"
            : $"{source.FileName} [{streamIndex + 1:N0}/{streamCountText}] {streamName}";
    }

    private static string BuildRebuiltFileName(string audioName, string? extension)
    {
        extension = extension?.Trim().TrimStart('.') ?? string.Empty;
        return string.IsNullOrWhiteSpace(extension)
            ? audioName
            : $"{audioName}.{extension}";
    }

    private static string BuildStorageInfo(FmodBankAudioSource source)
    {
        return source.Compressed
            ? source.Partial
                ? $"LZMA-compressed FMOD bank preview prefix, {source.SourceByteCount:N0} bytes -> {source.DecodedByteCount:N0} bytes"
                : $"LZMA-compressed FMOD bank, {source.SourceByteCount:N0} bytes -> {source.DecodedByteCount:N0} bytes"
            : source.Partial
                ? $"FMOD bank preview prefix, {source.DecodedByteCount:N0} decoded bytes"
                : $"FMOD bank, {source.SourceByteCount:N0} bytes";
    }

    private static bool TryResolveStream(
        FmodBankAudioSource source,
        int streamIndex,
        out FmodBankFsbBlock block,
        out int localStreamIndex)
    {
        int remaining = streamIndex;
        foreach (FmodBankFsbBlock candidate in source.FsbBlocks)
        {
            if (candidate.StreamCount > int.MaxValue)
            {
                break;
            }

            int streamCount = (int)candidate.StreamCount;
            if (remaining < streamCount)
            {
                block = candidate;
                localStreamIndex = remaining + 1;
                return true;
            }

            remaining -= streamCount;
        }

        block = null!;
        localStreamIndex = 0;
        return false;
    }

    private static string ResolveStreamName(FmodBankFsbBlock block, int localStreamIndex)
    {
        int nameIndex = localStreamIndex - 1;
        return nameIndex >= 0 && nameIndex < block.SampleNames.Count
            ? block.SampleNames[nameIndex]
            : string.Empty;
    }

    private static bool RunVgmstream(
        string vgmstreamPath,
        string fsbPath,
        string wavPath,
        int localStreamIndex,
        out string? errorMessage)
    {
        errorMessage = null;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = vgmstreamPath,
            WorkingDirectory = Path.GetDirectoryName(vgmstreamPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add(localStreamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(wavPath);
        process.StartInfo.ArgumentList.Add(fsbPath);

        try
        {
            if (!process.Start())
            {
                errorMessage = "Could not start vgmstream-cli.exe.";
                return false;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(VgmstreamTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                errorMessage = "vgmstream-cli.exe timed out while converting the BANK stream.";
                return false;
            }

            if (process.ExitCode != 0 || !File.Exists(wavPath) || new FileInfo(wavPath).Length == 0)
            {
                errorMessage = BuildVgmstreamError(process.ExitCode, stdout, stderr);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string BuildVgmstreamError(int exitCode, string stdout, string stderr)
    {
        string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        detail = detail.Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"vgmstream-cli.exe failed with exit code {exitCode}."
            : $"vgmstream-cli.exe failed with exit code {exitCode}: {detail}";
    }

    private static bool TryResolveVgmstream(out string? executablePath)
    {
        if (_cachedExecutableResolved && !string.IsNullOrWhiteSpace(_cachedExecutablePath))
        {
            executablePath = _cachedExecutablePath;
            return true;
        }

        foreach (string candidate in EnumerateVgmstreamCandidates())
        {
            if (File.Exists(candidate))
            {
                _cachedExecutablePath = Path.GetFullPath(candidate);
                _cachedExecutableResolved = true;
                executablePath = _cachedExecutablePath;
                return true;
            }
        }

        _cachedExecutableResolved = true;
        executablePath = null;
        return false;
    }

    private static IEnumerable<string> EnumerateVgmstreamCandidates()
    {
        string? env = Environment.GetEnvironmentVariable("CFREZ_VGMSTREAM");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "tools", "vgmstream", "vgmstream-cli.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "vgmstream-cli.exe");

        foreach (string root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, "tools", "vgmstream", "vgmstream-cli.exe");
            yield return Path.Combine(root, "tools_downloads", "vgmstream-r2083", "vgmstream-cli.exe");
        }
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                if (seen.Add(directory.FullName))
                {
                    yield return directory.FullName;
                }
            }
        }
    }
}
