using System.Diagnostics;
using System.IO;
using System.Text;

namespace MediaRecovery.Services;

public class VideoIntegrityResult
{
    public bool HasErrors { get; set; }
    public bool IsProResCandidate { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RawErrorDetails { get; set; } = string.Empty;
}

public class VideoIntegrityService
{
    private static readonly byte[] MagicIcpf = [0x69, 0x63, 0x70, 0x66]; // 'icpf'

    /// <summary>
    /// Analiza rápidamente la integridad del contenedor y flujo de video para detectar archivos dañados.
    /// </summary>
    public async Task<VideoIntegrityResult> CheckVideoIntegrityAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var result = new VideoIntegrityResult();

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return result;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
            {
                result.HasErrors = true;
                result.Title = "Archivo vacío";
                result.Description = "El archivo seleccionado tiene 0 bytes.";
                return result;
            }

            // 1. Probar contenedor con ffprobe
            var probeResult = await RunProcessCaptureAsync("ffprobe", 
                $"-v error -show_entries format=format_name,duration:stream=codec_type,codec_name -of json \"{filePath}\"", 
                cancellationToken);

            bool probeFailed = probeResult.ExitCode != 0 || !string.IsNullOrWhiteSpace(probeResult.StdErr);

            if (probeFailed)
            {
                // Error en el contenedor / cabecera
                bool containsIcpf = await ScanForProResMagicAsync(filePath, maxBytesToScan: 64 * 1024 * 1024, cancellationToken);
                result.HasErrors = true;
                result.IsProResCandidate = containsIcpf;
                result.RawErrorDetails = probeResult.StdErr;

                string shortError = CleanErrorMessage(probeResult.StdErr);

                if (probeResult.StdErr.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase))
                {
                    result.Title = "Contenedor QuickTime / MP4 incompleto (sin átomo 'moov')";
                    result.Description = containsIcpf
                        ? "El contenedor no se cerró correctamente al grabar, pero se encontraron tramas ProRes válidas. Se recomienda repararlo y reconstruirlo."
                        : "El archivo carece del índice 'moov' indispensable. Se recomienda reconstruir el contenedor con el módulo de recuperación.";
                }
                else if (containsIcpf)
                {
                    result.Title = "Contenedor dañado con tramas ProRes detectadas";
                    result.Description = "El video no puede ser leído directamente, pero contiene secuencias ProRes recuperables. Use la herramienta de reparación.";
                }
                else
                {
                    result.Title = "Fallas detectadas en el archivo de video";
                    result.Description = string.IsNullOrWhiteSpace(shortError) 
                        ? "El archivo está incompleto o dañado y no puede ser procesado directamente."
                        : $"FFmpeg reportó fallas de lectura: \"{shortError}\". Se recomienda repararlo antes de convertir.";
                }

                return result;
            }

            // 2. Si el contenedor abrió, hacer prueba rápida de decodificación de los primeros segundos
            var decodeResult = await RunProcessCaptureAsync("ffmpeg", 
                $"-v error -xerror -i \"{filePath}\" -t 3 -f null -", 
                cancellationToken);

            if (decodeResult.ExitCode != 0 || !string.IsNullOrWhiteSpace(decodeResult.StdErr))
            {
                result.HasErrors = true;
                result.RawErrorDetails = decodeResult.StdErr;
                string shortError = CleanErrorMessage(decodeResult.StdErr);

                result.Title = "Fallas en el flujo de video al decodificar";
                result.Description = string.IsNullOrWhiteSpace(shortError)
                    ? "Se detectaron cuadros o paquetes dañados en el inicio del video. Podría fallar durante la conversión."
                    : $"Error de decodificación: \"{shortError}\". Podría requerir reparación previa.";
                return result;
            }

            // Sin fallas detectadas
            result.HasErrors = false;
            return result;
        }
        catch (OperationCanceledException)
        {
            return result;
        }
        catch (Exception ex)
        {
            result.HasErrors = true;
            result.Title = "Error al verificar archivo";
            result.Description = ex.Message;
            return result;
        }
    }

    private static async Task<bool> ScanForProResMagicAsync(string filePath, int maxBytesToScan, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
            byte[] buffer = new byte[64 * 1024];
            int totalScanned = 0;
            byte[] tail = [];

            while (totalScanned < maxBytesToScan && !ct.IsCancellationRequested)
            {
                int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (bytesRead == 0) break;

                int combinedLen = tail.Length + bytesRead;
                byte[] block = new byte[combinedLen];
                Buffer.BlockCopy(tail, 0, block, 0, tail.Length);
                Buffer.BlockCopy(buffer, 0, block, tail.Length, bytesRead);

                if (block.AsSpan().IndexOf(MagicIcpf) >= 0)
                {
                    return true;
                }

                totalScanned += bytesRead;
                int keep = Math.Min(combinedLen, 8);
                tail = new byte[keep];
                Buffer.BlockCopy(block, combinedLen - keep, tail, 0, keep);
            }
        }
        catch
        {
            // Ignorar errores de acceso
        }

        return false;
    }

    private static string CleanErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return string.Empty;
        
        string line = lines[0];
        if (line.Length > 120) line = line[..120] + "...";
        return line;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessCaptureAsync(string fileName, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = new Process { StartInfo = psi };
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

        if (!proc.Start()) return (-1, "", "No se pudo iniciar el proceso");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        });

        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, sbOut.ToString().Trim(), sbErr.ToString().Trim());
    }
}
