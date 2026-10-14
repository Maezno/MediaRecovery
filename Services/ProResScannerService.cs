using System.Buffers.Binary;
using System.IO;
using MediaRecovery.Models;

namespace MediaRecovery.Services;

public class ProResScannerService
{
    // Firma mágica 'icpf' (0x69, 0x63, 0x70, 0x66)
    private static readonly byte[] MagicIcpf = [0x69, 0x63, 0x70, 0x66];

    public const int DefaultFrameMin = 150_000;
    public const int DefaultFrameMax = 1_200_000;
    public const int DefaultBufferSize = 64 * 1024 * 1024; // 64 MB

    public async Task<List<FrameInfo>> ScanFramesAsync(
        string filePath,
        int frameMin = DefaultFrameMin,
        int frameMax = DefaultFrameMax,
        int bufferSize = DefaultBufferSize,
        IProgress<ScanProgressReport>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var frames = new List<FrameInfo>();
        var fileInfo = new FileInfo(filePath);
        long fileSize = fileInfo.Length;

        log?.Invoke($"Iniciando escaneo ProRes en: {fileInfo.Name} ({fileSize / (1024.0 * 1024.0 * 1024.0):F2} GB)");
        log?.Invoke($"Buscando firma icpf en chunks de {bufferSize / (1024 * 1024)} MB...");

        int overlap = frameMax + 8;
        byte[] tail = [];
        long totalRead = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var reportWatch = System.Diagnostics.Stopwatch.StartNew();

        await Task.Run(async () =>
        {
            byte[] readBuffer = new byte[bufferSize];

            await using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await fileStream.ReadAsync(readBuffer.AsMemory(0, bufferSize), cancellationToken);
                if (bytesRead == 0) break;

                // Combinar residuo (tail) con el nuevo bloque
                int combinedLength = tail.Length + bytesRead;
                byte[] block = new byte[combinedLength];
                Buffer.BlockCopy(tail, 0, block, 0, tail.Length);
                Buffer.BlockCopy(readBuffer, 0, block, tail.Length, bytesRead);

                long baseOffset = totalRead - tail.Length;
                ReadOnlySpan<byte> blockSpan = block.AsSpan();

                int p = 0;
                while (p <= blockSpan.Length - 8)
                {
                    int idx = blockSpan[p..].IndexOf(MagicIcpf);
                    if (idx == -1) break;

                    int matchPos = p + idx;
                    int frameStart = matchPos - 4;

                    if (frameStart < 0)
                    {
                        p = matchPos + 4;
                        continue;
                    }

                    // En ProRes el tamaño del átomo está en Big-Endian 4 bytes antes de 'icpf'
                    uint frameLen = BinaryPrimitives.ReadUInt32BigEndian(blockSpan.Slice(frameStart, 4));

                    if (frameLen >= frameMin && frameLen <= frameMax)
                    {
                        long globalOffset = baseOffset + frameStart;
                        if (frames.Count == 0 || globalOffset > frames[^1].Offset)
                        {
                            frames.Add(new FrameInfo(globalOffset, (int)frameLen));
                        }
                        p = (int)(frameStart + frameLen);
                    }
                    else
                    {
                        p = matchPos + 4;
                    }
                }

                totalRead += bytesRead;

                // Guardar la cola para el próximo ciclo
                int keep = Math.Min(combinedLength, overlap);
                tail = new byte[keep];
                Buffer.BlockCopy(block, combinedLength - keep, tail, 0, keep);

                // Reporte periódico cada 1 segundo
                if (reportWatch.ElapsedMilliseconds >= 1000)
                {
                    double pct = fileSize > 0 ? (double)totalRead / fileSize * 100.0 : 0.0;
                    double elapsedSec = Math.Max(0.1, stopwatch.Elapsed.TotalSeconds);
                    double speedMBs = (totalRead / (1024.0 * 1024.0)) / elapsedSec;
                    double remainingBytes = Math.Max(0, fileSize - totalRead);
                    double etaMin = speedMBs > 0 ? (remainingBytes / (1024.0 * 1024.0)) / speedMBs / 60.0 : 0;

                    progress?.Report(new ScanProgressReport
                    {
                        Percentage = pct,
                        SpeedMBs = speedMBs,
                        EtaMinutes = etaMin,
                        FramesFound = frames.Count,
                        BytesRead = totalRead,
                        StatusText = $"Escaneando {pct:F1}% | {speedMBs:F0} MB/s | ~{etaMin:F1} min | {frames.Count:N0} frames"
                    });

                    reportWatch.Restart();
                }
            }
        }, cancellationToken);

        double totalDurationSec = frames.Count / 25.0; // 25 FPS por defecto
        log?.Invoke($"\n=== ESCANEO FINALIZADO ===");
        log?.Invoke($"Frames encontrados : {frames.Count:N0}");
        log?.Invoke($"Duración estimada  : {totalDurationSec / 3600.0:F2} hs ({totalDurationSec / 60.0:F1} min)");
        if (frames.Count > 0)
        {
            long avg = (long)frames.Average(f => f.Length);
            long totalBytes = frames.Sum(f => (long)f.Length);
            log?.Invoke($"Tamaño promedio frame : {avg:N0} bytes");
            log?.Invoke($"Total de video neto    : {totalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB");
        }

        return frames;
    }

    public async Task<AudioInfo?> AnalyzeAudioAsync(string filePath, List<FrameInfo> frames, Action<string>? log = null)
    {
        if (frames.Count == 0) return null;

        log?.Invoke("\nAnalizando zona de audio...");
        var lastFrame = frames[^1];
        long audioStart = lastFrame.Offset + lastFrame.Length;

        var fi = new FileInfo(filePath);
        long available = fi.Length - audioStart;
        const int chunkAudio = 49_152;
        const int sampleRate = 48_000;

        if (available < chunkAudio)
        {
            log?.Invoke("[!] Sin datos suficientes de audio después del video.");
            return null;
        }

        byte[] sample = new byte[Math.Min(1024, (int)available)];
        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Seek(audioStart, SeekOrigin.Begin);
            _ = await fs.ReadAsync(sample.AsMemory(0, sample.Length));
        }

        if (sample.All(b => b == 0))
        {
            log?.Invoke("[!] Zona de audio contiene solo ceros.");
            return null;
        }

        if (sample.AsSpan().IndexOf(MagicIcpf) != -1)
        {
            log?.Invoke("[!] Zona de audio contiene cabeceras de video ProRes.");
            return null;
        }

        int chunkCount = (int)Math.Min(available / chunkAudio, frames.Count * 2);
        double audioDuration = (double)chunkCount * 1024.0 / sampleRate;
        double videoDuration = frames.Count / 25.0;
        double audioPct = videoDuration > 0 ? (audioDuration / videoDuration) * 100.0 : 0.0;
        bool isComplete = audioPct >= 90.0;

        log?.Invoke($"Audio detectado en offset: {audioStart:N0}");
        log?.Invoke($"  Chunks: {chunkCount:N0} | Duración: {audioDuration / 60.0:F1} min ({audioPct:F0}%)");
        log?.Invoke($"  Estado: {(isComplete ? "COMPLETO" : "PARCIAL")}");

        return new AudioInfo(audioStart, chunkCount, audioDuration, isComplete);
    }
}
