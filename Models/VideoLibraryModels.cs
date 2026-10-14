using System.IO;

namespace MediaRecovery.Models;

public class CachedVideoMetadata
{
    public string DeviceModel { get; set; } = "Desconocido";
    public string Orientation { get; set; } = "Horizontal";
    public string Resolution { get; set; } = string.Empty;
    public int Rotation { get; set; }
    public double Duration { get; set; }
    public long SizeBytes { get; set; }
    public DateTime DateModified { get; set; }
}

public class VideoMetadataItem
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
    public string DeviceModel { get; set; } = "Desconocido";
    public string Orientation { get; set; } = "Horizontal";
    public string Resolution { get; set; } = string.Empty;
    public int Rotation { get; set; }
    public double Duration { get; set; }
    public long SizeBytes { get; set; }
    public DateTime DateModified { get; set; }

    public string DurationFormatted
    {
        get
        {
            if (Duration <= 0) return "--";
            int totalSec = (int)Math.Round(Duration);
            int m = totalSec / 60;
            int s = totalSec % 60;
            return m > 0 ? $"{m}m {s:02d}s" : $"{s}s";
        }
    }

    public string SizeFormatted => $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
}

public class DeviceSummaryItem
{
    public required string DeviceModel { get; init; }
    public int VerticalCount { get; set; }
    public int HorizontalCount { get; set; }
    public int TotalCount => VerticalCount + HorizontalCount;
    public string PctVertical => TotalCount > 0 ? $"{Math.Round((double)VerticalCount * 100.0 / TotalCount):0}%" : "0%";
}

public class BrokenVideoItem
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
    public long SizeBytes { get; set; }
    public string ErrorReason { get; set; } = string.Empty;
    public bool IsProResCandidate { get; set; }

    public string SizeFormatted => $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
}

public class HeavyVideoItem
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
    public string Codec { get; set; } = string.Empty;
    public double BitrateMbps { get; set; }
    public string Resolution { get; set; } = string.Empty;
    public double Duration { get; set; }
    public long SizeBytes { get; set; }

    public string DurationFormatted
    {
        get
        {
            if (Duration <= 0) return "--";
            int totalSec = (int)Math.Round(Duration);
            int m = totalSec / 60;
            int s = totalSec % 60;
            return m > 0 ? $"{m}m {s:02d}s" : $"{s}s";
        }
    }

    public string SizeFormatted => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    public string BitrateFormatted => $"{BitrateMbps:F1} Mbps";
}

public class DuplicateVideoItem
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
    public long SizeBytes { get; set; }
    public double Duration { get; set; }
    public DateTime DateModified { get; set; }
    public int GroupId { get; set; }
    public bool IsPrimary { get; set; }

    public string SizeFormatted => $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
    public string DurationFormatted => Duration > 0 ? $"{(int)Duration}s" : "--";
}
