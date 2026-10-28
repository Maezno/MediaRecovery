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

public abstract class VideoFileItemBase
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
    public long SizeBytes { get; set; }
    public double Duration { get; set; }

    public virtual string SizeFormatted => $"{SizeBytes / (1024.0 * 1024.0):F1} MB";

    public virtual string DurationFormatted
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
}

public class VideoMetadataItem : VideoFileItemBase
{
    public string DeviceModel { get; set; } = "Desconocido";
    public string Orientation { get; set; } = "Horizontal";
    public string Resolution { get; set; } = string.Empty;
    public int Rotation { get; set; }
    public DateTime DateModified { get; set; }
}

public class DeviceSummaryItem
{
    public required string DeviceModel { get; init; }
    public int VerticalCount { get; set; }
    public int HorizontalCount { get; set; }
    public int TotalCount => VerticalCount + HorizontalCount;
    public string PctVertical => TotalCount > 0 ? $"{Math.Round((double)VerticalCount * 100.0 / TotalCount):0}%" : "0%";
}

public class BrokenVideoItem : VideoFileItemBase
{
    public string ErrorReason { get; set; } = string.Empty;
    public bool IsProResCandidate { get; set; }
}

public class HeavyVideoItem : VideoFileItemBase
{
    public string Codec { get; set; } = string.Empty;
    public double BitrateMbps { get; set; }
    public string Resolution { get; set; } = string.Empty;

    public override string SizeFormatted => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    public string BitrateFormatted => $"{BitrateMbps:F1} Mbps";
}

public class DuplicateVideoItem : VideoFileItemBase
{
    public DateTime DateModified { get; set; }
    public int GroupId { get; set; }
    public bool IsPrimary { get; set; }
}
