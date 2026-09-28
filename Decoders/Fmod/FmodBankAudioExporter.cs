using System.IO;

namespace CFRezManager;

internal sealed record FmodBankAudioExportProgress(int Completed, int Total, string StreamName);

internal sealed record FmodBankAudioExportResult(
    int ExportedCount,
    int FailedCount,
    IReadOnlyList<string> WavPaths,
    string? FirstError);

/// <summary>
/// Exports every audio stream embedded in an FMOD .bank as a PCM WAVE file,
/// reusing the preview decode pipeline (Fmod5Sharp rebuild, Ogg -> WAVE
/// conversion, vgmstream fallback) from FmodBankAudioPreviewDocumentFactory.
/// </summary>
internal static class FmodBankAudioExporter
{
    public static FmodBankAudioExportResult ExportStreamsToWave(
        byte[] data,
        string fileName,
        string outputDirectory,
        IProgress<FmodBankAudioExportProgress>? progress = null)
    {
        if (!FmodBankDecoder.TryPrepareDecodedData(
                data,
                out byte[]? bankData,
                out bool compressed,
                out long decodedBytes,
                out string? errorMessage) ||
            bankData is null)
        {
            throw new InvalidDataException(errorMessage ?? "BANK compression could not be decoded.");
        }

        if (!FmodBankAudioPreviewDocumentFactory.TryCreateSourceFromPreparedData(
                fileName,
                bankData,
                compressed,
                data.Length,
                checked((int)Math.Min(decodedBytes, int.MaxValue)),
                partial: false,
                out FmodBankAudioSource? source,
                out errorMessage) ||
            source is null)
        {
            throw new InvalidDataException(errorMessage ?? "BANK does not contain playable FSB5 streams.");
        }

        Directory.CreateDirectory(outputDirectory);
        string baseName = MakeSafeFileName(Path.GetFileNameWithoutExtension(fileName));

        var wavPaths = new List<string>();
        string? firstError = null;
        int failedCount = 0;
        int totalStreams = source.StreamCount;
        for (int streamIndex = 0; streamIndex < totalStreams; streamIndex++)
        {
            string streamLabel = $"Stream {streamIndex + 1:N0}";
            try
            {
                if (FmodBankAudioPreviewDocumentFactory.TryDecodeStreamToWaveData(
                        source,
                        streamIndex,
                        out byte[]? waveData,
                        out string streamName,
                        out string? streamError) &&
                    waveData is not null)
                {
                    streamLabel = streamName;
                    string wavPath = MakeUniquePath(Path.Combine(
                        outputDirectory,
                        $"{baseName}.{streamIndex + 1:D3}{BuildNameSuffix(streamName)}.wav"));
                    File.WriteAllBytes(wavPath, waveData);
                    wavPaths.Add(wavPath);
                }
                else
                {
                    failedCount++;
                    firstError ??= streamError;
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                firstError ??= ex.Message;
            }

            progress?.Report(new FmodBankAudioExportProgress(streamIndex + 1, totalStreams, streamLabel));
        }

        return new FmodBankAudioExportResult(wavPaths.Count, failedCount, wavPaths, firstError);
    }

    private static string BuildNameSuffix(string streamName)
    {
        if (string.IsNullOrWhiteSpace(streamName))
        {
            return string.Empty;
        }

        string safeName = MakeSafeFileName(streamName.Trim());
        return string.IsNullOrWhiteSpace(safeName) ? string.Empty : $"-{safeName}";
    }

    private static string MakeSafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "bank" : value;
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 1; ; index++)
        {
            string candidate = Path.Combine(directory, $"{fileName} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
