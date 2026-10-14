using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MediaRecovery.Models;
using MediaRecovery.Services;

namespace MediaRecovery.ViewModels;

public class DownloaderViewModel : ViewModelBase
{
    private readonly ProcessRunnerService _runner = new();
    private CancellationTokenSource? _cts;

    private string _url = string.Empty;
    private string _outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private bool _isVideo = true;
    private bool _isAudio;
    private string _mp3Quality = "320k";
    private bool _downloadFull = true;
    private bool _downloadCut;
    private string _startH = "00";
    private string _startM = "00";
    private string _startS = "00";
    private string _endH = "00";
    private string _endM = "01";
    private string _endS = "00";
    private bool _openFolderOnComplete = true;

    private bool _isBusy;
    private double _progress;
    private string _statusText = "Listo";
    private string _logOutput = string.Empty;

    public DownloaderViewModel()
    {
        PasteUrlCommand = new RelayCommand(_ => PasteFromClipboard());
        BrowseFolderCommand = new RelayCommand(_ => BrowseFolder());
        StartDownloadCommand = new RelayCommand(async _ => await StartDownloadAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(Url));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
    }

    public string Url { get => _url; set { SetField(ref _url, value); CommandManager.InvalidateRequerySuggested(); } }
    public string OutputFolder { get => _outputFolder; set => SetField(ref _outputFolder, value); }
    public bool IsVideo { get => _isVideo; set { SetField(ref _isVideo, value); if (value) IsAudio = false; } }
    public bool IsAudio { get => _isAudio; set { SetField(ref _isAudio, value); if (value) IsVideo = false; } }
    public string Mp3Quality { get => _mp3Quality; set => SetField(ref _mp3Quality, value); }
    public bool DownloadFull { get => _downloadFull; set { SetField(ref _downloadFull, value); if (value) DownloadCut = false; } }
    public bool DownloadCut { get => _downloadCut; set { SetField(ref _downloadCut, value); if (value) DownloadFull = false; } }

    public string StartH { get => _startH; set => SetField(ref _startH, value); }
    public string StartM { get => _startM; set => SetField(ref _startM, value); }
    public string StartS { get => _startS; set => SetField(ref _startS, value); }
    public string EndH { get => _endH; set => SetField(ref _endH, value); }
    public string EndM { get => _endM; set => SetField(ref _endM, value); }
    public string EndS { get => _endS; set => SetField(ref _endS, value); }
    public bool OpenFolderOnComplete { get => _openFolderOnComplete; set => SetField(ref _openFolderOnComplete, value); }

    public bool IsBusy { get => _isBusy; set { SetField(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }
    public double Progress { get => _progress; set => SetField(ref _progress, value); }
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }
    public string LogOutput { get => _logOutput; set => SetField(ref _logOutput, value); }

    public ICommand PasteUrlCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand StartDownloadCommand { get; }
    public ICommand CancelCommand { get; }

    private void PasteFromClipboard()
    {
        if (Clipboard.ContainsText())
        {
            Url = Clipboard.GetText().Trim();
        }
    }

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Seleccionar Carpeta de Descargas",
            InitialDirectory = OutputFolder
        };
        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        AppendLog("[!] Cancelación solicitada por el usuario.");
    }

    private async Task StartDownloadAsync()
    {
        IsBusy = true;
        Progress = 0;
        StatusText = "Procesando...";
        LogOutput = string.Empty;
        _cts = new CancellationTokenSource();

        AppendLog($"Iniciando tarea para: {Url}");
        AppendLog($"Carpeta de destino: {OutputFolder}");

        try
        {
            // Construir argumentos de yt-dlp reproduciendo exactamente descargar-videos.py
            string outputTemplate = Path.Combine(OutputFolder, "%(title)s.%(ext)s");
            var args = new System.Text.StringBuilder();

            if (IsAudio)
            {
                args.Append($"-x --audio-format mp3 --audio-quality {Mp3Quality.Replace("k", "")}K ");
            }
            else
            {
                args.Append("-f \"bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b\" --merge-output-format mp4 ");
            }

            if (DownloadCut)
            {
                string start = $"{StartH}:{StartM}:{StartS}";
                string end = $"{EndH}:{EndM}:{EndS}";
                args.Append($"--download-sections \"*{start}-{end}\" --force-keyframes-at-cuts ");
            }

            args.Append($"-o \"{outputTemplate}\" \"{Url}\"");

            AppendLog($"[Descarga] Iniciando obtención de contenido...");

            int exitCode = await _runner.RunAsync(
                "yt-dlp",
                args.ToString(),
                onOutputLine: line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    if (line.Contains("[download]") && line.Contains('%')) return; // Ya se muestra en barra de progreso
                    AppendLog(line.Trim());
                },
                onErrorLine: line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    if (line.Contains("[download]") && line.Contains('%')) return;
                    AppendLog(line.Trim());
                },
                onProgressPercentage: pct => Progress = pct,
                cancellationToken: _cts.Token);

            if (exitCode == 0)
            {
                StatusText = "¡Descarga completada con éxito!";
                AppendLog("[OK] Proceso finalizado exitosamente.");
                if (OpenFolderOnComplete && Directory.Exists(OutputFolder))
                {
                    Process.Start("explorer.exe", OutputFolder);
                }
            }
            else
            {
                StatusText = $"Finalizado con código de salida: {exitCode}";
                AppendLog($"[!] Proceso terminado con código {exitCode}.");
            }
        }
        catch (Exception ex)
        {
            StatusText = "Error durante la ejecución";
            AppendLog($"[ERROR] Excepción: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
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

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
