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
        if (sender is ListViewItem item && item.DataContext is VideoMetadataItem meta)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.LibraryTools.OpenFileLocationCommand.Execute(meta.FilePath);
            }
        }
    }

    #region Drag and Drop Handlers

    private void GenericFileDragOver(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    // 1. Conversor - Archivo individual (selectivo: 1 archivo -> individual, >1 -> redirige a lote)
    private void OnSingleFileDragOver(object sender, DragEventArgs e) => GenericFileDragOver(e);

    private void OnSingleFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Converter.HandleSingleFileDrop(files);
            }
            e.Handled = true;
        }
    }

    // 2. Conversor - Procesamiento por lotes (admite simples y múltiples)
    private void OnBatchFilesDragOver(object sender, DragEventArgs e) => GenericFileDragOver(e);

    private void OnBatchFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Converter.HandleBatchFilesDrop(files);
            }
            e.Handled = true;
        }
    }

    // 3. Recuperación ProRes
    private void OnRecoveryFileDragOver(object sender, DragEventArgs e) => GenericFileDragOver(e);

    private void OnRecoveryFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            string? firstFile = files.FirstOrDefault(f => !System.IO.Directory.Exists(f) && System.IO.File.Exists(f));
            if (firstFile != null && DataContext is MainViewModel vm)
            {
                vm.Recovery.SetSourceFile(firstFile);
            }
            e.Handled = true;
        }
    }

    // 4. Biblioteca - Carpeta A
    private void OnFolderADragOver(object sender, DragEventArgs e) => GenericFileDragOver(e);

    private void OnFolderADrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] items && items.Length > 0)
        {
            string first = items[0];
            string folder = System.IO.Directory.Exists(first) ? first : (System.IO.Path.GetDirectoryName(first) ?? "");
            if (!string.IsNullOrWhiteSpace(folder) && DataContext is MainViewModel vm)
            {
                vm.LibraryTools.FolderPathA = folder;
            }
            e.Handled = true;
        }
    }

    // 5. Duplicados - Carpeta B
    private void OnFolderBDragOver(object sender, DragEventArgs e) => GenericFileDragOver(e);

    private void OnFolderBDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] items && items.Length > 0)
        {
            string first = items[0];
            string folder = System.IO.Directory.Exists(first) ? first : (System.IO.Path.GetDirectoryName(first) ?? "");
            if (!string.IsNullOrWhiteSpace(folder) && DataContext is MainViewModel vm)
            {
                vm.LibraryTools.FolderPathB = folder;
            }
            e.Handled = true;
        }
    }

    #endregion
}
