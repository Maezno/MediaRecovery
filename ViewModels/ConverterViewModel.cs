using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MediaRecovery.Services;

namespace MediaRecovery.ViewModels;

public class BatchVideoItem : ViewModelBase
{
    private string _status = "Pendiente";
    private double _progress = 0;

    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    
    public string Status { get => _status; set => SetField(ref _status, value); }
    public double Progress { get => _progress; set => SetField(ref _progress, value); }
}

public class ConverterViewModel : ViewModelBase
{
    private readonly VideoConverterService _converter = new();
    private readonly VideoIntegrityService _integrityService = new();
    private readonly Action<string>? _navigateToRepair;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _integrityCts;

    // Archivo individual
    private string _sourceFile = string.Empty;
    private string _outputFile = string.Empty;

    // Advertencia de errores / integridad
    private bool _hasInputErrors;
    private bool _isCheckingIntegrity;
    private string _inputWarningTitle = string.Empty;
    private string _inputWarningMessage = string.Empty;

    // Parámetros de codificación recomendados (Presets V5)
    private string _selectedCodec = "H.264";
    private int _bitrateMbps = 25; // 25 Mbps de V5
    private string _selectedGpu = "Auto";
    private string _selectedDeinterlace = "🧠 Inteligente (detectar por archivo)";
    private bool _includeAudio = true;
    private string _selectedResolution = "Original";

    // Rango de tiempo / Fragmento
    private bool _convertFull = true;
    private bool _convertCut;
    private string _startH = "00";
    private string _startM = "00";
    private string _startS = "00";
    private string _endH = "00";
    private string _endM = "01";
    private string _endS = "00";

    // Lotes
    private ObservableCollection<BatchVideoItem> _batchItems = new();
    private BatchVideoItem? _selectedBatchItem;
    private string _batchOutputFolder = string.Empty;

    // Estado UI
    private bool _isBusy;
    private double _progress;
    private string _statusText = "Listo para convertir";
    private string _logOutput = string.Empty;

    public ConverterViewModel(Action<string>? navigateToRepair = null)
    {
        _navigateToRepair = navigateToRepair;

        BrowseSourceFileCommand = new RelayCommand(_ => BrowseSourceFile());
        BrowseOutputFileCommand = new RelayCommand(_ => BrowseOutputFile());
        BrowseBatchFolderCommand = new RelayCommand(_ => BrowseBatchFolder());
        
        AddBatchFilesCommand = new RelayCommand(_ => AddBatchFiles());
        RemoveBatchItemCommand = new RelayCommand(_ => RemoveBatchItem(), _ => SelectedBatchItem != null);
        ClearBatchCommand = new RelayCommand(_ => BatchItems.Clear(), _ => BatchItems.Count > 0);

        StartSingleConvertCommand = new RelayCommand(async _ => await StartSingleConvertAsync(), _ => !IsBusy && File.Exists(SourceFile));
        StartBatchConvertCommand = new RelayCommand(async _ => await StartBatchConvertAsync(), _ => !IsBusy && BatchItems.Count > 0);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);

