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
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true
        };
        _http = new HttpClient(handler);
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
    /// Paso 1: Descarga el nuevo binario a un archivo temporal a alta velocidad (buffer de 512KB + streams optimizados).
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(
        string downloadUrl,
        IProgress<(double Pct, double SpeedMBs)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

        string currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrWhiteSpace(currentExePath)) return null;

        string currentDir = Path.GetDirectoryName(currentExePath)!;
        string tempExePath = Path.Combine(currentDir, $"MediaRecovery_New_{Guid.NewGuid():N}.tmp");

        using (var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            // FileStream optimizado con 512 KB de buffer interno y escritura asíncrona
            const int bufferSize = 512 * 1024;
            await using var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[bufferSize];
            long totalRead = 0;
            int bytesRead;

            var stopwatch = Stopwatch.StartNew();
            long lastReportBytes = 0;
            long lastReportTicks = stopwatch.ElapsedTicks;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                long nowTicks = stopwatch.ElapsedTicks;
                double elapsedSec = (double)(nowTicks - lastReportTicks) / Stopwatch.Frequency;

                // Notificar cada 200 ms para no saturar el Dispatcher UI
                if (elapsedSec >= 0.20 && progress != null)
                {
                    double bytesInInterval = totalRead - lastReportBytes;
                    double speedMBs = (bytesInInterval / (1024.0 * 1024.0)) / elapsedSec;
                    double pct = totalBytes > 0 ? ((double)totalRead / totalBytes * 100.0) : 0;

                    progress.Report((pct, speedMBs));

                    lastReportTicks = nowTicks;
                    lastReportBytes = totalRead;
                }
            }
        }

        // Validar integridad mínima: debe existir, tener tamaño válido y comenzar con la firma PE de Windows ('MZ')
        try
        {
            var fileInfo = new FileInfo(tempExePath);
            if (!fileInfo.Exists || fileInfo.Length < 1024 * 1024)
            {
                try { File.Delete(tempExePath); } catch { }
                return null;
            }

            byte[] header = new byte[2];
            using (var fs = new FileStream(tempExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Read(header, 0, 2) < 2 || header[0] != 0x4D || header[1] != 0x5A)
                {
                    // Archivo no es un ejecutable PE válido (ej. multipart corrupto o página de error HTML)
                    fs.Close();
                    try { File.Delete(tempExePath); } catch { }
                    return null;
                }
            }
        }
        catch
        {
            try { File.Delete(tempExePath); } catch { }
            return null;
        }

        return tempExePath;
    }

    /// <summary>
    /// Paso 2: Genera el script de reemplazo, sustituye el ejecutable actual y reinicia la app.
    /// </summary>
    public void ApplyUpdateAndRestart(string tempExePath)
    {
        if (string.IsNullOrWhiteSpace(tempExePath) || !File.Exists(tempExePath)) return;

        // Doble verificación de seguridad de la firma PE antes de ejecutar la sustitución
        try
        {
            byte[] header = new byte[2];
            using (var fs = new FileStream(tempExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Read(header, 0, 2) < 2 || header[0] != 0x4D || header[1] != 0x5A)
                {
                    return;
                }
            }
        }
        catch
        {
            return;
        }

        string currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrWhiteSpace(currentExePath)) return;

        int currentPid = Environment.ProcessId;

        // Guardamos el script en la carpeta temporal para que no dependa ni bloquee el directorio de la app
        string updaterScriptPath = Path.Combine(Path.GetTempPath(), $"mr_update_{Guid.NewGuid():N}.cmd");

        // Script batch con bucle de espera por liberación de bloqueo (file lock) y reintentos en el comando move
        string scriptContent = $@"@echo off
setlocal enabledelayedexpansion

:: 1. Esperar a que el proceso anterior termine completamente
:waitproc
tasklist /fi ""PID eq {currentPid}"" 2>nul | find ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto waitproc
)

:: Breve pausa para asegurar liberacion de handles del sistema operativo
timeout /t 1 /nobreak >nul

:: 2. Bucle de reintentos para sobrescribir el ejecutable original
set RETRIES=0
:moveloop
move /y ""{tempExePath}"" ""{currentExePath}"" >nul 2>&1
if errorlevel 1 (
    set /a RETRIES+=1
    if !RETRIES! geq 30 goto fail
    timeout /t 1 /nobreak >nul
    goto moveloop
)

:: 3. Iniciar la aplicacion actualizada
start """" ""{currentExePath}""
goto cleanup

:fail
:: Si fallo por permisos u otro motivo, abrir al menos el ejecutable o notificar
start """" ""{currentExePath}""

:cleanup
:: Auto-eliminar el script temporal
del ""%~f0""
";
        File.WriteAllText(updaterScriptPath, scriptContent);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{updaterScriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Process.Start(psi);

        // Forzar cierre completo de la app para liberar handles de archivos de inmediato
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
        Environment.Exit(0);
    }
}
