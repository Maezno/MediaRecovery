using System.Buffers.Binary;
using System.IO;
using MediaRecovery.Models;

namespace MediaRecovery.Services;

public class MovBuilderService
{
    private const int Fps = 25;
    private const int Width = 1920;
    private const int Height = 1080;
    private const int TicksPerFrame = 100;
    private const int ChunkAudio = 49_152;
    private const int SampleRate = 48_000;
    private const int Channels = 16;
    private const int ReportEvery = 500;

    public async Task<bool> BuildMovAsync(
        string sourcePath,
        string outputPath,
        List<FrameInfo> frames,
        AudioInfo? audioInfo,
        bool includeAudio,
        IProgress<double>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (frames.Count == 0)
        {
            log?.Invoke("[!] No hay frames para construir el MOV.");
            return false;
        }

        byte[] ftyp = BuildFtyp();
        const int ftypSize = 20;
        const int mdatHdr = 16;
        int audioChunks = (includeAudio && audioInfo != null) ? audioInfo.ChunkCount : 0;
        long totalVidSize = frames.Sum(f => (long)f.Length);
        long totalAudSize = (long)audioChunks * ChunkAudio;
        long currentOffset = ftypSize + mdatHdr;

        var framesOut = new List<(long Offset, int Length)>(frames.Count);
        foreach (var frame in frames)
        {
            framesOut.Add((currentOffset, frame.Length));
            currentOffset += frame.Length;
        }

        var audOffsetsOut = new List<long>(audioChunks);
        if (includeAudio && audioInfo != null)
        {
            for (int i = 0; i < audioChunks; i++)
            {
                audOffsetsOut.Add(currentOffset);
                currentOffset += ChunkAudio;
            }
        }

        byte[] moov = BuildMoov(framesOut, Fps, Width, Height,
            includeAudio ? audioInfo : null,
            includeAudio ? audOffsetsOut : null);

        long estimatedSize = ftypSize + mdatHdr + totalVidSize + totalAudSize + moov.Length;
        log?.Invoke($"Construyendo MOV: {outputPath}");
        log?.Invoke($"Tamaño estimado: {estimatedSize / (1024.0 * 1024.0 * 1024.0):F3} GB");
        log?.Invoke($"Audio: {(includeAudio && audioInfo != null ? $"Incluido ({audioChunks:N0} chunks)" : "Omitido")}");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await Task.Run(async () =>
            {
                await using var srcStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                await using var dstStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);

                // 1. Escribir ftyp
                await dstStream.WriteAsync(ftyp, cancellationToken);

                // 2. Encabezado temporal mdat (tamaño se actualiza al final)
                long mdatHdrOffset = dstStream.Position;
                await dstStream.WriteAsync(PackUInt32(1), cancellationToken); // Large size flag
                await dstStream.WriteAsync("mdat"u8.ToArray(), cancellationToken);
                await dstStream.WriteAsync(PackUInt64(0), cancellationToken);

                long bytesWrittenData = 0;
                byte[] frameBuffer = new byte[ProResScannerService.DefaultFrameMax];

                // 3. Escribir frames de video
                for (int i = 0; i < frames.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    var f = frames[i];
                    srcStream.Seek(f.Offset, SeekOrigin.Begin);
                    int bytesRead = await srcStream.ReadAsync(frameBuffer.AsMemory(0, f.Length), cancellationToken);
                    await dstStream.WriteAsync(frameBuffer.AsMemory(0, bytesRead), cancellationToken);
                    bytesWrittenData += bytesRead;

                    if (i % ReportEvery == 0)
                    {
                        double pct = (double)i / frames.Count * (includeAudio && audioInfo != null ? 90.0 : 98.0);
                        progress?.Report(pct);
                    }
                }

                // 4. Escribir audio si aplica
                if (includeAudio && audioInfo != null)
                {
                    log?.Invoke("Escribiendo pistas de audio LPCM...");
                    srcStream.Seek(audioInfo.Offset, SeekOrigin.Begin);
                    byte[] audioBuffer = new byte[ChunkAudio];

                    for (int i = 0; i < audioChunks; i++)
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        int read = await srcStream.ReadAsync(audioBuffer.AsMemory(0, ChunkAudio), cancellationToken);
                        await dstStream.WriteAsync(audioBuffer.AsMemory(0, read), cancellationToken);
                        bytesWrittenData += read;

                        if (i % 2000 == 0)
                        {
                            double pct = 90.0 + ((double)i / audioChunks * 8.0);
                            progress?.Report(pct);
                        }
                    }
                }

                // 5. Corregir tamaño del átomo mdat
                long realMdatSize = 16 + bytesWrittenData;
                dstStream.Seek(mdatHdrOffset, SeekOrigin.Begin);
                await dstStream.WriteAsync(PackUInt32(1), cancellationToken);
                await dstStream.WriteAsync("mdat"u8.ToArray(), cancellationToken);
                await dstStream.WriteAsync(PackUInt64((ulong)realMdatSize), cancellationToken);

                // 6. Escribir átomo moov al final
                dstStream.Seek(0, SeekOrigin.End);
                await dstStream.WriteAsync(moov, cancellationToken);
            }, cancellationToken);

            progress?.Report(100.0);
            var fi = new FileInfo(outputPath);
            log?.Invoke($"[OK] MOV finalizado: {fi.Length / (1024.0 * 1024.0 * 1024.0):F3} GB en {stopwatch.Elapsed.TotalMinutes:F1} min.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Falla al escribir MOV: {ex.Message}");
            return false;
        }
    }

    #region Atom Helpers
    private static byte[] PackUInt32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    private static byte[] PackUInt16(ushort value)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        return b;
    }

    private static byte[] PackUInt64(ulong value)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        return b;
    }

    private static byte[] MakeAtom(string name, byte[] data)
    {
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        byte[] result = new byte[data.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)result.Length);
        Buffer.BlockCopy(nameBytes, 0, result, 4, 4);
        Buffer.BlockCopy(data, 0, result, 8, data.Length);
        return result;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        int total = arrays.Sum(a => a.Length);
        byte[] res = new byte[total];
        int offset = 0;
        foreach (var arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, res, offset, arr.Length);
            offset += arr.Length;
        }
        return res;
    }

    private static byte[] BuildFtyp()
    {
        return Concat(
            PackUInt32(20),
            "ftypqt  "u8.ToArray(),
            PackUInt32(0x200),
            "qt  "u8.ToArray()
        );
    }

    private static byte[] BuildMoov(
        List<(long Offset, int Length)> framesOut,
        int fps, int width, int height,
        AudioInfo? audioInfo,
        List<long>? audOffsetsOut)
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2082844800u;
        uint n = (uint)framesOut.Count;
        uint ts = (uint)(fps * 100);
        uint dur = n * TicksPerFrame;
        uint trackCount = (uint)((audioInfo != null && audOffsetsOut != null) ? 2 : 1);

        byte[] mvhd = MakeAtom("mvhd", Concat(
            new byte[4],
            PackUInt32(now), PackUInt32(now),
            PackUInt32(ts), PackUInt32(dur),
            PackUInt32(0x00010000), PackUInt16(0x0100), new byte[10],
            PackUInt32(0x00010000), new byte[12],
            PackUInt32(0x00010000), new byte[12],
            PackUInt32(0x40000000),
            new byte[24],
            PackUInt32(trackCount + 1)
        ));

        byte[] trakV = BuildTrakVideo(framesOut, fps, width, height, trackId: 1, durTicks: dur, now: now);

        if (audioInfo != null && audOffsetsOut != null)
        {
            byte[] trakA = BuildTrakAudio(audOffsetsOut, (uint)audioInfo.ChunkCount, SampleRate, Channels, dur, fps, trackId: 2, now: now);
            return MakeAtom("moov", Concat(mvhd, trakV, trakA));
        }

        return MakeAtom("moov", Concat(mvhd, trakV));
    }

    private static byte[] BuildTrakVideo(
        List<(long Offset, int Length)> framesOut,
        int fps, int width, int height,
        uint trackId, uint durTicks, uint now)
    {
        uint n = (uint)framesOut.Count;
        uint ts = (uint)(fps * 100);

        byte[] tkhd = MakeAtom("tkhd", Concat(
            [0x00, 0x00, 0x00, 0x03],
            PackUInt32(now), PackUInt32(now),
            PackUInt32(trackId), new byte[4],
            PackUInt32(durTicks), new byte[8],
            new byte[6], new byte[2],
            [0x00, 0x01, 0x00, 0x00], new byte[8],
            [0x00, 0x00, 0x00, 0x00],
            [0x00, 0x01, 0x00, 0x00], new byte[8],
            [0x00, 0x00, 0x00, 0x00],
            [0x40, 0x00, 0x00, 0x00],
            PackUInt32((uint)(width << 16)), PackUInt32((uint)(height << 16))
        ));

        byte[] mdhd = MakeAtom("mdhd", Concat(
            new byte[4],
            PackUInt32(now), PackUInt32(now),
            PackUInt32(ts), PackUInt32(durTicks),
            new byte[4]
        ));

        byte[] hdlr = MakeAtom("hdlr", Concat(
            new byte[8], "vide"u8.ToArray(), new byte[12],
            System.Text.Encoding.ASCII.GetBytes("Apple Video Media Handler\0")
        ));

        byte[] apcn = MakeAtom("apcn", Concat(
            new byte[6], PackUInt16(1), new byte[8],
            PackUInt16((ushort)width), PackUInt16((ushort)height),
            PackUInt32(0x00480000), PackUInt32(0x00480000), PackUInt32(0), PackUInt16(1),
            "\x0cApple ProRes\0"u8.ToArray(), new byte[18],
            PackUInt16(0x0018), [0xFF, 0xFF]
        ));

        byte[] stsd = MakeAtom("stsd", Concat(new byte[4], PackUInt32(1), apcn));
        byte[] stts = MakeAtom("stts", Concat(new byte[4], PackUInt32(1), PackUInt32(n), PackUInt32(TicksPerFrame)));
        byte[] stsc = MakeAtom("stsc", Concat(new byte[4], PackUInt32(1), PackUInt32(1), PackUInt32(1), PackUInt32(1)));

        byte[] stszData = new byte[8 + n * 4];
        BinaryPrimitives.WriteUInt32BigEndian(stszData.AsSpan(4, 4), n);
        for (int i = 0; i < framesOut.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(stszData.AsSpan(8 + i * 4, 4), (uint)framesOut[i].Length);
        }
        byte[] stsz = MakeAtom("stsz", stszData);

        byte[] co = BuildChunkOffsets(framesOut.Select(f => f.Offset).ToList());
        byte[] dref = MakeAtom("dref", Concat(new byte[4], PackUInt32(1), MakeAtom("url ", [0x00, 0x00, 0x00, 0x01])));
        byte[] vmhd = MakeAtom("vmhd", Concat([0x00, 0x00, 0x00, 0x01], new byte[6]));
        byte[] stbl = MakeAtom("stbl", Concat(stsd, stts, stsc, stsz, co));
        byte[] minf = MakeAtom("minf", Concat(vmhd, MakeAtom("dinf", dref), stbl));
        byte[] mdia = MakeAtom("mdia", Concat(mdhd, hdlr, minf));

        return MakeAtom("trak", Concat(tkhd, mdia));
    }

    private static byte[] BuildTrakAudio(
        List<long> audioOffsets,
        uint chunkCount, uint sampleRate, uint channels,
        uint durVideoTicks, int fps, uint trackId, uint now)
    {
        double durVideoSec = (double)durVideoTicks / (fps * 100);
        uint durAudioSamples = (uint)(durVideoSec * sampleRate);

        byte[] tkhd = MakeAtom("tkhd", Concat(
            [0x00, 0x00, 0x00, 0x03],
            PackUInt32(now), PackUInt32(now),
            PackUInt32(trackId), new byte[4],
            PackUInt32(durVideoTicks), new byte[8],
            new byte[6], new byte[2],
            [0x00, 0x01, 0x00, 0x00], new byte[8],
            [0x00, 0x00, 0x00, 0x00],
            [0x00, 0x01, 0x00, 0x00], new byte[8],
            [0x00, 0x00, 0x00, 0x00],
            [0x40, 0x00, 0x00, 0x00],
            PackUInt32(0), PackUInt32(0)
        ));

        byte[] mdhd = MakeAtom("mdhd", Concat(
            new byte[4],
            PackUInt32(now), PackUInt32(now),
            PackUInt32(sampleRate), PackUInt32(durAudioSamples),
            new byte[4]
        ));

        byte[] hdlr = MakeAtom("hdlr", Concat(
            new byte[8], "soun"u8.ToArray(), new byte[12],
            System.Text.Encoding.ASCII.GetBytes("Apple Sound Media Handler\0")
        ));

        byte[] srF64 = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(srF64, sampleRate);

        byte[] lpcmBody = Concat(
            new byte[6], PackUInt16(1), PackUInt16(2), PackUInt16(0), new byte[4],
            PackUInt16(3), PackUInt16(16), [0xFF, 0xFF, 0xFF, 0xFE], PackUInt16(0), PackUInt16(0),
            PackUInt32(65536), PackUInt32(72), srF64, PackUInt32(channels),
            PackUInt32(0x7F000000), PackUInt32(24), PackUInt32(0x6), PackUInt32(channels * 3), PackUInt32(1)
        );

        byte[] lpcm = MakeAtom("lpcm", lpcmBody);
        byte[] stsd = MakeAtom("stsd", Concat(new byte[4], PackUInt32(1), lpcm));
        byte[] stts = MakeAtom("stts", Concat(new byte[4], PackUInt32(1), PackUInt32(chunkCount), PackUInt32(1024)));
        byte[] stsc = MakeAtom("stsc", Concat(new byte[4], PackUInt32(1), PackUInt32(1), PackUInt32(1), PackUInt32(1)));
        byte[] stsz = MakeAtom("stsz", Concat(new byte[4], PackUInt32(ChunkAudio), PackUInt32(chunkCount)));

        byte[] coData = new byte[8 + audioOffsets.Count * 8];
        BinaryPrimitives.WriteUInt32BigEndian(coData.AsSpan(4, 4), (uint)audioOffsets.Count);
        for (int i = 0; i < audioOffsets.Count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(coData.AsSpan(8 + i * 8, 8), (ulong)audioOffsets[i]);
        }
        byte[] co = MakeAtom("co64", coData);

        byte[] dref = MakeAtom("dref", Concat(new byte[4], PackUInt32(1), MakeAtom("url ", [0x00, 0x00, 0x00, 0x01])));
        byte[] smhd = MakeAtom("smhd", new byte[8]);
        byte[] stbl = MakeAtom("stbl", Concat(stsd, stts, stsc, stsz, co));
        byte[] minf = MakeAtom("minf", Concat(smhd, MakeAtom("dinf", dref), stbl));
        byte[] mdia = MakeAtom("mdia", Concat(mdhd, hdlr, minf));

        return MakeAtom("trak", Concat(tkhd, mdia));
    }

    private static byte[] BuildChunkOffsets(List<long> offsets)
    {
        bool use64 = offsets.Any(o => o > 0xFFFFFFFF);
        if (use64)
        {
            byte[] data = new byte[8 + offsets.Count * 8];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), (uint)offsets.Count);
            for (int i = 0; i < offsets.Count; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8 + i * 8, 8), (ulong)offsets[i]);
            }
            return MakeAtom("co64", data);
        }
        else
        {
            byte[] data = new byte[8 + offsets.Count * 4];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), (uint)offsets.Count);
            for (int i = 0; i < offsets.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8 + i * 4, 4), (uint)offsets[i]);
            }
            return MakeAtom("stco", data);
        }
    }
    #endregion
}
