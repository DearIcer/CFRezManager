using System.IO;
using System.Windows.Media;

namespace CFRezManager;

internal sealed record UnityPreviewExport(string ObjPath, string DirectoryPath, string? AnimPackagePath);

internal static class UnityPreviewExporter
{
    private static readonly TimeSpan StaleDirectoryAge = TimeSpan.FromDays(1);

    private static string TempRootPath => Path.Combine(Path.GetTempPath(), "CFRezManager", "UnityPreview");

    public static UnityPreviewExport? Export(string fileName, LithTechModelDocument document, Func<string, ImageSource?>? textureResolver)
    {
        try
        {
            string directoryPath = Path.Combine(TempRootPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            string objPath = Path.Combine(directoryPath, "preview.obj");
            var source = new LithTechObjExportSource(
                Path.GetFileNameWithoutExtension(fileName),
                fileName,
                document,
                textureResolver);
            LithTechObjExporter.Export(objPath, [source]);

            string? animPackagePath = null;
            if (document.Skeleton is not null && document.Animations.Count > 0)
            {
                string candidatePath = Path.Combine(directoryPath, "preview.cfan");
                if (LithTechAnimPackageExporter.Export(candidatePath, source))
                {
                    animPackagePath = candidatePath;
                }
            }

            return new UnityPreviewExport(objPath, directoryPath, animPackagePath);
        }
        catch
        {
            return null;
        }
    }

    public static void Cleanup(UnityPreviewExport? export)
    {
        if (export is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(export.DirectoryPath))
            {
                Directory.Delete(export.DirectoryPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup; stale directories are swept on next startup.
        }
    }

    public static void CleanupStaleDirectories()
    {
        try
        {
            if (!Directory.Exists(TempRootPath))
            {
                return;
            }

            DateTime threshold = DateTime.Now - StaleDirectoryAge;
            foreach (string directory in Directory.EnumerateDirectories(TempRootPath))
            {
                try
                {
                    if (Directory.GetLastWriteTime(directory) < threshold)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                    // Never block startup on cleanup of a single directory.
                }
            }
        }
        catch
        {
            // Never block startup on temp cleanup.
        }
    }
}
