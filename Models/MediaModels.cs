namespace MediaRecovery.Models;

public record FrameInfo(long Offset, int Length);

public record AudioInfo(long Offset, int ChunkCount, double DurationSeconds, bool IsComplete);

public class ScanProgressReport
{
    public double Percentage { get; set; }
    public double SpeedMBs { get; set; }
    public double EtaMinutes { get; set; }
    public int FramesFound { get; set; }
    public long BytesRead { get; set; }
    public string StatusText { get; set; } = string.Empty;
}
