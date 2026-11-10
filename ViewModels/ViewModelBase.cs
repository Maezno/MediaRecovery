using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace MediaRecovery.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void AppendLogEntry(ref string logField, string message, string propertyName = "LogOutput")
    {
        string entry = $"{DateTime.Now:HH:mm:ss} - {message}\n";
        logField = logField.Length > 60_000
            ? logField[^30_000..] + entry
            : logField + entry;
        OnPropertyChanged(propertyName);
        CommandManager.InvalidateRequerySuggested();
    }

    protected static void CopyTextToClipboard(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
        }
    }

    public static string SanitizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Trim().Trim('"', '\'', '\u201C', '\u201D', '\r', '\n').Trim();
    }

    public static string SanitizeFolderPath(string? path)
    {
        string clean = SanitizePath(path);
        if (string.IsNullOrEmpty(clean)) return string.Empty;
        if (File.Exists(clean) && !Directory.Exists(clean))
        {
            return Path.GetDirectoryName(clean) ?? clean;
        }
        return clean;
    }

    protected static void OpenPathInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path))
            Process.Start("explorer.exe", $"\"{path}\"");
    }
}

public class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    private readonly Action<object?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
