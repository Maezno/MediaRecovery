using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaRecovery.Models;

namespace MediaRecovery.Services;

public partial class VideoLibraryService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".3gp", ".mts", ".m4v", ".wmv", ".m2ts"
    };

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "__MACOSX", ".git", ".vs"
    };

    private static readonly Dictionary<string, string> MajorBrandMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "msnv", "Sony" },
        { "xavc", "Sony" },
        { "caep", "Canon" },
        { "cdes", "Canon" },
        { "kddi", "KDDI (AU)" },
        { "m4v",  "Apple (iTunes)" },
        { "f4v",  "Adobe" },
        { "niko", "Nikon" }
    };

    private static readonly HashSet<string> UncompressedOrMasterCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "prores", "apch", "apcn", "apcs", "apco", "ap4h",
        "dnxhd", "dnxhr",
        "rawvideo", "v210", "v308", "v408", "v410", "yuv4", "b64a", "r210",
        "mjpeg", "cineform", "cfhd", "ffv1", "qtrle"
    };

    private static readonly byte[] MagicIcpf = [0x69, 0x63, 0x70, 0x66]; // 'icpf'

    [GeneratedRegex(@"adobe|premiere|after effects|media encoder|davinci|resolve|final cut|handbrake|ffmpeg|lavf|lavc|vegas|capcut|inshot|kdenlive|shotcut|camtasia|filmora|kinemaster|clipchamp|movavi|openshot|wondershare|canva|\bobs\b", RegexOptions.IgnoreCase)]
    private static partial Regex EditorRegex();

    [GeneratedRegex(@"[\s_/\-]*\(?v?\d.*$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"vivoMediaExtInfo[^{]*(\{.*?\})")]
    private static partial Regex VivoExtInfoRegex();

    [GeneratedRegex(@"""com\.android\.camera\.takenmodel""\s*:\s*""([^""]+)""")]
    private static partial Regex VivoModelRegex();

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. RECOLECCIÓN DE ARCHIVOS
    // ─────────────────────────────────────────────────────────────────────────────

    public List<string> FindVideoFiles(string folderPath, bool recursive = true)
    {
        var result = new List<string>();
        if (!Directory.Exists(folderPath)) return result;

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            var dirInfo = new DirectoryInfo(folderPath);
            var files = dirInfo.EnumerateFiles("*.*", searchOption);

            foreach (var fi in files)
            {
                if (fi.Name.StartsWith("._")) continue;
                if (VideoExtensions.Contains(fi.Extension))
                {
                    // Evitar carpetas del sistema ignoradas
                    string fullPath = fi.FullName;
                    if (IgnoredDirs.Any(d => fullPath.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar) || fullPath.Contains("/" + d + "/")))
                        continue;

                    result.Add(fullPath);
                }
            }
        }
        catch
        {
            // Fallback recursivo manual por si falla algún permiso en subcarpeta
            CollectFilesManually(folderPath, result, recursive);
        }

        return result;
    }

    private void CollectFilesManually(string folder, List<string> result, bool recursive)
    {
        try
        {
            foreach (var f in Directory.GetFiles(folder))
            {
                string ext = Path.GetExtension(f);
                if (VideoExtensions.Contains(ext) && !Path.GetFileName(f).StartsWith("._"))
                {
                    result.Add(f);
                }
            }

            if (recursive)
            {
                foreach (var d in Directory.GetDirectories(folder))
                {
                    string dirName = Path.GetFileName(d);
                    if (!IgnoredDirs.Contains(dirName))
                    {
                        CollectFilesManually(d, result, true);
                    }
                }
            }
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. BUSCADOR & ANALIZADOR DE METADATOS (Port de analizador_prensa-v2.py con Caché en Disco)
    // ─────────────────────────────────────────────────────────────────────────────

    private static string GetCacheFilePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRecoveryStudio");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "metadata_cache.json");
    }

    private static ConcurrentDictionary<string, CachedVideoMetadata> LoadCache()
    {
        var cache = new ConcurrentDictionary<string, CachedVideoMetadata>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = GetCacheFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var dict = JsonSerializer.Deserialize<Dictionary<string, CachedVideoMetadata>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict) cache[kvp.Key] = kvp.Value;
                }
            }
        }
        catch { }
        return cache;
    }

    private static void SaveCache(ConcurrentDictionary<string, CachedVideoMetadata> cache)
    {
        try
        {
            string path = GetCacheFilePath();
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(cache);
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, true);
        }
        catch { }
    }

    public async Task<List<VideoMetadataItem>> ScanMetadataAsync(
        List<string> videoPaths,
        int? filterMonth = null,
        int? filterYear = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new ConcurrentBag<VideoMetadataItem>();
        int total = videoPaths.Count;
        int processed = 0;
        int newItemsCount = 0;

        var cache = LoadCache();

        int maxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(videoPaths, parallelOptions, async (filePath, ct) =>
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists) return;

                if (filterMonth.HasValue && fi.LastWriteTime.Month != filterMonth.Value) return;
                if (filterYear.HasValue && fi.LastWriteTime.Year != filterYear.Value) return;

                // 1. Revisar caché persistente
                if (cache.TryGetValue(filePath, out var cached) &&
                    cached.SizeBytes == fi.Length &&
                    Math.Abs((cached.DateModified - fi.LastWriteTime).TotalSeconds) < 2)
                {
                    items.Add(new VideoMetadataItem
                    {
                        FilePath = filePath,
                        DeviceModel = cached.DeviceModel,
                        Orientation = cached.Orientation,
                        Resolution = cached.Resolution,
                        Rotation = cached.Rotation,
                        Duration = cached.Duration,
                        SizeBytes = cached.SizeBytes,
                        DateModified = cached.DateModified
                    });
                    return;
                }

                // 2. Si no está en caché, analizar con ffprobe
                var meta = await ProbeSingleVideoMetadataAsync(filePath, fi, ct);
                if (meta != null)
                {
                    items.Add(meta);
                    cache[filePath] = new CachedVideoMetadata
                    {
                        DeviceModel = meta.DeviceModel,
                        Orientation = meta.Orientation,
                        Resolution = meta.Resolution,
                        Rotation = meta.Rotation,
                        Duration = meta.Duration,
                        SizeBytes = meta.SizeBytes,
                        DateModified = meta.DateModified
                    };
                    Interlocked.Increment(ref newItemsCount);
                }
            }
            catch { }
            finally
            {
                int current = Interlocked.Increment(ref processed);
                onProgress?.Invoke(current, total, Path.GetFileName(filePath));
            }
        });

        if (newItemsCount > 0)
        {
            _ = Task.Run(() => SaveCache(cache));
        }

        return items.OrderByDescending(x => x.DateModified).ToList();
    }

    private async Task<VideoMetadataItem?> ProbeSingleVideoMetadataAsync(string filePath, FileInfo fi, CancellationToken ct)
    {
        string args = $"-v error -print_format json -select_streams v -show_streams -show_format \"{filePath}\"";
        var probe = await RunProcessCaptureAsync("ffprobe", args, ct);
        if (string.IsNullOrWhiteSpace(probe.StdOut)) return null;

        try
        {
            using var doc = JsonDocument.Parse(probe.StdOut);
            var root = doc.RootElement;

            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                return null;

            JsonElement? videoStream = null;
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var cType) && cType.GetString() == "video")
                {
                    // Ignorar carátulas / attached_pic
                    if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1)
                        continue;

                    videoStream = s;
                    break;
                }
            }

            if (videoStream == null) return null;
            var vs = videoStream.Value;

            int width = vs.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            int height = vs.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            if (width <= 0 || height <= 0) return null;

            // Extraer formato y tags
            JsonElement formatObj = default;
            root.TryGetProperty("format", out formatObj);

            var streamTags = ExtractTags(vs);
            var formatTags = formatObj.ValueKind == JsonValueKind.Object ? ExtractTags(formatObj) : [];

            int rotation = ExtractRotation(vs, formatTags);
            if (rotation is 90 or 270)
            {
                (width, height) = (height, width);
            }

            string orientation = height > width ? "Vertical" : "Horizontal";
            string model = DetectDeviceModel(formatTags, streamTags, filePath);

            double duration = 0;
            if (formatObj.ValueKind == JsonValueKind.Object && formatObj.TryGetProperty("duration", out var durProp))
            {
                if (double.TryParse(durProp.GetString(), System.Globalization.CultureInfo.InvariantCulture, out double dVal))
                    duration = dVal;
            }

            return new VideoMetadataItem
            {
                FilePath = filePath,
                DeviceModel = model,
                Orientation = orientation,
                Resolution = $"{width}×{height}",
                Rotation = rotation,
                Duration = duration,
                SizeBytes = fi.Length,
                DateModified = fi.LastWriteTime
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ExtractTags(JsonElement element)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in tags.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }
        return dict;
    }

    private static int ExtractRotation(JsonElement stream, Dictionary<string, string> formatTags)
    {
        // 1. side_data_list -> rotation
        if (stream.TryGetProperty("side_data_list", out var sideDataList) && sideDataList.ValueKind == JsonValueKind.Array)
        {
            foreach (var sd in sideDataList.EnumerateArray())
            {
                if (sd.TryGetProperty("rotation", out var rotProp))
                {
                    if (rotProp.TryGetInt32(out int rot)) return (rot % 360 + 360) % 360;
                    if (double.TryParse(rotProp.GetString(), out double rotD)) return ((int)Math.Round(rotD) % 360 + 360) % 360;
                }
            }
        }

        // 2. tags -> rotate
        if (stream.TryGetProperty("tags", out var sTags) && sTags.TryGetProperty("rotate", out var rotTag))
        {
            if (double.TryParse(rotTag.GetString(), out double r)) return ((int)Math.Round(r) % 360 + 360) % 360;
        }

        // 3. format tags -> rotate
        if (formatTags.TryGetValue("rotate", out var fRot))
        {
            if (double.TryParse(fRot, out double r)) return ((int)Math.Round(r) % 360 + 360) % 360;
        }

        return 0;
    }

    private static string DetectDeviceModel(Dictionary<string, string> formatTags, Dictionary<string, string> streamTags, string filePath)
    {
        var allTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in formatTags) allTags[kvp.Key] = kvp.Value;
        foreach (var kvp in streamTags) allTags[kvp.Key] = kvp.Value;

        string Get(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (allTags.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    string low = v.Trim().ToLowerInvariant();
                    if (low != "unknown" && low != "n/a" && low != "none" && low != "null")
                        return v.Trim();
                }
            }
            return string.Empty;
        }

        // 1. Apple
        string appleMake = Get("com.apple.quicktime.make");
        string appleModel = Get("com.apple.quicktime.model");
        string appleSoft = Get("com.apple.quicktime.software");
        if (!string.IsNullOrEmpty(appleMake) || !string.IsNullOrEmpty(appleModel))
        {
            if (!string.IsNullOrEmpty(appleMake) && (appleMake.Contains("iphone", StringComparison.OrdinalIgnoreCase) ||
                                                    appleMake.Contains("ipad", StringComparison.OrdinalIgnoreCase)))
                return CleanModelString(appleMake);

            if (!string.IsNullOrEmpty(appleMake) && !string.IsNullOrEmpty(appleModel))
            {
                string r = appleModel.Contains(appleMake, StringComparison.OrdinalIgnoreCase) ? appleModel : $"{appleMake} {appleModel}";
                return CleanModelString(r);
            }
            if (!string.IsNullOrEmpty(appleModel)) return CleanModelString(appleModel);
            return CleanModelString(appleMake + (!string.IsNullOrEmpty(appleSoft) ? $" (iOS {appleSoft})" : ""));
        }

        // 2. Android estándar
        string aModel = Get("com.android.model", "model");
        string aMake = Get("com.android.manufacturer", "com.android.make", "manufacturer", "make");
        if (!string.IsNullOrEmpty(aModel))
        {
            string r = string.IsNullOrEmpty(aMake) || aModel.Contains(aMake, StringComparison.OrdinalIgnoreCase) ? aModel : $"{aMake} {aModel}";
            return CleanModelString(r);
        }

        // 3. major_brand y compatible_brands
        string majorBrand = Get("major_brand").ToLowerInvariant();
        if (MajorBrandMap.TryGetValue(majorBrand, out var brandName))
            return brandName;

        string compBrands = Get("compatible_brands").ToLowerInvariant();
        foreach (var kvp in MajorBrandMap)
        {
            if (compBrands.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        // 4. Software / Editor
        string software = Get("software", "encoder");
        if (!string.IsNullOrEmpty(software))
        {
            string clean = CleanModelString(software);
            if (EditorRegex().IsMatch(software))
            {
                string baseEd = VersionRegex().Replace(clean, "").Trim(' ', '-', '_', '/');
                return $"[Editado] {(string.IsNullOrWhiteSpace(baseEd) ? clean : baseEd)}";
            }
            return clean;
        }

        // 5. Samsung por utc_offset
        if (!string.IsNullOrEmpty(Get("com.samsung.android.utc_offset")))
        {
            string ver = Get("com.android.version");
            return !string.IsNullOrEmpty(ver) ? $"Samsung (Android {ver})" : "Samsung";
        }

        // 6. Vivo (metadatos propietarios)
        string vivoModel = TryExtractVivoModel(filePath);
        if (!string.IsNullOrEmpty(vivoModel))
            return CleanModelString(vivoModel);

        // 7. DJI
        string fileName = Path.GetFileName(filePath).ToLowerInvariant();
        if (fileName.StartsWith("dji_") && fileName.Contains("_video"))
            return "DJI (Osmo / Pocket / Mimo)";

        // 8. WhatsApp
        if (fileName.Contains("wa00") || filePath.Contains("whatsapp", StringComparison.OrdinalIgnoreCase))
            return "WhatsApp (Comprimido)";

        // 9. Android genérico
        string androidVer = Get("com.android.version");
        if (!string.IsNullOrEmpty(androidVer))
            return $"Android genérico (Android {androidVer})";

        // 10. Contenedor mp42
        if (majorBrand == "mp42")
            return "Video comprimido / Sin metadatos";

        return "Desconocido";
    }

    private static string CleanModelString(string text)
    {
        text = text.Replace("Google Android", "").Replace("google/", "").Replace("samsung/", "Samsung ").Replace("SAMSUNG", "Samsung");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 70 ? text[..70] : text;
    }

    private static string TryExtractVivoModel(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Length < 512) return string.Empty;

            long readBytes = Math.Min(fi.Length, 131072);
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(fi.Length - readBytes, SeekOrigin.Begin);
            byte[] chunk = new byte[readBytes];
            int read = fs.Read(chunk, 0, (int)readBytes);

            string text = Encoding.UTF8.GetString(chunk, 0, read);
            var m = VivoExtInfoRegex().Match(text);
            if (m.Success)
            {
                string payload = m.Groups[1].Value;
                var modelMatch = VivoModelRegex().Match(payload);
                if (modelMatch.Success) return modelMatch.Groups[1].Value.Trim();
            }
        }
        catch { }
        return string.Empty;
    }

    public List<DeviceSummaryItem> BuildDeviceSummaries(IEnumerable<VideoMetadataItem> items)
    {
        return items
            .GroupBy(x => x.DeviceModel)
            .Select(g => new DeviceSummaryItem
            {
                DeviceModel = g.Key,
                VerticalCount = g.Count(x => x.Orientation.Equals("Vertical", StringComparison.OrdinalIgnoreCase)),
                HorizontalCount = g.Count(x => x.Orientation.Equals("Horizontal", StringComparison.OrdinalIgnoreCase))
            })
            .OrderByDescending(x => x.TotalCount)
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3. BUSCADOR DE VIDEOS ROTOS / CORRUPTOS
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task<List<BrokenVideoItem>> ScanBrokenVideosAsync(
        List<string> videoPaths,
        bool deepDecodeCheck = false,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var broken = new ConcurrentBag<BrokenVideoItem>();
        int total = videoPaths.Count;
        int processed = 0;

        int maxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(videoPaths, parallelOptions, async (filePath, ct) =>
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists) return;

                if (fi.Length == 0)
                {
                    broken.Add(new BrokenVideoItem
                    {
                        FilePath = filePath,
                        SizeBytes = 0,
                        ErrorReason = "Archivo vacío (0 bytes)",
                        IsProResCandidate = false
                    });
                    return;
                }

                // 1. Probar lectura de contenedor con ffprobe
                var probe = await RunProcessCaptureAsync("ffprobe",
                    $"-v error -show_entries format=format_name,duration:stream=codec_type,codec_name -of json \"{filePath}\"",
                    ct);

                bool failed = probe.ExitCode != 0 || !string.IsNullOrWhiteSpace(probe.StdErr);

                if (failed)
                {
                    bool isProRes = await ScanForProResMagicAsync(filePath, 32 * 1024 * 1024, ct);
                    string reason = "Error de contenedor / cabecera inválida";

                    if (probe.StdErr.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = isProRes 
                            ? "Falta átomo 'moov' (tramas ProRes detectadas - recuperable)" 
                            : "Contenedor roto (falta átomo 'moov')";
                    }
                    else if (!string.IsNullOrWhiteSpace(probe.StdErr))
                    {
                        var firstLine = probe.StdErr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(firstLine))
                            reason = firstLine.Length > 90 ? firstLine[..90] + "..." : firstLine;
                    }

                    broken.Add(new BrokenVideoItem
                    {
                        FilePath = filePath,
                        SizeBytes = fi.Length,
                        ErrorReason = reason,
                        IsProResCandidate = isProRes
                    });
                    return;
                }

                // 2. Decodificación rápida si se solicita deep check
                if (deepDecodeCheck)
                {
                    var decode = await RunProcessCaptureAsync("ffmpeg",
                        $"-v error -xerror -i \"{filePath}\" -t 2 -f null -", ct);

                    if (decode.ExitCode != 0 || !string.IsNullOrWhiteSpace(decode.StdErr))
                    {
                        string reason = "Falla al decodificar paquetes iniciales";
                        var firstLine = decode.StdErr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(firstLine))
                            reason = firstLine.Length > 90 ? firstLine[..90] + "..." : firstLine;

                        broken.Add(new BrokenVideoItem
                        {
                            FilePath = filePath,
                            SizeBytes = fi.Length,
                            ErrorReason = reason,
                            IsProResCandidate = false
                        });
                    }
                }
            }
            catch { }
            finally
            {
                int current = Interlocked.Increment(ref processed);
                onProgress?.Invoke(current, total, Path.GetFileName(filePath));
            }
        });

        return broken.OrderByDescending(x => x.SizeBytes).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 4. BUSCADOR DE VIDEOS SIN COMPRESIÓN / PESADOS
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task<List<HeavyVideoItem>> ScanHeavyVideosAsync(
        List<string> videoPaths,
        double minBitrateMbps = 40.0,
        double minSizeGB = 1.0,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var heavy = new ConcurrentBag<HeavyVideoItem>();
        int total = videoPaths.Count;
        int processed = 0;

        int maxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(videoPaths, parallelOptions, async (filePath, ct) =>
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length == 0) return;

                double sizeGb = fi.Length / (1024.0 * 1024.0 * 1024.0);

                var probe = await RunProcessCaptureAsync("ffprobe",
                    $"-v error -print_format json -select_streams v:0 -show_entries stream=codec_name,width,height,bit_rate:format=duration,bit_rate \"{filePath}\"",
                    ct);

                if (string.IsNullOrWhiteSpace(probe.StdOut)) return;

                using var doc = JsonDocument.Parse(probe.StdOut);
                var root = doc.RootElement;

                string codec = "desconocido";
                int width = 0, height = 0;
                double bitrateMbps = 0;
                double duration = 0;

                if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
                {
                    var s = streams[0];
                    if (s.TryGetProperty("codec_name", out var cProp)) codec = cProp.GetString() ?? "";
                    if (s.TryGetProperty("width", out var wProp)) width = wProp.GetInt32();
                    if (s.TryGetProperty("height", out var hProp)) height = hProp.GetInt32();
                    if (s.TryGetProperty("bit_rate", out var brProp) && double.TryParse(brProp.GetString(), out double br))
                    {
                        bitrateMbps = br / 1_000_000.0;
                    }
                }

                if (root.TryGetProperty("format", out var fmt))
                {
                    if (fmt.TryGetProperty("duration", out var durProp) && double.TryParse(durProp.GetString(), System.Globalization.CultureInfo.InvariantCulture, out double d))
                    {
                        duration = d;
                    }
                    if (bitrateMbps <= 0 && fmt.TryGetProperty("bit_rate", out var fbrProp) && double.TryParse(fbrProp.GetString(), out double fbr))
                    {
                        bitrateMbps = fbr / 1_000_000.0;
                    }
                }

                if (bitrateMbps <= 0 && duration > 0)
                {
                    bitrateMbps = (fi.Length * 8.0) / (duration * 1_000_000.0);
                }

                bool isMasterCodec = UncompressedOrMasterCodecs.Contains(codec);
                bool isHighBitrate = bitrateMbps >= minBitrateMbps;
                bool isLargeFile = sizeGb >= minSizeGB;

                if (isMasterCodec || isHighBitrate || (isLargeFile && duration < 600))
                {
                    heavy.Add(new HeavyVideoItem
                    {
                        FilePath = filePath,
                        Codec = codec.ToUpperInvariant(),
                        BitrateMbps = bitrateMbps,
                        Resolution = $"{width}×{height}",
                        Duration = duration,
                        SizeBytes = fi.Length
                    });
                }
            }
            catch { }
            finally
            {
                int current = Interlocked.Increment(ref processed);
                onProgress?.Invoke(current, total, Path.GetFileName(filePath));
            }
        });

        return heavy.OrderByDescending(x => x.SizeBytes).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 5. BUSCADOR DE DUPLICADOS (1 Carpeta o Comparación de 2 Carpetas)
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task<List<DuplicateVideoItem>> ScanDuplicatesAsync(
        List<string> folderAFiles,
        List<string>? folderBFiles = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var duplicates = new List<DuplicateVideoItem>();
        bool twoFoldersMode = folderBFiles != null && folderBFiles.Count > 0;

        var allCandidates = new List<(string Path, long Size, bool IsFolderB)>();

        foreach (var f in folderAFiles)
        {
            try
            {
                var fi = new FileInfo(f);
                if (fi.Exists && fi.Length > 0) allCandidates.Add((f, fi.Length, false));
            }
            catch { }
        }

        if (twoFoldersMode)
        {
            foreach (var f in folderBFiles!)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > 0) allCandidates.Add((f, fi.Length, true));
                }
                catch { }
            }
        }

        // 1. Agrupar por tamaño de archivo exacto
        var sizeGroups = allCandidates
            .GroupBy(x => x.Size)
            .Where(g => g.Count() > 1)
            .ToList();

        int totalToCheck = sizeGroups.Sum(g => g.Count());
        int processed = 0;
        int currentGroupId = 1;

        foreach (var group in sizeGroups)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // En modo dos carpetas, el grupo debe contener al menos uno de A y uno de B
            if (twoFoldersMode)
            {
                bool hasA = group.Any(x => !x.IsFolderB);
                bool hasB = group.Any(x => x.IsFolderB);
                if (!hasA || !hasB)
                {
                    processed += group.Count();
                    continue;
                }
            }

            // 2. Hash rápido (primeros 64KB + 64KB centrales + últimos 64KB)
            var hashGroups = new Dictionary<string, List<(string Path, long Size, bool IsFolderB)>>();

            foreach (var item in group)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string hash = await ComputeFastSignatureAsync(item.Path, item.Size, cancellationToken);
                if (!hashGroups.ContainsKey(hash)) hashGroups[hash] = [];
                hashGroups[hash].Add(item);

                processed++;
                onProgress?.Invoke(processed, totalToCheck, Path.GetFileName(item.Path));
            }

            foreach (var hg in hashGroups.Values.Where(h => h.Count > 1))
            {
                if (twoFoldersMode)
                {
                    if (!hg.Any(x => !x.IsFolderB) || !hg.Any(x => x.IsFolderB))
                        continue;
                }

                bool isFirst = true;
                foreach (var match in hg)
                {
                    var fi = new FileInfo(match.Path);
                    duplicates.Add(new DuplicateVideoItem
                    {
                        FilePath = match.Path,
                        SizeBytes = match.Size,
                        Duration = 0,
                        DateModified = fi.LastWriteTime,
                        GroupId = currentGroupId,
                        IsPrimary = isFirst
                    });
                    isFirst = false;
                }
                currentGroupId++;
            }
        }

        return duplicates;
    }

    private static async Task<string> ComputeFastSignatureAsync(string filePath, long fileSize, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            using var sha = SHA256.Create();

            int sampleSize = (int)Math.Min(64 * 1024, fileSize);
            byte[] buffer = new byte[sampleSize * 3];
            int pos = 0;

            // 1. Inicio
            int read = await fs.ReadAsync(buffer.AsMemory(pos, sampleSize), ct);
            pos += read;

            // 2. Centro
            if (fileSize > sampleSize * 3)
            {
                fs.Seek(fileSize / 2, SeekOrigin.Begin);
                read = await fs.ReadAsync(buffer.AsMemory(pos, sampleSize), ct);
                pos += read;
            }

            // 3. Final
            if (fileSize > sampleSize * 2)
            {
                fs.Seek(Math.Max(0, fileSize - sampleSize), SeekOrigin.Begin);
                read = await fs.ReadAsync(buffer.AsMemory(pos, sampleSize), ct);
                pos += read;
            }

            byte[] hash = sha.ComputeHash(buffer, 0, pos);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return Guid.NewGuid().ToString();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // UTILIDADES COMUNES
    // ─────────────────────────────────────────────────────────────────────────────

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
        catch { }
        return false;
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

    // ─────────────────────────────────────────────────────────────────────────────
    // 6. EXPORTACIÓN A CSV (Compatible con Excel regional)
    // ─────────────────────────────────────────────────────────────────────────────

    public static void ExportMetadataToCsv(string targetCsvPath, IEnumerable<VideoMetadataItem> items, IEnumerable<DeviceSummaryItem>? summaries = null, bool exportDetails = true)
    {
        using var sw = new StreamWriter(targetCsvPath, false, Encoding.UTF8);

        if (exportDetails)
        {
            sw.WriteLine("Archivo;Carpeta;Dispositivo;Orientación;Resolución;Rotación;Duración (s);Tamaño (MB);Fecha");
            foreach (var it in items)
            {
                sw.WriteLine($"{EscapeCsv(it.FileName)};{EscapeCsv(it.Directory)};{EscapeCsv(it.DeviceModel)};{it.Orientation};{it.Resolution};{it.Rotation};{it.Duration:F1};{it.SizeBytes / (1024.0 * 1024.0):F1};{it.DateModified:yyyy-MM-dd HH:mm:ss}");
            }
        }
        else if (summaries != null)
        {
            sw.WriteLine("Dispositivo;Verticales;Horizontales;Total;% Vertical");
            foreach (var s in summaries)
            {
                sw.WriteLine($"{EscapeCsv(s.DeviceModel)};{s.VerticalCount};{s.HorizontalCount};{s.TotalCount};{s.PctVertical}");
            }
        }
    }

    private static string EscapeCsv(string val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        if (val.Contains(';') || val.Contains('"') || val.Contains('\n') || val.Contains('\r'))
        {
            return $"\"{val.Replace("\"", "\"\"")}\"";
        }
        return val;
    }
}
