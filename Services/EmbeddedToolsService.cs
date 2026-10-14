using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace MediaRecovery.Services;

public static class EmbeddedToolsService
{
    private static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaRecoveryStudio",
        "bin"
    );

    private static bool _initialized;
    private static readonly object LockObj = new();

    /// <summary>
    /// Garantiza que ffmpeg.exe, ffprobe.exe y yt-dlp.exe estén disponibles en el sistema o extraídos localmente,
    /// y añade la ruta de las herramientas a la variable de entorno PATH del proceso actual.
    /// </summary>
    public static void EnsureToolsAvailable()
    {
        if (_initialized) return;

        lock (LockObj)
        {
            if (_initialized) return;

            Directory.CreateDirectory(ToolsDir);

            string ffmpegPath = Path.Combine(ToolsDir, "ffmpeg.exe");
            string ffprobePath = Path.Combine(ToolsDir, "ffprobe.exe");
            string ytdlpPath = Path.Combine(ToolsDir, "yt-dlp.exe");

            bool needsExtraction = !File.Exists(ffmpegPath) || !File.Exists(ffprobePath) || !File.Exists(ytdlpPath);

            if (needsExtraction)
            {
                ExtractEmbeddedTools();
            }

            // Inyectar ToolsDir al principio del PATH de esta sesión de proceso para que Process.Start("ffmpeg") lo encuentre siempre
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!currentPath.Contains(ToolsDir, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", ToolsDir + Path.PathSeparator + currentPath);
            }

            _initialized = true;
        }
    }

    private static void ExtractEmbeddedTools()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            // Buscar recurso embebido tools.zip
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("tools.zip", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null) return;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return;

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string destinationPath = Path.Combine(ToolsDir, entry.Name);

                // Solo extraer si no existe o si tiene tamaño diferente
                if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length != entry.Length)
                {
                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EmbeddedToolsService] Error extrayendo herramientas: {ex.Message}");
        }
    }

    public static string GetToolPath(string toolName)
    {
        EnsureToolsAvailable();
        string localPath = Path.Combine(ToolsDir, toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? toolName : toolName + ".exe");
        if (File.Exists(localPath))
        {
            return localPath;
        }
        return toolName;
    }
}
