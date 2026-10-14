namespace MediaRecovery.Models;

public record FrameInfo(long Offset, int Length);

public record AudioInfo(long Offset, int ChunkCount, double DurationSeconds, bool IsComplete);

public record JumpInfo(int FrameIndex, double TimestampSeconds, int MissingFrames);

public enum MediaFormatType
{
    VideoMp4,
    AudioMp3
}

public class DownloadOptions
{
    public string Url { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public MediaFormatType Format { get; set; } = MediaFormatType.VideoMp4;
    public string Mp3Quality { get; set; } = "320k";
    public bool FullVideo { get; set; } = true;
    public TimeSpan StartTime { get; set; } = TimeSpan.Zero;
    public TimeSpan EndTime { get; set; } = TimeSpan.FromMinutes(1);
    public bool OpenFolderOnComplete { get; set; } = true;
}

public class ScanProgressReport
{
    public double Percentage { get; set; }
    public double SpeedMBs { get; set; }
    public double EtaMinutes { get; set; }
    public int FramesFound { get; set; }
    public long BytesRead { get; set; }
    public string StatusText { get; set; } = string.Empty;
}
