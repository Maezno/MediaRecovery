using System.Diagnostics;
using System.Text;

namespace MediaRecovery.Services;

public class ProcessRunnerService
{
    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments) => new()
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    public static async Task<(int ExitCode, string StdOut, string StdErr)> CaptureAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default)
    {
        using var proc = new Process { StartInfo = CreateStartInfo(fileName, arguments) };
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

    public async Task<int> RunAsync(
        string fileName,
        string arguments,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        Action<double>? onProgressPercentage = null,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments) };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            onOutputLine?.Invoke(e.Data);
            TryExtractProgress(e.Data, onProgressPercentage);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            onErrorLine?.Invoke(e.Data);
            TryExtractProgress(e.Data, onProgressPercentage);
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"No se pudo iniciar el proceso: {fileName}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { }
            });

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }

    private static void TryExtractProgress(string line, Action<double>? onProgressPercentage)
    {
        if (onProgressPercentage == null) return;

        // Patrón yt-dlp: "[download]  45.2% of 120.5MiB at 5.2MiB/s"
        if (line.Contains("[download]") && line.Contains('%'))
        {
            int pctIdx = line.IndexOf('%');
            int spaceBefore = line.LastIndexOf(' ', pctIdx - 1);
            if (spaceBefore != -1 && double.TryParse(line.Substring(spaceBefore + 1, pctIdx - spaceBefore - 1),
                System.Globalization.CultureInfo.InvariantCulture, out double pct))
            {
                onProgressPercentage(pct);
            }
        }
    }
}
