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

    public bool IsCheckingUpdate { get => _isCheckingUpdate; set => SetField(ref _isCheckingUpdate, value); }
    public bool IsUpdating { get => _isUpdating; set => SetField(ref _isUpdating, value); }
    public bool HasUpdateNotice { get => _hasUpdateNotice; set => SetField(ref _hasUpdateNotice, value); }
    public string UpdateNoticeText { get => _updateNoticeText; set => SetField(ref _updateNoticeText, value); }
    public string UpdateButtonText { get => _updateButtonText; set => SetField(ref _updateButtonText, value); }
    public double UpdateDownloadProgress { get => _updateDownloadProgress; set => SetField(ref _updateDownloadProgress, value); }

    public System.Windows.Input.ICommand CheckUpdateCommand { get; }
    public System.Windows.Input.ICommand DownloadUpdateCommand { get; }
    public System.Windows.Input.ICommand DismissUpdateCommand { get; }

    public MainViewModel()
    {
        CheckUpdateCommand = new RelayCommand(async _ => await CheckForUpdatesAsync(manual: true));
        DownloadUpdateCommand = new RelayCommand(async _ => await ApplyUpdateAsync());
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
            else if (manual)
            {
                UpdateNoticeText = $"Tienes la última versión instalada (v{result.CurrentVersion}).";
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

    private async Task ApplyUpdateAsync()
    {
        if (_lastUpdateResult == null || string.IsNullOrWhiteSpace(_lastUpdateResult.DownloadUrl))
        {
            // Si no hay asset binario directo, abrir release en el navegador
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"https://github.com/maezno/MediaRecovery/releases/tag/v{_lastUpdateResult?.LatestVersion ?? "latest"}",
                UseShellExecute = true
            });
            return;
        }

        IsUpdating = true;
        UpdateNoticeText = "Descargando actualización e instalando...";

        var progress = new Progress<double>(pct =>
        {
            UpdateDownloadProgress = pct;
            UpdateNoticeText = $"Descargando actualización: {pct:F0}%";
        });

        try
        {
            await _updateService.DownloadAndInstallUpdateAsync(_lastUpdateResult.DownloadUrl, progress);
        }
        catch (Exception ex)
        {
            UpdateNoticeText = $"Error al descargar: {ex.Message}";
            IsUpdating = false;
        }
    }
}
