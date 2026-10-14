using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace MediaRecovery.Services;

public record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string DownloadUrl,
    long AssetSizeBytes
);

public class UpdateService
{
    private const string RepoOwner = "maezno";
    private const string RepoName = "MediaRecovery";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MediaRecoveryStudio", GetCurrentVersionString()));
    }

    public static string GetCurrentVersionString()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        string currentVer = GetCurrentVersionString();

        try
        {
            using var response = await _http.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, currentVer, currentVer, "", $"No se pudo consultar GitHub (Status: {response.StatusCode})", "", 0);
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            string cleanLatest = tag.TrimStart('v', 'V');
            string title = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : tag;
            string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";

            string downloadUrl = "";
            long assetSize = 0;

            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var aName) ? aName.GetString() ?? "" : "";
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var aUrl) ? aUrl.GetString() ?? "" : "";
                        assetSize = asset.TryGetProperty("size", out var aSize) ? aSize.GetInt64() : 0;
                        break;
                    }
                }
            }

            bool isNewer = IsVersionNewer(currentVer, cleanLatest);
            return new UpdateCheckResult(isNewer, currentVer, cleanLatest, title, body, downloadUrl, assetSize);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, currentVer, currentVer, "", $"Error al verificar actualizaciones: {ex.Message}", "", 0);
        }
    }

    private static bool IsVersionNewer(string currentStr, string latestStr)
    {
        if (Version.TryParse(currentStr, out var current) && Version.TryParse(latestStr, out var latest))
        {
            return latest > current;
        }
        return false;
    }

    /// <summary>
    /// Descarga el nuevo ejecutable y crea un script cmd para reemplazar el actual y relanzar la app.
    /// </summary>
    public async Task<bool> DownloadAndInstallUpdateAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)) return false;

        string currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath)) return false;

        string currentDir = Path.GetDirectoryName(currentExePath)!;
        string tempExePath = Path.Combine(currentDir, $"MediaRecovery_Update_{Guid.NewGuid():N}.exe");

        // 1. Descarga del archivo con reporte de progreso
        using (var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            byte[] buffer = new byte[65536];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;
                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)totalRead / totalBytes * 100.0);
                }
            }
        }

        // 2. Generar script CMD para esperar cierre del proceso, reemplazar el .exe y relanzarlo
        int currentPid = Environment.ProcessId;
        string updaterScriptPath = Path.Combine(currentDir, "update_install.cmd");

        string scriptContent = $@"@echo off
timeout /t 1 /nobreak > nul
:waitloop
tasklist /fi ""PID eq {currentPid}"" 2>nul | find ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto waitloop
)
move /y ""{tempExePath}"" ""{currentExePath}"" >nul
start """" ""{currentExePath}""
del ""%~f0""
";
        await File.WriteAllTextAsync(updaterScriptPath, scriptContent, cancellationToken);

        // 3. Ejecutar el script y cerrar la app actual
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{updaterScriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Process.Start(psi);

        // Salir de la aplicación para permitir el reemplazo
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });

        return true;
    }
}
