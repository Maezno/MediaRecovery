using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MediaRecovery.Models;
using MediaRecovery.Services;

namespace MediaRecovery.ViewModels;

public class RecoveryViewModel : ViewModelBase
{
    private readonly ProResScannerService _scanner = new();
    private CancellationTokenSource? _cts;

    private string _sourceFilePath = string.Empty;
    private int _frameMin = ProResScannerService.DefaultFrameMin;
    private int _frameMax = ProResScannerService.DefaultFrameMax;
    private int _bufferSizeMB = 64;

    private bool _isScanning;
    private double _scanProgress;
    private string _scanStatus = "Seleccione un archivo binario o imagen raw para escanear.";
    private string _metricsText = "0 frames | 0 MB/s | ETA: --";
    private string _logOutput = string.Empty;

    private readonly MovBuilderService _movBuilder = new();
    private List<FrameInfo> _allScannedFrames = [];
    private AudioInfo? _detectedAudio;

    private string _outputMovPath = string.Empty;
    private bool _includeAudio = true;
    private bool _isBuildingMov;

    public ObservableCollection<FrameInfo> DiscoveredFrames { get; } = [];

    public RecoveryViewModel()
    {
        BrowseFileCommand = new RelayCommand(_ => BrowseFile());
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
        StartScanCommand = new RelayCommand(async _ => await StartScanAsync(), _ => !IsScanning && !IsBuildingMov && File.Exists(SourceFilePath));
        CancelScanCommand = new RelayCommand(_ => CancelScan(), _ => IsScanning || IsBuildingMov);
        BuildMovCommand = new RelayCommand(async _ => await BuildMovAsync(), _ => !IsScanning && !IsBuildingMov && _allScannedFrames.Count > 0 && !string.IsNullOrWhiteSpace(OutputMovPath));
    }

    public string SourceFilePath { get => _sourceFilePath; set { SetField(ref _sourceFilePath, value); CommandManager.InvalidateRequerySuggested(); } }
    public string OutputMovPath { get => _outputMovPath; set { SetField(ref _outputMovPath, value); CommandManager.InvalidateRequerySuggested(); } }

    public void SetSourceFile(string filePath)
    {
        SourceFilePath = filePath;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(filePath);
            OutputMovPath = Path.Combine(dir, $"{name}_recovered.mov");
        }
    }
    public bool IncludeAudio { get => _includeAudio; set => SetField(ref _includeAudio, value); }
    public bool IsBuildingMov { get => _isBuildingMov; set { SetField(ref _isBuildingMov, value); CommandManager.InvalidateRequerySuggested(); } }
    public int FrameMin { get => _frameMin; set => SetField(ref _frameMin, value); }
    public int FrameMax { get => _frameMax; set => SetField(ref _frameMax, value); }
    public int BufferSizeMB { get => _bufferSizeMB; set => SetField(ref _bufferSizeMB, value); }

    public bool IsScanning { get => _isScanning; set { SetField(ref _isScanning, value); CommandManager.InvalidateRequerySuggested(); } }
    public double ScanProgress { get => _scanProgress; set => SetField(ref _scanProgress, value); }
    public string ScanStatus { get => _scanStatus; set => SetField(ref _scanStatus, value); }
    public string MetricsText { get => _metricsText; set => SetField(ref _metricsText, value); }
    public string LogOutput { get => _logOutput; set => SetField(ref _logOutput, value); }

    public ICommand BrowseFileCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand StartScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand BuildMovCommand { get; }

    private void BrowseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Seleccionar Archivo Dañado o Imagen de Disco Raw",
            Filter = "Archivos de Video/Raw (*.mov;*.raw;*.img;*.bin;*.*)|*.mov;*.raw;*.img;*.bin;*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            SourceFilePath = dialog.FileName;
            string dir = Path.GetDirectoryName(SourceFilePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(SourceFilePath);
            OutputMovPath = Path.Combine(dir, $"{name}_recovered.mov");
        }
    }

    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar Archivo MOV Recuperado",
            Filter = "Video QuickTime (*.mov)|*.mov",
            FileName = Path.GetFileName(OutputMovPath)
        };
        if (dialog.ShowDialog() == true)
        {
            OutputMovPath = dialog.FileName;
        }
    }

    private void CancelScan()
    {
        _cts?.Cancel();
        AppendLog("[!] Operación cancelada por el usuario.");
    }

    private async Task StartScanAsync()
    {
        IsScanning = true;
        ScanProgress = 0;
        LogOutput = string.Empty;
        DiscoveredFrames.Clear();
        _allScannedFrames.Clear();
        _cts = new CancellationTokenSource();

        var progress = new Progress<ScanProgressReport>(report =>
        {
            ScanProgress = report.Percentage;
            MetricsText = $"{report.FramesFound:N0} frames | {report.SpeedMBs:F0} MB/s | ETA: ~{report.EtaMinutes:F1} min";
            ScanStatus = report.StatusText;
        });

        try
        {
            var frames = await _scanner.ScanFramesAsync(
                SourceFilePath,
                FrameMin,
                FrameMax,
                bufferSize: BufferSizeMB * 1024 * 1024,
                progress: progress,
                log: AppendLog,
                cancellationToken: _cts.Token);

            _allScannedFrames = frames;

            if (!_cts.IsCancellationRequested && frames.Count > 0)
            {
                _detectedAudio = await _scanner.AnalyzeAudioAsync(SourceFilePath, frames, AppendLog);
            }

            foreach (var frame in frames.Take(500))
            {
                DiscoveredFrames.Add(frame);
            }

            ScanStatus = $"¡Escaneo completado! {frames.Count:N0} frames ProRes encontrados. Listo para construir MOV.";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Escaneo cancelado.";
        }
        catch (Exception ex)
        {
            ScanStatus = "Error en el escaneo.";
            AppendLog($"[ERROR] {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task BuildMovAsync()
    {
        IsBuildingMov = true;
        ScanProgress = 0;
        ScanStatus = "Construyendo contenedor MOV...";
        _cts = new CancellationTokenSource();

        var progress = new Progress<double>(pct =>
        {
            ScanProgress = pct;
            MetricsText = $"Construyendo MOV: {pct:F1}%";
        });

        try
        {
            bool ok = await _movBuilder.BuildMovAsync(
                SourceFilePath,
                OutputMovPath,
                _allScannedFrames,
                _detectedAudio,
                IncludeAudio,
                progress: progress,
                log: AppendLog,
                cancellationToken: _cts.Token);

            ScanStatus = ok ? "¡Archivo MOV reconstruido con éxito!" : "Error al construir el MOV.";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Construcción de MOV cancelada.";
        }
        catch (Exception ex)
        {
            ScanStatus = "Error al reconstruir MOV.";
            AppendLog($"[ERROR] {ex.Message}");
        }
        finally
        {
            IsBuildingMov = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void AppendLog(string message)
    {
        string entry = $"{DateTime.Now:HH:mm:ss} - {message}\n";
        if (LogOutput.Length > 60_000)
        {
            LogOutput = LogOutput[^30_000..] + entry;
        }
        else
        {
            LogOutput += entry;
        }
    }
}
