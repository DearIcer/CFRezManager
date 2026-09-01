using System.IO;

namespace CFRezManager;

/// <summary>
/// Writes extracted audio in a directly playable form, mirroring the audio preview
/// pipeline (AudioPreviewDocumentFactory): LZMA-wrapped streams are decompressed and
/// Ogg data is decoded to PCM WAVE so common players can open the result.
/// </summary>
internal static class DecodedAudioExporter
{
    public static bool IsCandidate(string extension)
    {
        return AudioMetadataDecoder.IsSupportedExtension(extension);
    }

    /// <summary>
    /// Decodes <paramref name="sourceData"/> and writes a playable file. Ogg sources are
    /// written as .wav next to <paramref name="destinationPath"/> (the alternate path is
    /// claimed through <paramref name="makeUniquePath"/>). Returns the path actually
    /// written, or null when the data cannot be decoded and should be copied raw.
    /// </summary>
    public static string? TryWritePlayable(
        byte[] sourceData,
        string destinationPath,
        Func<string, string> makeUniquePath)
    {
        byte[]? data = LzmaAloneDecoder.TryPrepareData(sourceData, AudioPreviewDocumentFactory.MaxAudioPreviewBytes);
        if (data is null)
        {
            return null;
        }

        if (AudioMetadataDecoder.IsOggData(data))
        {
            string wavePath = makeUniquePath(
                Path.ChangeExtension(destinationPath, ".wav") ?? $"{destinationPath}.wav");
            if (OggVorbisWaveDecoder.TryDecodeToWave(data, wavePath, out _))
            {
                return wavePath;
            }
        }

        File.WriteAllBytes(destinationPath, data);
        return destinationPath;
    }
}
