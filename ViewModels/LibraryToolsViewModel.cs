using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MediaRecovery.Models;
using MediaRecovery.Services;

namespace MediaRecovery.ViewModels;

public class LibraryToolsViewModel : ViewModelBase
{
    private readonly VideoLibraryService _libraryService = new();
    private readonly Action<string>? _onSendToRepair;
    private readonly Action<string>? _onSendToConverter;

    private CancellationTokenSource? _cts;

    // Carpetas
    private string _folderPathA = string.Empty;
    private string _folderPathB = string.Empty;
    private bool _isRecursive = true;
    private bool _compareTwoFolders;

    // Herramienta activa
    // 0 = Analizador Metadatos / Dispositivos
    // 1 = Buscador de Videos (Horizontal / Vertical)
    // 2 = Videos Rotos o Dañados
    // 3 = Videos Sin Compresión / Pesados
    // 4 = Buscador de Duplicados
    private int _selectedToolIndex = 0;

    // Filtros de fecha (Analizador)
    private string _selectedMonth = "Todos";
    private string _selectedYear = "Todos";

    // Filtros Buscador de Orientación
    private string _searchWord = string.Empty;
    private string _selectedOrientationFilter = "Todos";
    public List<string> OrientationFilterList { get; } = ["Todos", "Horizontal", "Vertical"];

    // Opciones Videos Rotos
    private bool _deepDecodeCheck;

    // Opciones Videos Pesados
    private double _minBitrateMbps = 40.0;
    private double _minSizeGB = 1.0;

    // Estado UI
    private bool _isScanning;
    private double _scanProgress;
    private string _statusText = "Seleccione una carpeta para comenzar.";
    private string _metricsText = string.Empty;

    // Resultados
    private ObservableCollection<VideoMetadataItem> _metadataItems = [];
    private ObservableCollection<DeviceSummaryItem> _deviceSummaries = [];
    private DeviceSummaryItem? _selectedDeviceSummary;
    private VideoMetadataItem? _selectedMetadataItem;

    private ObservableCollection<BrokenVideoItem> _brokenItems = [];
    private BrokenVideoItem? _selectedBrokenItem;

    private ObservableCollection<HeavyVideoItem> _heavyItems = [];
    private HeavyVideoItem? _selectedHeavyItem;

    private ObservableCollection<DuplicateVideoItem> _duplicateItems = [];
    private DuplicateVideoItem? _selectedDuplicateItem;

    public List<string> MonthsList { get; } =
    [
        "Todos", "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"
    ];

    public List<string> YearsList { get; } = ["Todos", "2026", "2025", "2024", "2023", "2022", "2021", "2020", "2019", "2018"];

    public LibraryToolsViewModel(Action<string>? onSendToRepair = null, Action<string>? onSendToConverter = null)
    {
        _onSendToRepair = onSendToRepair;
        _onSendToConverter = onSendToConverter;

        BrowseFolderACommand = new RelayCommand(_ => BrowseFolderA());
        BrowseFolderBCommand = new RelayCommand(_ => BrowseFolderB());

        StartScanCommand = new RelayCommand(async _ => await StartScanAsync(), _ => !IsScanning && Directory.Exists(FolderPathA));
        CancelScanCommand = new RelayCommand(_ => CancelScan(), _ => IsScanning);

        OpenFileLocationCommand = new RelayCommand(param => OpenFileInExplorer(param as string));
        SendToRepairCommand = new RelayCommand(param => SendToRepair(param as string));
        SendToConverterCommand = new RelayCommand(param => SendToConverter(param as string));

        ExportCsvSummaryCommand = new RelayCommand(_ => ExportCsv(false), _ => DeviceSummaries.Count > 0);
        ExportCsvDetailCommand = new RelayCommand(_ => ExportCsv(true), _ => MetadataItems.Count > 0);

        DeleteDuplicateCommand = new RelayCommand(param => DeleteDuplicateFile(param as DuplicateVideoItem));
    }

    // Propiedades de configuración
    public string FolderPathA { get => _folderPathA; set { SetField(ref _folderPathA, value); CommandManager.InvalidateRequerySuggested(); } }
    public string FolderPathB { get => _folderPathB; set => SetField(ref _folderPathB, value); }
    public bool IsRecursive { get => _isRecursive; set => SetField(ref _isRecursive, value); }
    public bool CompareTwoFolders { get => _compareTwoFolders; set => SetField(ref _compareTwoFolders, value); }

    public int SelectedToolIndex { get => _selectedToolIndex; set => SetField(ref _selectedToolIndex, value); }

    public string SelectedMonth { get => _selectedMonth; set => SetField(ref _selectedMonth, value); }
    public string SelectedYear { get => _selectedYear; set => SetField(ref _selectedYear, value); }

