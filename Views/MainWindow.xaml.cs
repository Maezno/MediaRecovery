using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaRecovery.Models;
using MediaRecovery.ViewModels;

namespace MediaRecovery.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnMetadataItemDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: VideoMetadataItem meta } && DataContext is MainViewModel vm)
        {
            vm.LibraryTools.OpenFileLocationCommand.Execute(meta.FilePath);
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = hasFiles;
    }

    private static string[]? GetDroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
            ? paths
            : null;

    private static string? GetDroppedFolder(DragEventArgs e)
    {
        var paths = GetDroppedPaths(e);
        if (paths is null) return null;
        string first = paths[0];
        return Directory.Exists(first) ? first : Path.GetDirectoryName(first);
    }

    private void OnSingleFileDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedPaths(e) is { } files && DataContext is MainViewModel vm)
        {
            vm.Converter.HandleSingleFileDrop(files);
            e.Handled = true;
        }
    }

    private void OnBatchFilesDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedPaths(e) is { } files && DataContext is MainViewModel vm)
        {
            vm.Converter.HandleBatchFilesDrop(files);
            e.Handled = true;
        }
    }

    private void OnRecoveryFileDrop(object sender, DragEventArgs e)
    {
        var firstFile = GetDroppedPaths(e)?.FirstOrDefault(f => !Directory.Exists(f) && File.Exists(f));
        if (firstFile != null && DataContext is MainViewModel vm)
        {
            vm.Recovery.SetSourceFile(firstFile);
            e.Handled = true;
        }
    }

    private void OnFolderADrop(object sender, DragEventArgs e)
    {
        if (GetDroppedFolder(e) is { Length: > 0 } folder && DataContext is MainViewModel vm)
        {
            vm.LibraryTools.FolderPathA = folder;
            e.Handled = true;
        }
    }

    private void OnFolderBDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedFolder(e) is { Length: > 0 } folder && DataContext is MainViewModel vm)
        {
            vm.LibraryTools.FolderPathB = folder;
            e.Handled = true;
        }
    }
}
