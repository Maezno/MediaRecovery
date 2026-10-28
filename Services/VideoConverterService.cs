using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MediaRecovery.Services;

namespace MediaRecovery.Services;

public enum HardwareGpuChoice
{
    Auto,
    Nvidia,
    Amd,
    Cpu
}

public enum DeinterlaceMode
{
    Deinterlace25p,  // Recomendado para ProRes Blackmagic (yadif=mode=0:parity=auto:deint=all)
    Auto,            // Solo si se detectan cuadros entrelazados (yadif=mode=0:parity=auto:deint=interlaced)
    Progressive,     // No tocar metraje
    Deinterlace50p   // Doble de cuadros (yadif=mode=1:parity=auto:deint=all)
}

public record ConversionJob(
    string InputPath,
    string OutputPath,
    string Codec = "h264",
    int BitrateMbps = 25,
    HardwareGpuChoice Gpu = HardwareGpuChoice.Auto,
    DeinterlaceMode Deint = DeinterlaceMode.Deinterlace25p,
    bool IncludeAudio = true,
    string Resolution = "original",
    bool UseTimeRange = false,
    string? StartTime = null,
    string? EndTime = null
);

public class VideoConverterService
{
    private readonly ProcessRunnerService _runner = new();

    public async Task<HardwareGpuChoice> DetectAvailableGpuAsync(CancellationToken cancellationToken = default)
    {
        // Prueba Nvidia NVENC
        if (await TestEncoderAsync("h264_nvenc", cancellationToken))
            return HardwareGpuChoice.Nvidia;

        // Prueba AMD AMF
        if (await TestEncoderAsync("h264_amf", cancellationToken))
            return HardwareGpuChoice.Amd;

        return HardwareGpuChoice.Cpu;
    }

