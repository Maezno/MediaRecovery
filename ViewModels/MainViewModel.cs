using MediaRecovery.Services;

namespace MediaRecovery.ViewModels;

public class MainViewModel : ViewModelBase
{
    private int _selectedTabIndex;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetField(ref _selectedTabIndex, value);
    }

    public DownloaderViewModel Downloader { get; } = new();
    public RecoveryViewModel Recovery { get; } = new();
    public ConverterViewModel Converter { get; }
    public LibraryToolsViewModel LibraryTools { get; }

    // Actualizador
    private readonly UpdateService _updateService = new();
    private bool _isCheckingUpdate;
    private bool _isUpdating;
    private bool _hasUpdateNotice;
    private string _updateNoticeText = string.Empty;
    private string _updateButtonText = "Actualizar";
    private double _updateDownloadProgress;
    private UpdateCheckResult? _lastUpdateResult;

    public string AppVersionBadge => $"v{UpdateService.GetCurrentVersionString()} (.NET 10)";
    public string AppTitle => $"MediaRecovery Studio v{UpdateService.GetCurrentVersionString()} (.NET 10)";
    public bool HasUpdateAction => !string.IsNullOrWhiteSpace(UpdateButtonText);

    public bool IsCheckingUpdate 
    { 
        get => _isCheckingUpdate; 
        set 
        { 
            if (SetField(ref _isCheckingUpdate, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        } 
    }

    public bool IsUpdating 
    { 
        get => _isUpdating; 
        set 
        { 
            if (SetField(ref _isUpdating, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        } 
    }

    public bool HasUpdateNotice { get => _hasUpdateNotice; set => SetField(ref _hasUpdateNotice, value); }
    public string UpdateNoticeText { get => _updateNoticeText; set => SetField(ref _updateNoticeText, value); }
    public string UpdateButtonText 
    { 
        get => _updateButtonText; 
        set 
        { 
            if (SetField(ref _updateButtonText, value))
                OnPropertyChanged(nameof(HasUpdateAction));
        } 
    }
    public double UpdateDownloadProgress { get => _updateDownloadProgress; set => SetField(ref _updateDownloadProgress, value); }

    public System.Windows.Input.ICommand CheckUpdateCommand { get; }
    public System.Windows.Input.ICommand DownloadUpdateCommand { get; }
    public System.Windows.Input.ICommand DismissUpdateCommand { get; }

    public MainViewModel()
    {
        CheckUpdateCommand = new RelayCommand(async _ => await CheckForUpdatesAsync(manual: true), _ => !IsCheckingUpdate && !IsUpdating);
        DownloadUpdateCommand = new RelayCommand(async _ => await ApplyUpdateAsync(), _ => !IsUpdating);
        DismissUpdateCommand = new RelayCommand(_ => HasUpdateNotice = false);

        Converter = new ConverterViewModel(filePath =>
        {
            Recovery.SetSourceFile(filePath);
            SelectedTabIndex = 1; // Pestaña 1: Recuperación ProRes
        });

        LibraryTools = new LibraryToolsViewModel(
            onSendToRepair: filePath =>
            {
                Recovery.SetSourceFile(filePath);
                SelectedTabIndex = 1; // Pestaña 1: Recuperación ProRes
            },
            onSendToConverter: filePath =>
            {
                Converter.SourceFile = filePath;
                SelectedTabIndex = 2; // Pestaña 2: Convertir Videos & Lotes
            }
        );

        // Chequeo en segundo plano no intrusivo al iniciar la aplicación
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            await CheckForUpdatesAsync(manual: false);
        });
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsCheckingUpdate || IsUpdating) return;
        IsCheckingUpdate = true;

        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            _lastUpdateResult = result;

            if (result.UpdateAvailable)
            {
                UpdateNoticeText = $"¡Nueva versión v{result.LatestVersion} disponible! (Actual: v{result.CurrentVersion})";
                UpdateButtonText = $"Actualizar a v{result.LatestVersion}";
                HasUpdateNotice = true;
            }
            else if (!string.IsNullOrWhiteSpace(result.ReleaseNotes) && (result.ReleaseNotes.StartsWith("No se pudo", StringComparison.OrdinalIgnoreCase) || result.ReleaseNotes.StartsWith("Error", StringComparison.OrdinalIgnoreCase)))
            {
                if (manual)
                {
                    UpdateNoticeText = result.ReleaseNotes;
                    UpdateButtonText = "";
                    HasUpdateNotice = true;
                }
            }
            else if (manual)
            {
                UpdateNoticeText = $"Tienes la versión más reciente instalada (v{result.CurrentVersion}).";
                UpdateButtonText = "";
                HasUpdateNotice = true;
            }
        }
        catch (Exception ex)
        {
            if (manual)
            {
                UpdateNoticeText = $"Error al buscar actualizaciones: {ex.Message}";
                HasUpdateNotice = true;
            }
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private string? _downloadedTempPath;
    private bool _isUpdateDownloaded;
    public bool IsUpdateDownloaded { get => _isUpdateDownloaded; set => SetField(ref _isUpdateDownloaded, value); }

    private async Task ApplyUpdateAsync()
    {
        // Si ya está descargada en el archivo temporal, el usuario hizo clic en "Reiniciar y actualizar"
        if (IsUpdateDownloaded && !string.IsNullOrWhiteSpace(_downloadedTempPath))
        {
            _updateService.ApplyUpdateAndRestart(_downloadedTempPath);
            return;
        }

        if (_lastUpdateResult == null || string.IsNullOrWhiteSpace(_lastUpdateResult.DownloadUrl))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"https://github.com/maezno/MediaRecovery/releases/tag/v{_lastUpdateResult?.LatestVersion ?? "latest"}",
                UseShellExecute = true
            });
            return;
        }

        IsUpdating = true;
        UpdateNoticeText = "Descargando actualización en segundo plano...";

        var progress = new Progress<(double Pct, double SpeedMBs)>(p =>
        {
            UpdateDownloadProgress = p.Pct;
            UpdateNoticeText = $"Descargando actualización: {p.Pct:F0}% ({p.SpeedMBs:F1} MB/s)";
        });

        try
        {
            string? tempPath = await _updateService.DownloadUpdateAsync(_lastUpdateResult.DownloadUrl, progress);
            if (!string.IsNullOrWhiteSpace(tempPath) && System.IO.File.Exists(tempPath))
            {
                _downloadedTempPath = tempPath;
                IsUpdateDownloaded = true;
                IsUpdating = false;
                UpdateNoticeText = $"¡Actualización v{_lastUpdateResult.LatestVersion} lista! Haz clic para reiniciar y actualizar.";
                UpdateButtonText = "🔄 Reiniciar y actualizar";
            }
            else
            {
                UpdateNoticeText = "No se pudo completar la descarga del archivo.";
                IsUpdating = false;
            }
        }
        catch (Exception ex)
        {
            UpdateNoticeText = $"Error al descargar: {ex.Message}";
            IsUpdating = false;
        }
    }
}