        NavigateToRepairCommand = new RelayCommand(_ => NavigateToRepair(), _ => !string.IsNullOrWhiteSpace(SourceFile));
        CheckIntegrityCommand = new RelayCommand(async _ => await CheckIntegrityAsync(SourceFile), _ => !string.IsNullOrWhiteSpace(SourceFile));
        OpenOutputCommand = new RelayCommand(_ => OpenOutput(), _ => HasLastOutput);
    }

    private string _lastOutputPath = string.Empty;
    public string LastOutputPath
    {
        get => _lastOutputPath;
        set { SetField(ref _lastOutputPath, value); OnPropertyChanged(nameof(HasLastOutput)); CommandManager.InvalidateRequerySuggested(); }
    }
    public bool HasLastOutput => !string.IsNullOrWhiteSpace(LastOutputPath) && (File.Exists(LastOutputPath) || Directory.Exists(LastOutputPath));
    public ICommand OpenOutputCommand { get; }

    private void OpenOutput() => OpenPathInExplorer(LastOutputPath);

    public string SourceFile
    {
        get => _sourceFile;
        set
        {
            if (SetField(ref _sourceFile, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(OutputFile))
                {
                    string dir = Path.GetDirectoryName(value) ?? "";
                    string baseName = Path.GetFileNameWithoutExtension(value);
                    OutputFile = Path.Combine(dir, $"{baseName}_converted.mp4");
                }

                _ = CheckIntegrityAsync(value);
                _ = AutoDetectScanTypeAsync(value);
            }
        }
    }

    // Resultado de la última detección de barrido (null = desconocido)
    private bool? _detectedInterlaced;
    private string _scanTypeInfo = string.Empty;
    public string ScanTypeInfo { get => _scanTypeInfo; set => SetField(ref _scanTypeInfo, value); }

    private const string Deint50pLabel = "Desentrelazar -> 50p (Recomendado ProRes / Doble de cuadros)";
    private const string ProgressiveLabel = "Progresivo (No tocar el video)";
    private const string SmartLabel = "🧠 Inteligente (detectar por archivo)";

    private async Task AutoDetectScanTypeAsync(string path)
    {
        _detectedInterlaced = null;
        ScanTypeInfo = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        ScanTypeInfo = "🔍 Analizando barrido del video...";
        var info = await VideoConverterService.DetectScanTypeAsync(path);
        if (path != SourceFile) return; // el usuario cambió de archivo mientras analizábamos

        if (info == null || info.Method == "indeterminado")
        {
            ScanTypeInfo = "⚪ No se pudo determinar el barrido. Ajuste el desentrelazado manualmente.";
            return;
        }

        _detectedInterlaced = info.IsInterlaced;
        string codec = info.Codec.ToUpperInvariant();
        if (info.IsInterlaced)
        {
            string field = info.FieldOrder is "tt" or "tb" ? "TFF" : "BFF";
            SelectedDeinterlace = Deint50pLabel;
            ScanTypeInfo = $"🟠 {codec} entrelazado ({field}, por {info.Method}) → desentrelazado 50p activado automáticamente";
        }
        else
        {
            SelectedDeinterlace = ProgressiveLabel;
            ScanTypeInfo = $"🟢 {codec} progresivo (por {info.Method}) → desentrelazado desactivado (innecesario)";
        }
        AppendLog($"[Detección] {ScanTypeInfo}");
    }

    public string OutputFile { get => _outputFile; set => SetField(ref _outputFile, value); }
    public string SelectedCodec
    {
        get => _selectedCodec;
        set
        {
            if (SetField(ref _selectedCodec, value))
            {
                if (!string.IsNullOrWhiteSpace(OutputFile))
                {
                    string targetExt = value.Contains("ProRes", StringComparison.OrdinalIgnoreCase) ? ".mov" : ".mp4";
                    OutputFile = Path.ChangeExtension(OutputFile, targetExt);
                }

                if (value.Contains("ProRes", StringComparison.OrdinalIgnoreCase) && _detectedInterlaced != false)
                {
                    SelectedDeinterlace = Deint50pLabel;
                }
            }
        }
    }
    public int BitrateMbps { get => _bitrateMbps; set => SetField(ref _bitrateMbps, value); }
    public string SelectedGpu { get => _selectedGpu; set => SetField(ref _selectedGpu, value); }
    public string SelectedDeinterlace { get => _selectedDeinterlace; set => SetField(ref _selectedDeinterlace, value); }
    public bool IncludeAudio { get => _includeAudio; set => SetField(ref _includeAudio, value); }
    public string SelectedResolution { get => _selectedResolution; set => SetField(ref _selectedResolution, value); }

    public bool ConvertFull { get => _convertFull; set { SetField(ref _convertFull, value); if (value) ConvertCut = false; } }
    public bool ConvertCut { get => _convertCut; set { SetField(ref _convertCut, value); if (value) ConvertFull = false; } }
    public string StartH { get => _startH; set => SetField(ref _startH, value); }
    public string StartM { get => _startM; set => SetField(ref _startM, value); }
    public string StartS { get => _startS; set => SetField(ref _startS, value); }
    public string EndH { get => _endH; set => SetField(ref _endH, value); }
    public string EndM { get => _endM; set => SetField(ref _endM, value); }
    public string EndS { get => _endS; set => SetField(ref _endS, value); }

    // Advertencia de errores
    public bool HasInputErrors { get => _hasInputErrors; set { SetField(ref _hasInputErrors, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsCheckingIntegrity { get => _isCheckingIntegrity; set => SetField(ref _isCheckingIntegrity, value); }
    public string InputWarningTitle { get => _inputWarningTitle; set => SetField(ref _inputWarningTitle, value); }
    public string InputWarningMessage { get => _inputWarningMessage; set => SetField(ref _inputWarningMessage, value); }

    private int _selectedSubTabIndex;
    public int SelectedSubTabIndex { get => _selectedSubTabIndex; set => SetField(ref _selectedSubTabIndex, value); }

    public ObservableCollection<BatchVideoItem> BatchItems { get => _batchItems; set => SetField(ref _batchItems, value); }
    public BatchVideoItem? SelectedBatchItem { get => _selectedBatchItem; set => SetField(ref _selectedBatchItem, value); }
    public string BatchOutputFolder { get => _batchOutputFolder; set => SetField(ref _batchOutputFolder, value); }

    public void HandleSingleFileDrop(string[] files)
    {
        if (files == null || files.Length == 0) return;
        var validFiles = files.Where(f => !Directory.Exists(f) && File.Exists(f)).ToArray();
        if (validFiles.Length == 0) return;

        if (validFiles.Length == 1)
        {
            SourceFile = validFiles[0];
            SelectedSubTabIndex = 0; // Pestaña individual
        }
        else
        {
            // Múltiples archivos arrastrados al área individual: redirigir automáticamente a lote
            AddFilesToBatch(validFiles);
            SelectedSubTabIndex = 1; // Pestaña de lote
            AppendLog($"[Arrastrar y soltar] Se recibieron {validFiles.Length} archivos. Redirigiendo automáticamente a Procesamiento por lotes.");
        }
    }

    public void HandleBatchFilesDrop(string[] files)
    {
        if (files == null || files.Length == 0) return;
        var validFiles = files.Where(f => !Directory.Exists(f) && File.Exists(f)).ToArray();
        if (validFiles.Length > 0)
        {
            AddFilesToBatch(validFiles);
            SelectedSubTabIndex = 1;
        }
    }

    public void AddFilesToBatch(IEnumerable<string> filePaths)
    {
        int added = 0;
        foreach (var file in filePaths)
        {
            if (!BatchItems.Any(b => b.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase)))
            {
                BatchItems.Add(new BatchVideoItem { FilePath = file });
                added++;
            }
        }
        if (added > 0)
        {
            AppendLog($"[Lote] Añadidos {added} archivo(s) a la cola de procesamiento.");
        }
    }

    public bool IsBusy { get => _isBusy; set { SetField(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }
    public double Progress { get => _progress; set => SetField(ref _progress, value); }
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }
    public string LogOutput { get => _logOutput; set => SetField(ref _logOutput, value); }

    public ICommand BrowseSourceFileCommand { get; }
    public ICommand BrowseOutputFileCommand { get; }
    public ICommand BrowseBatchFolderCommand { get; }
    public ICommand AddBatchFilesCommand { get; }
    public ICommand RemoveBatchItemCommand { get; }
    public ICommand ClearBatchCommand { get; }
    public ICommand StartSingleConvertCommand { get; }
    public ICommand StartBatchConvertCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NavigateToRepairCommand { get; }
    public ICommand CheckIntegrityCommand { get; }

    public async Task CheckIntegrityAsync(string? filePath)
    {
        _integrityCts?.Cancel();
        _integrityCts = new CancellationTokenSource();
        var token = _integrityCts.Token;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            HasInputErrors = false;
            IsCheckingIntegrity = false;
            InputWarningTitle = string.Empty;
            InputWarningMessage = string.Empty;
            return;
        }

        IsCheckingIntegrity = true;

        try
        {
            var result = await _integrityService.CheckVideoIntegrityAsync(filePath, token);
            if (token.IsCancellationRequested) return;

            HasInputErrors = result.HasErrors;
            if (result.HasErrors)
            {
                InputWarningTitle = result.Title;
                InputWarningMessage = result.Description;
                AppendLog($"[ADVERTENCIA INTEGRIDAD] {result.Title}: {result.Description}");
            }
            else
            {
                InputWarningTitle = string.Empty;
                InputWarningMessage = string.Empty;
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                HasInputErrors = true;
                InputWarningTitle = "Error en comprobación de integridad";
                InputWarningMessage = ex.Message;
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsCheckingIntegrity = false;
            }
        }
    }

    private void NavigateToRepair()
    {
        if (!string.IsNullOrWhiteSpace(SourceFile))
        {
            AppendLog($"[Navegación] Enviando '{Path.GetFileName(SourceFile)}' a la pestaña de Reparación ProRes...");
            _navigateToRepair?.Invoke(SourceFile);
        }
    }

    private void BrowseSourceFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Seleccionar video para convertir",
            Filter = "Archivos de video (*.mov;*.mp4;*.mkv;*.avi;*.mxf)|*.mov;*.mp4;*.mkv;*.avi;*.mxf|Todos los archivos (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            SourceFile = dlg.FileName;
        }
    }

    private void BrowseOutputFile()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar video convertido",
            Filter = "Video MP4 (*.mp4)|*.mp4|Video QuickTime (*.mov)|*.mov",
            FileName = Path.GetFileName(OutputFile)
        };
        if (dlg.ShowDialog() == true)
        {
            OutputFile = dlg.FileName;
        }
    }

    private void BrowseBatchFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Seleccionar carpeta destino para lotes" };
        if (dlg.ShowDialog() == true)
        {
            BatchOutputFolder = dlg.FolderName;
        }
    }

    private void AddBatchFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Seleccionar videos para procesar por lote",
            Multiselect = true,
            Filter = "Archivos de video (*.mov;*.mp4;*.mkv;*.mxf)|*.mov;*.mp4;*.mkv;*.mxf|Todos (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            AddFilesToBatch(dlg.FileNames);
        }
    }

    private void RemoveBatchItem()
    {
        if (SelectedBatchItem != null)
        {
            BatchItems.Remove(SelectedBatchItem);
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        AppendLog("[!] Cancelación solicitada por el usuario.");
    }

    private DeinterlaceMode MapDeintMode()
    {
        if (SelectedDeinterlace.Contains("Inteligente")) return DeinterlaceMode.Auto;
        if (SelectedDeinterlace.Contains("50p")) return DeinterlaceMode.Deinterlace50p;
        if (SelectedDeinterlace.Contains("25p")) return DeinterlaceMode.Deinterlace25p;
        if (SelectedDeinterlace.Contains("Auto")) return DeinterlaceMode.Auto;
        return DeinterlaceMode.Progressive;
    }

    private HardwareGpuChoice MapGpuChoice()
    {
        if (SelectedGpu.Contains("Nvidia")) return HardwareGpuChoice.Nvidia;
        if (SelectedGpu.Contains("AMD")) return HardwareGpuChoice.Amd;
        if (SelectedGpu.Contains("CPU")) return HardwareGpuChoice.Cpu;
        return HardwareGpuChoice.Auto;
    }

    private string MapCodecChoice()
    {
        if (SelectedCodec.Contains("HEVC") || SelectedCodec.Contains("H.265")) return "h265";
        if (SelectedCodec.Contains("ProRes")) return "prores";
        return "h264";
    }

    private async Task StartSingleConvertAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceFile) || !File.Exists(SourceFile)) return;

        IsBusy = true;
        Progress = 0;
        LogOutput = string.Empty;
        _cts = new CancellationTokenSource();

        string? startTime = null;
        string? endTime = null;

        if (ConvertCut)
        {
            startTime = $"{StartH.PadLeft(2, '0')}:{StartM.PadLeft(2, '0')}:{StartS.PadLeft(2, '0')}";
            endTime = $"{EndH.PadLeft(2, '0')}:{EndM.PadLeft(2, '0')}:{EndS.PadLeft(2, '0')}";
            AppendLog($"[CORTE] Exportando fragmento: {startTime} -> {endTime}");
        }

        try
        {
            var job = new ConversionJob(
                SourceFile,
                OutputFile,
                Codec: MapCodecChoice(),
                BitrateMbps: BitrateMbps,
                Gpu: MapGpuChoice(),
                Deint: MapDeintMode(),
                IncludeAudio: IncludeAudio,
                Resolution: SelectedResolution.Equals("Original", StringComparison.OrdinalIgnoreCase) ? "original" : SelectedResolution,
                UseTimeRange: ConvertCut,
                StartTime: startTime,
                EndTime: endTime
            );

            int code = await _converter.ConvertVideoAsync(
                job,
                onLog: msg => AppendLog(msg),
                onProgress: (pct, txt) =>
                {
                    Progress = pct;
                    StatusText = txt;
                },
                cancellationToken: _cts.Token);

            if (code == 0)
            {
                Progress = 100;
                StatusText = "¡Conversión finalizada con éxito!";
                AppendLog("[OK] Archivo generado exitosamente.");
                LastOutputPath = OutputFile;
            }
            else
            {
                StatusText = $"Finalizado con código {code}";
                AppendLog($"[ERROR] Proceso de conversión falló ({code}).");
                _ = CheckIntegrityAsync(SourceFile);
            }
        }
        catch (Exception ex)
        {
            StatusText = "Error en conversión";
            AppendLog($"[EXCEPCIÓN] {ex.Message}");
            _ = CheckIntegrityAsync(SourceFile);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartBatchConvertAsync()
    {
        if (BatchItems.Count == 0) return;

        IsBusy = true;
        Progress = 0;
        LogOutput = string.Empty;
        _cts = new CancellationTokenSource();

        AppendLog($"[INICIO LOTE] Procesando {BatchItems.Count} archivos...");

        try
        {
            int total = BatchItems.Count;
            for (int i = 0; i < total; i++)
            {
                if (_cts.IsCancellationRequested) break;

                var item = BatchItems[i];
                item.Status = "En proceso...";
                StatusText = $"Procesando [{i + 1}/{total}]: {item.FileName}";

                string destFolder = string.IsNullOrWhiteSpace(BatchOutputFolder)
                    ? Path.GetDirectoryName(item.FilePath)!
                    : BatchOutputFolder;

                string outExt = MapCodecChoice() == "prores" ? ".mov" : ".mp4";
                string outPath = Path.Combine(destFolder, $"{Path.GetFileNameWithoutExtension(item.FilePath)}_converted{outExt}");

                DeinterlaceMode deint = MapDeintMode();
                if (SelectedDeinterlace == SmartLabel)
                {
                    var scan = await VideoConverterService.DetectScanTypeAsync(item.FilePath, _cts.Token);
                    deint = scan?.IsInterlaced == true ? DeinterlaceMode.Deinterlace50p
                          : scan?.Method == "indeterminado" || scan == null ? DeinterlaceMode.Auto
                          : DeinterlaceMode.Progressive;
                    string desc = deint switch
                    {
                        DeinterlaceMode.Deinterlace50p => "entrelazado → desentrelazar 50p",
                        DeinterlaceMode.Progressive => "progresivo → sin desentrelazado",
                        _ => "indeterminado → yadif automático"
                    };
                    AppendLog($"[{item.FileName}] Detección: {scan?.Codec.ToUpperInvariant() ?? "?"} {desc}");
                }

                var job = new ConversionJob(
                    item.FilePath,
                    outPath,
                    Codec: MapCodecChoice(),
                    BitrateMbps: BitrateMbps,
                    Gpu: MapGpuChoice(),
                    Deint: deint,
                    IncludeAudio: IncludeAudio,
                    Resolution: SelectedResolution.Equals("Original", StringComparison.OrdinalIgnoreCase) ? "original" : SelectedResolution
                );

                int code = await _converter.ConvertVideoAsync(
                    job,
                    onLog: msg => AppendLog($"[{item.FileName}] {msg}"),
                    onProgress: (pct, txt) =>
                    {
                        item.Progress = pct;
                        Progress = ((i + (pct / 100.0)) / total) * 100.0;
                    },
                    cancellationToken: _cts.Token);

                if (code == 0)
                {
                    item.Progress = 100;
                    item.Status = "Completado";
                }
                else
                {
                    item.Status = "Error";
                }
            }

            StatusText = "Lote finalizado.";
            AppendLog("[LOTE] Todos los archivos fueron procesados.");
            if (BatchItems.Any(b => b.Status == "Completado"))
            {
                string firstItem = BatchItems.First(b => b.Status == "Completado").FilePath;
                string dest = string.IsNullOrWhiteSpace(BatchOutputFolder) ? (Path.GetDirectoryName(firstItem) ?? "") : BatchOutputFolder;
                LastOutputPath = dest;
            }
        }
        catch (Exception ex)
        {
            StatusText = "Error en procesamiento de lote";
            AppendLog($"[EXCEPCIÓN] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AppendLog(string message) => AppendLogEntry(ref _logOutput, message);
}