    private static async Task<bool> TestEncoderAsync(string encoder, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunnerService.CaptureAsync("ffmpeg", $"-hide_banner -loglevel error -f lavfi -i nullsrc=s=1920x1080 -t 0.1 -c:v {encoder} -f null -", ct);
            return res.ExitCode == 0;
        }
        catch { return false; }
    }

    public async Task<int> ConvertVideoAsync(
        ConversionJob job,
        Action<string>? onLog = null,
        Action<double, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        string input = job.InputPath;
        string output = job.OutputPath;
        int bitrate = job.BitrateMbps;
        string bk = $"{bitrate}M";
        string bufk = $"{bitrate * 2}M";

        var chosenGpu = job.Gpu;
        if (chosenGpu == HardwareGpuChoice.Auto)
        {
            chosenGpu = await DetectAvailableGpuAsync(cancellationToken);
            onLog?.Invoke($"[GPU] Detección automática seleccionó: {chosenGpu}");
        }

        // Selección de Encoder y parámetros basados en v5
        string encoder;
        var encOpts = new List<string>();

        if (job.Codec.Equals("prores", StringComparison.OrdinalIgnoreCase))
        {
            encoder = "prores_ks";
            encOpts.AddRange(new[] { "-profile:v", "3", "-vendor", "apl0", "-pix_fmt", "yuv422p10le" });
            output = Path.ChangeExtension(output, ".mov");
        }
        else if (job.Codec.Equals("h265", StringComparison.OrdinalIgnoreCase))
        {
            if (chosenGpu == HardwareGpuChoice.Nvidia)
            {
                encoder = "hevc_nvenc";
                encOpts.AddRange(new[] { "-rc", "cbr", "-b:v", bk, "-maxrate", bk, "-bufsize", bufk, "-profile:v", "main", "-preset", "p4", "-tune", "hq" });
            }
            else if (chosenGpu == HardwareGpuChoice.Amd)
            {
                encoder = "hevc_amf";
                encOpts.AddRange(new[] { "-rc", "cbr", "-b:v", bk, "-maxrate", bk, "-bufsize", bufk, "-quality", "speed" });
            }
            else
            {
                encoder = "libx265";
                encOpts.AddRange(new[] { "-b:v", bk, "-maxrate", bk, "-bufsize", bufk, "-preset", "medium", "-x265-params", "nal-hrd=cbr" });
            }
            output = Path.ChangeExtension(output, ".mp4");
        }
        else // H.264
        {
            if (chosenGpu == HardwareGpuChoice.Nvidia)
            {
                // Lógica de V5 para máxima fidelidad con NVENC
                encoder = "h264_nvenc";
                encOpts.AddRange(new[]
                {
                    "-rc", "cbr",
                    "-b:v", bk,
                    "-maxrate", bk,
                    "-bufsize", bufk,
                    "-profile:v", "high",
                    "-level", "4.2",
                    "-preset", "p4",
                    "-tune", "hq",
                    "-rc-lookahead", "32"
                });
            }
            else if (chosenGpu == HardwareGpuChoice.Amd)
            {
                encoder = "h264_amf";
                encOpts.AddRange(new[]
                {
                    "-rc", "cbr",
                    "-b:v", bk,
                    "-maxrate", bk,
                    "-bufsize", bufk,
                    "-profile:v", "high",
                    "-level", "4.2",
                    "-quality", "speed",
                    "-preanalysis", "false",
                    "-vbaq", "false"
                });
            }
            else
            {
                encoder = "libx264";
                encOpts.AddRange(new[]
                {
                    "-b:v", bk,
                    "-maxrate", bk,
                    "-bufsize", bufk,
                    "-profile:v", "high",
                    "-level", "4.2",
                    "-preset", "medium"
                });
            }
            output = Path.ChangeExtension(output, ".mp4");
        }

        // Filtro de desentrelazado (yadif de v5)
        var filters = new List<string>();
        string fpsSyncOpts = "";

        switch (job.Deint)
        {
            case DeinterlaceMode.Deinterlace25p:
                filters.Add("yadif=mode=0:parity=auto:deint=all");
                fpsSyncOpts = "-fps_mode cfr -r 25 ";
                break;
            case DeinterlaceMode.Auto:
                filters.Add("yadif=mode=0:parity=auto:deint=interlaced");
                break;
            case DeinterlaceMode.Deinterlace50p:
                // mode=1 duplica a 50 fps; sincronizamos el contenedor a CFR 50 exactos para evitar tirones/stuttering
                filters.Add("yadif=mode=1:parity=auto:deint=all");
                fpsSyncOpts = "-fps_mode cfr -r 50 ";
                break;
            case DeinterlaceMode.Progressive:
            default:
                break;
        }

        if (!string.IsNullOrWhiteSpace(job.Resolution) && !job.Resolution.Equals("original", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add($"scale={job.Resolution}");
        }

        if (job.Codec.Equals("prores", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add("format=yuv422p10le");
        }
        else
        {
            filters.Add("format=yuv420p");
        }

        string vf = filters.Count > 0 ? $"-vf \"{string.Join(",", filters)}\" " : "";

        // Opciones de audio (stereo pan mixer de v5)
        string audioOpts = job.IncludeAudio
            ? "-c:a aac -b:a 320k -ac 2 -af \"pan=stereo|c0=0.5*c0+0.5*c2+0.5*c4+0.5*c6|c1=0.5*c1+0.5*c3+0.5*c5+0.5*c7\" "
            : "-an ";

        string timeOpts = "";
        double customCutDuration = 0;
        if (job.UseTimeRange && !string.IsNullOrWhiteSpace(job.StartTime) && !string.IsNullOrWhiteSpace(job.EndTime))
        {
            timeOpts = $"-ss {job.StartTime} -to {job.EndTime} ";
            if (TimeSpan.TryParse(job.StartTime, out var tsStart) && TimeSpan.TryParse(job.EndTime, out var tsEnd))
            {
                customCutDuration = Math.Max(0, (tsEnd - tsStart).TotalSeconds);
            }
        }

        string movflags = job.Codec.Equals("prores", StringComparison.OrdinalIgnoreCase) ? "" : "-movflags +faststart ";
        string fmt = job.Codec.Equals("prores", StringComparison.OrdinalIgnoreCase) ? "mov" : "mp4";

        // NOTA TÉCNICA: Colocar timeOpts DESPUÉS de -i garantiza que la cadena de desentrelazado
        // (yadif) decodifique con la cadencia de campos original intacta, evitando la inversión
        // temporal de campos (judder/stuttering) que provoca el seek previo a nivel de demuxer.
        string arguments = $"-y -probesize 100M -analyzeduration 50M -threads 0 -i \"{input}\" "
                         + timeOpts
                         + vf
                         + fpsSyncOpts
                         + $"-c:v {encoder} {string.Join(" ", encOpts)} "
                         + audioOpts
                         + movflags
                         + $"-f {fmt} \"{output}\"";

        onLog?.Invoke($"[Inicio] Codificando con {encoder.ToUpperInvariant()} ({job.Codec.ToUpperInvariant()}) -> {Path.GetFileName(output)}");

        // Obtener la duración total real con ffprobe antes de iniciar
        double inputDuration = await ProbeDurationSecondsAsync(input, cancellationToken);
        double totalDurationSeconds = customCutDuration > 0 ? customCutDuration : inputDuration;

        double cutStartSeconds = 0;
        if (job.UseTimeRange && !string.IsNullOrWhiteSpace(job.StartTime) && TimeSpan.TryParse(job.StartTime, out var tsStartParsed))
        {
            cutStartSeconds = tsStartParsed.TotalSeconds;
        }

        var durationRegex = new Regex(@"Duration:\s+(\d+):(\d+):([\d\.]+)");
        var timeRegex = new Regex(@"time=(\d+):(\d+):([\d\.]+)");
        var fpsRegex = new Regex(@"fps=\s*([\d\.]+)");

        return await _runner.RunAsync(
            "ffmpeg",
            arguments,
            onOutputLine: line =>
            {
                LogFiltered(line);
                ProcessProgress(line);
            },
            onErrorLine: line =>
            {
                LogFiltered(line);
                ProcessProgress(line);
            },
            cancellationToken: cancellationToken);

        void LogFiltered(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // Omitir líneas periódicas de telemetría ya representadas en la barra de estado y progreso
            if (line.Contains("frame=") || line.Contains("size=") || line.Contains("bitrate=") || line.Contains("speed=")) return;
            
            // Omitir banners informativos de compilación y librerías de FFmpeg
            if (line.StartsWith("ffmpeg version") || line.StartsWith("  built with") || line.StartsWith("  configuration:") || line.StartsWith("  lib")) return;

            // Mantener avisos relevantes, errores o cambios de estado
            if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) || 
                line.Contains("Warning", StringComparison.OrdinalIgnoreCase) || 
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Stream #") ||
                line.Contains("Input #") ||
                line.Contains("Output #"))
            {
                onLog?.Invoke(line.Trim());
            }
        }

        void ProcessProgress(string line)
        {
            if (totalDurationSeconds <= 0)
            {
                var dMatch = durationRegex.Match(line);
                if (dMatch.Success &&
                    double.TryParse(dMatch.Groups[1].Value, out double h) &&
                    double.TryParse(dMatch.Groups[2].Value, out double m) &&
                    double.TryParse(dMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture, out double s))
                {
                    totalDurationSeconds = h * 3600 + m * 60 + s;
                }
            }

            if (line.Contains("frame=") && totalDurationSeconds > 0)
            {
                var tMatch = timeRegex.Match(line);
                if (tMatch.Success &&
                    double.TryParse(tMatch.Groups[1].Value, out double h) &&
                    double.TryParse(tMatch.Groups[2].Value, out double m) &&
                    double.TryParse(tMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture, out double s))
                {
                    double rawTime = h * 3600 + m * 60 + s;
                    
                    // Si se usó corte con -ss después de -i, FFmpeg puede reportar el timestamp absoluto o relativo.
                    // Si rawTime >= cutStartSeconds y estamos cortando, el avance relativo dentro del corte es (rawTime - cutStartSeconds)
                    double processedSeconds = rawTime;
                    if (job.UseTimeRange && cutStartSeconds > 0 && rawTime >= cutStartSeconds)
                    {
                        processedSeconds = rawTime - cutStartSeconds;
                    }

                    double pct = Math.Clamp((processedSeconds / totalDurationSeconds) * 100.0, 0, 99.9);

                    string fpsStr = "0";
                    var fpsMatch = fpsRegex.Match(line);
                    if (fpsMatch.Success) fpsStr = fpsMatch.Groups[1].Value;

                    // Cálculo de tiempo restante estimado (ETA)
                    string etaText = "";
                    if (double.TryParse(fpsStr, System.Globalization.CultureInfo.InvariantCulture, out double currentFps) && currentFps > 0)
                    {
                        double remainingSec = Math.Max(0, totalDurationSeconds - processedSeconds);
                        // Suponiendo 25 o 50 fps base para el flujo de video procesado por segundo
                        double targetFps = job.Deint == DeinterlaceMode.Deinterlace50p ? 50.0 : 25.0;
                        double speedRatio = currentFps / targetFps;
                        if (speedRatio > 0.05)
                        {
                            double etaSeconds = remainingSec / speedRatio;
                            etaText = $" | Restante: ~{FormatTime(etaSeconds)}";
                        }
                    }

                    onProgress?.Invoke(pct, $"{pct:0.0}% | fps: {fpsStr} | {FormatTime(processedSeconds)} / {FormatTime(totalDurationSeconds)}{etaText}");
                }
            }
        }
    }

    private static string FormatTime(double totalSeconds)
    {
        if (totalSeconds < 0 || double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "00:00";
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    public record ScanTypeInfo(string Codec, string FieldOrder, bool IsInterlaced, string Method);

    /// <summary>
    /// Detecta si el video es entrelazado o progresivo.
    /// 1) Lee field_order del stream con ffprobe (rápido).
    /// 2) Si es desconocido, analiza ~300 cuadros con el filtro idet de ffmpeg.
    /// </summary>
    public static async Task<ScanTypeInfo?> DetectScanTypeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            string probe = await RunCaptureAsync("ffprobe",
                $"-v error -select_streams v:0 -show_entries stream=codec_name,field_order -of default=noprint_wrappers=1 \"{filePath}\"",
                captureStdErr: false, cancellationToken);

            string codec = "", fieldOrder = "";
            foreach (var raw in probe.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("codec_name=")) codec = line[11..];
                else if (line.StartsWith("field_order=")) fieldOrder = line[12..];
            }

            if (string.IsNullOrEmpty(codec)) return null;

            if (fieldOrder is "tt" or "bb" or "tb" or "bt")
                return new ScanTypeInfo(codec, fieldOrder, true, "metadatos");
            if (fieldOrder == "progressive")
                return new ScanTypeInfo(codec, fieldOrder, false, "metadatos");

            // Metadato ausente: análisis real de cuadros con idet
            string idet = await RunCaptureAsync("ffmpeg",
                $"-hide_banner -nostats -i \"{filePath}\" -map 0:v:0 -frames:v 300 -vf idet -an -f null -",
                captureStdErr: true, cancellationToken);

            var m = Regex.Match(idet, @"Multi frame detection:\s*TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)");
            if (m.Success)
            {
                long tff = long.Parse(m.Groups[1].Value), bff = long.Parse(m.Groups[2].Value), prog = long.Parse(m.Groups[3].Value);
                bool interlaced = (tff + bff) > prog;
                string fo = interlaced ? (tff >= bff ? "tt" : "bb") : "progressive";
                return new ScanTypeInfo(codec, fo, interlaced, "análisis idet");
            }

            return new ScanTypeInfo(codec, "unknown", false, "indeterminado");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> RunCaptureAsync(string exe, string args, bool captureStdErr, CancellationToken ct)
    {
        var res = await ProcessRunnerService.CaptureAsync(exe, args, ct);
        return captureStdErr ? res.StdErr : res.StdOut;
    }

    private static async Task<double> ProbeDurationSecondsAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunnerService.CaptureAsync("ffprobe", $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"", ct);
            if (double.TryParse(res.StdOut, System.Globalization.CultureInfo.InvariantCulture, out double dur)) return dur;
        }
        catch { }
        return 0;
    }
}