    public string SearchWord
    {
        get => _searchWord;
        set
        {
            if (SetField(ref _searchWord, value))
            {
                OnPropertyChanged(nameof(FilteredOrientationVideos));
            }
        }
    }

    public string SelectedOrientationFilter
    {
        get => _selectedOrientationFilter;
        set
        {
            if (SetField(ref _selectedOrientationFilter, value))
            {
                OnPropertyChanged(nameof(FilteredOrientationVideos));
            }
        }
    }

    public bool DeepDecodeCheck { get => _deepDecodeCheck; set => SetField(ref _deepDecodeCheck, value); }
    public double MinBitrateMbps { get => _minBitrateMbps; set => SetField(ref _minBitrateMbps, value); }
    public double MinSizeGB { get => _minSizeGB; set => SetField(ref _minSizeGB, value); }

    // Progreso
    public bool IsScanning { get => _isScanning; set { SetField(ref _isScanning, value); CommandManager.InvalidateRequerySuggested(); } }
    public double ScanProgress { get => _scanProgress; set => SetField(ref _scanProgress, value); }
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }
    public string MetricsText { get => _metricsText; set => SetField(ref _metricsText, value); }

    // Colecciones de resultados
    public ObservableCollection<VideoMetadataItem> MetadataItems { get => _metadataItems; set => SetField(ref _metadataItems, value); }
    public ObservableCollection<DeviceSummaryItem> DeviceSummaries { get => _deviceSummaries; set => SetField(ref _deviceSummaries, value); }
    public DeviceSummaryItem? SelectedDeviceSummary
    {
        get => _selectedDeviceSummary;
        set
        {
            if (SetField(ref _selectedDeviceSummary, value))
            {
                OnPropertyChanged(nameof(FilteredVideosForSelectedDevice));
            }
        }
    }
    public VideoMetadataItem? SelectedMetadataItem { get => _selectedMetadataItem; set => SetField(ref _selectedMetadataItem, value); }

    public IEnumerable<VideoMetadataItem> FilteredVideosForSelectedDevice
    {
        get
        {
            if (SelectedDeviceSummary == null) return MetadataItems;
            return MetadataItems.Where(x => x.DeviceModel.Equals(SelectedDeviceSummary.DeviceModel, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IEnumerable<VideoMetadataItem> FilteredOrientationVideos
    {
        get
        {
            IEnumerable<VideoMetadataItem> result = MetadataItems;

            if (!string.IsNullOrWhiteSpace(SearchWord))
            {
                result = result.Where(x => x.FileName.Contains(SearchWord, StringComparison.OrdinalIgnoreCase) ||
                                           x.Directory.Contains(SearchWord, StringComparison.OrdinalIgnoreCase));
            }

            if (SelectedOrientationFilter == "Vertical")
            {
                result = result.Where(x => x.Orientation.Equals("Vertical", StringComparison.OrdinalIgnoreCase));
            }
            else if (SelectedOrientationFilter == "Horizontal")
            {
                result = result.Where(x => x.Orientation.Equals("Horizontal", StringComparison.OrdinalIgnoreCase));
            }

            return result;
        }
    }

    public ObservableCollection<BrokenVideoItem> BrokenItems { get => _brokenItems; set => SetField(ref _brokenItems, value); }
    public BrokenVideoItem? SelectedBrokenItem { get => _selectedBrokenItem; set => SetField(ref _selectedBrokenItem, value); }

    public ObservableCollection<HeavyVideoItem> HeavyItems { get => _heavyItems; set => SetField(ref _heavyItems, value); }
    public HeavyVideoItem? SelectedHeavyItem { get => _selectedHeavyItem; set => SetField(ref _selectedHeavyItem, value); }

    public ObservableCollection<DuplicateVideoItem> DuplicateItems { get => _duplicateItems; set => SetField(ref _duplicateItems, value); }
    public DuplicateVideoItem? SelectedDuplicateItem { get => _selectedDuplicateItem; set => SetField(ref _selectedDuplicateItem, value); }

    // Comandos
    public ICommand BrowseFolderACommand { get; }
    public ICommand BrowseFolderBCommand { get; }
    public ICommand StartScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand OpenFileLocationCommand { get; }
    public ICommand SendToRepairCommand { get; }
    public ICommand SendToConverterCommand { get; }
    public ICommand ExportCsvSummaryCommand { get; }
    public ICommand ExportCsvDetailCommand { get; }
    public ICommand DeleteDuplicateCommand { get; }

    private void BrowseFolderA()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Seleccionar carpeta principal de videos" };
        if (dlg.ShowDialog() == true)
        {
            FolderPathA = dlg.FolderName;
        }
    }

    private void BrowseFolderB()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Seleccionar segunda carpeta para comparar duplicados" };
        if (dlg.ShowDialog() == true)
        {
            FolderPathB = dlg.FolderName;
        }
    }

    private async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPathA) || !Directory.Exists(FolderPathA))
        {
            MessageBox.Show("Seleccione una carpeta válida antes de iniciar el análisis.", "Carpeta no encontrada", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsScanning = true;
        ScanProgress = 0;
        StatusText = "Buscando archivos de video...";
        MetricsText = string.Empty;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var filesA = await Task.Run(() => _libraryService.FindVideoFiles(FolderPathA, IsRecursive), token);
            if (token.IsCancellationRequested) return;

            if (filesA.Count == 0)
            {
                StatusText = "No se encontraron videos compatibles en la carpeta seleccionada.";
                IsScanning = false;
                return;
            }

            StatusText = $"Se encontraron {filesA.Count} videos. Iniciando procesamiento...";

            switch (SelectedToolIndex)
            {
                case 0:
                    // 1. Analizador de Metadatos / Dispositivos
                    await ExecuteMetadataScanAsync(filesA, token);
                    break;

                case 1:
                    // 2. Buscador de Videos Horizontales / Verticales
                    await ExecuteOrientationSearchScanAsync(filesA, token);
                    break;

                case 2:
                    // 3. Videos Rotos o Dañados
                    await ExecuteBrokenVideosScanAsync(filesA, token);
                    break;

                case 3:
                    // 4. Videos Sin Compresión / Pesados
                    await ExecuteHeavyVideosScanAsync(filesA, token);
                    break;

                case 4:
                    // 5. Buscador de Duplicados
                    await ExecuteDuplicateScanAsync(filesA, token);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operación cancelada por el usuario.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error durante el análisis: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task ExecuteMetadataScanAsync(List<string> files, CancellationToken token)
    {
        MetadataItems.Clear();
        DeviceSummaries.Clear();

        int? monthFilter = SelectedMonth != "Todos" ? MonthsList.IndexOf(SelectedMonth) : null;
        int? yearFilter = int.TryParse(SelectedYear, out int y) ? y : null;

        var items = await _libraryService.ScanMetadataAsync(
            files,
            monthFilter,
            yearFilter,
            onProgress: (done, total, file) =>
            {
                ScanProgress = (done / (double)total) * 100.0;
                StatusText = $"Analizando metadatos ({done}/{total}): {file}";
            },
            cancellationToken: token);

        if (token.IsCancellationRequested) return;

        MetadataItems = new ObservableCollection<VideoMetadataItem>(items);
        OnPropertyChanged(nameof(FilteredVideosForSelectedDevice));

        var summaries = _libraryService.BuildDeviceSummaries(items);
        DeviceSummaries = new ObservableCollection<DeviceSummaryItem>(summaries);

        int totalV = items.Count(x => x.Orientation.Equals("Vertical", StringComparison.OrdinalIgnoreCase));
        int totalH = items.Count(x => x.Orientation.Equals("Horizontal", StringComparison.OrdinalIgnoreCase));

        StatusText = $"Análisis finalizado: {items.Count:N0} videos analizados.";
        MetricsText = $"{items.Count:N0} videos  •  {totalV:N0} verticales  •  {totalH:N0} horizontales  •  {summaries.Count} dispositivos";
    }

    private async Task ExecuteOrientationSearchScanAsync(List<string> files, CancellationToken token)
    {
        MetadataItems.Clear();

        var items = await _libraryService.ScanMetadataAsync(
            files,
            filterMonth: null,
            filterYear: null,
            onProgress: (done, total, file) =>
            {
                ScanProgress = (done / (double)total) * 100.0;
                StatusText = $"Analizando orientación ({done}/{total}): {file}";
            },
            cancellationToken: token);

        if (token.IsCancellationRequested) return;

        MetadataItems = new ObservableCollection<VideoMetadataItem>(items);
        OnPropertyChanged(nameof(FilteredOrientationVideos));

        int totalV = items.Count(x => x.Orientation.Equals("Vertical", StringComparison.OrdinalIgnoreCase));
        int totalH = items.Count(x => x.Orientation.Equals("Horizontal", StringComparison.OrdinalIgnoreCase));

        StatusText = $"Búsqueda lista: {items.Count:N0} videos analizados.";
        MetricsText = $"{items.Count:N0} videos en total  •  {totalV:N0} verticales  •  {totalH:N0} horizontales";
    }

    private async Task ExecuteBrokenVideosScanAsync(List<string> files, CancellationToken token)
    {
        BrokenItems.Clear();

        var broken = await _libraryService.ScanBrokenVideosAsync(
            files,
            deepDecodeCheck: DeepDecodeCheck,
            onProgress: (done, total, file) =>
            {
                ScanProgress = (done / (double)total) * 100.0;
                StatusText = $"Comprobando integridad ({done}/{total}): {file}";
            },
            cancellationToken: token);

        if (token.IsCancellationRequested) return;

        BrokenItems = new ObservableCollection<BrokenVideoItem>(broken);

        int proResCount = broken.Count(x => x.IsProResCandidate);
        StatusText = $"Comprobación completada: {broken.Count:N0} videos con fallas detectados.";
        MetricsText = broken.Count == 0
            ? "¡Excelente! No se detectaron videos dañados."
            : $"{broken.Count:N0} dañados  •  {proResCount:N0} con tramas ProRes recuperables";
    }

    private async Task ExecuteHeavyVideosScanAsync(List<string> files, CancellationToken token)
    {
        HeavyItems.Clear();

        var heavy = await _libraryService.ScanHeavyVideosAsync(
            files,
            minBitrateMbps: MinBitrateMbps,
            minSizeGB: MinSizeGB,
            onProgress: (done, total, file) =>
            {
                ScanProgress = (done / (double)total) * 100.0;
                StatusText = $"Inspeccionando codecs ({done}/{total}): {file}";
            },
            cancellationToken: token);

        if (token.IsCancellationRequested) return;

        HeavyItems = new ObservableCollection<HeavyVideoItem>(heavy);

        double totalGb = heavy.Sum(x => x.SizeBytes) / (1024.0 * 1024.0 * 1024.0);
        StatusText = $"Búsqueda finalizada: {heavy.Count:N0} videos pesados o sin compresión encontrados.";
        MetricsText = $"{heavy.Count:N0} archivos  •  Ocupan {totalGb:F2} GB en total";
    }

    private async Task ExecuteDuplicateScanAsync(List<string> filesA, CancellationToken token)
    {
        DuplicateItems.Clear();

        List<string>? filesB = null;
        if (CompareTwoFolders && !string.IsNullOrWhiteSpace(FolderPathB) && Directory.Exists(FolderPathB))
        {
            filesB = await Task.Run(() => _libraryService.FindVideoFiles(FolderPathB, IsRecursive), token);
        }

        var dupes = await _libraryService.ScanDuplicatesAsync(
            filesA,
            filesB,
            onProgress: (done, total, file) =>
            {
                ScanProgress = total > 0 ? (done / (double)total) * 100.0 : 0;
                StatusText = $"Comparando firmas ({done}/{total}): {file}";
            },
            cancellationToken: token);

        if (token.IsCancellationRequested) return;

        DuplicateItems = new ObservableCollection<DuplicateVideoItem>(dupes);

        int groupsCount = dupes.Select(x => x.GroupId).Distinct().Count();
        long wastedBytes = dupes.Where(x => !x.IsPrimary).Sum(x => x.SizeBytes);
        double wastedGb = wastedBytes / (1024.0 * 1024.0 * 1024.0);

        StatusText = $"Búsqueda finalizada: {groupsCount} grupos de videos duplicados encontrados.";
        MetricsText = dupes.Count == 0
            ? "No se encontraron videos duplicados."
            : $"{dupes.Count} archivos en {groupsCount} grupos  •  Espacio recuperable: ~{wastedGb:F2} GB";
    }

    private void CancelScan()
    {
        _cts?.Cancel();
        StatusText = "Deteniendo escaneo...";
    }

    private void OpenFileInExplorer(string? filePath)
    {
        try { OpenPathInExplorer(filePath); }
        catch (Exception ex) { MessageBox.Show($"No se pudo abrir la ubicación: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SendToRepair(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        _onSendToRepair?.Invoke(filePath);
    }

    private void SendToConverter(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        _onSendToConverter?.Invoke(filePath);
    }

    private void DeleteDuplicateFile(DuplicateVideoItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;

        var result = MessageBox.Show(
            $"¿Desea enviar a la Papelera de Reciclaje el archivo duplicado?\n\n{item.FileName}\nUbicación: {item.Directory}",
            "Confirmar eliminación",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                // Enviar a la papelera con la API segura de Windows
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    item.FilePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                DuplicateItems.Remove(item);
                StatusText = $"Archivo '{item.FileName}' enviado a la Papelera.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo eliminar el archivo: {ex.Message}", "Error al eliminar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ExportCsv(bool exportDetails)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = exportDetails ? "Exportar Detalle de Videos a CSV" : "Exportar Resumen por Dispositivo a CSV",
            Filter = "Archivo CSV (*.csv)|*.csv",
            FileName = exportDetails ? $"detalle_videos_{DateTime.Now:yyyyMMdd_HHmm}.csv" : $"resumen_dispositivos_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                VideoLibraryService.ExportMetadataToCsv(dlg.FileName, MetadataItems, DeviceSummaries, exportDetails);
                MessageBox.Show($"Reporte guardado con éxito en:\n{dlg.FileName}", "Exportación exitosa", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al exportar CSV: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
