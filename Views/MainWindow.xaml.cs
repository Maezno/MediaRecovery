using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaRecovery.Models;
using MediaRecovery.ViewModels;

namespace MediaRecovery.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(this, OnGlobalTextBoxPasting);
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewPasteExecuted);
        CommandManager.AddPreviewCanExecuteHandler(this, OnPreviewPasteCanExecute);
    }

    private void OnPreviewPasteCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        if (e.OriginalSource is TextBox { IsReadOnly: false } && Clipboard.ContainsFileDropList())
        {
            e.CanExecute = true;
            e.Handled = true;
        }
    }

    private void OnPreviewPasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        if (e.OriginalSource is not TextBox tb || tb.IsReadOnly) return;

        // Cuando se hace Ctrl+C a un archivo/carpeta en el Explorador de Windows, el portapapeles contiene FileDropList (no texto)
        // y el TextBox de WPF por defecto ignora el comando Paste antes de disparar DataObject.Pasting.
        if (Clipboard.ContainsFileDropList() && !Clipboard.ContainsText())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0 && files[0] is { } firstPath)
            {
                string resolved = ResolvePathForTextBox(tb, firstPath);
                ReplaceTextBoxValue(tb, resolved);
                e.Handled = true;
            }
        }
    }

    private void OnGlobalTextBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.Source is not TextBox tb || tb.IsReadOnly) return;

        // 1. Si el usuario copió un archivo o carpeta en el Explorador de Windows (Ctrl+C sobre archivo) y pega (Ctrl+V) en una barra de ruta
        if (e.DataObject.GetDataPresent(DataFormats.FileDrop) &&
            e.DataObject.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } droppedFiles)
        {
            string pastedPath = ResolvePathForTextBox(tb, droppedFiles[0]);
            ReplaceTextBoxValue(tb, pastedPath);
            e.CancelCommand();
            return;
        }

        // 2. Si el usuario usó "Copiar como ruta de acceso" de Windows (que envuelve entre comillas "C:\...")
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) &&
            e.DataObject.GetData(DataFormats.UnicodeText) is string rawText &&
            !string.IsNullOrWhiteSpace(rawText))
        {
            string trimmed = rawText.Trim();
            bool hasOuterQuotes = (trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
                                  (trimmed.StartsWith('\'') && trimmed.EndsWith('\''));
            string clean = ViewModelBase.SanitizePath(trimmed);

            if (hasOuterQuotes || File.Exists(clean) || Directory.Exists(clean))
            {
                string resolved = ResolvePathForTextBox(tb, clean);
                if (resolved != rawText)
                {
                    var newData = new DataObject();
                    newData.SetData(DataFormats.UnicodeText, resolved);
                    newData.SetData(DataFormats.Text, resolved);
                    e.DataObject = newData;
                }
            }
        }
    }

    private static string ResolvePathForTextBox(TextBox tb, string rawPath)
    {
        string clean = ViewModelBase.SanitizePath(rawPath);
        string? boundProp = tb.GetBindingExpression(TextBox.TextProperty)?.ParentBinding?.Path?.Path;

        bool isFolderBox = boundProp is "OutputFolder" or "BatchOutputFolder" or "FolderPathA" or "FolderPathB";
        if (isFolderBox)
        {
            return ViewModelBase.SanitizeFolderPath(clean);
        }
        return clean;
    }

    private static void ReplaceTextBoxValue(TextBox tb, string value)
    {
        tb.Text = value;
        tb.CaretIndex = tb.Text.Length;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void OnMetadataItemDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: VideoMetadataItem meta } && DataContext is MainViewModel vm)
        {
            vm.LibraryTools.OpenFileLocationCommand.Execute(meta.FilePath);
        }
    }

    // ────────── DRAG & DROP GLOBAL EN TODA LA VENTANA ──────────

    private void OnWindowPreviewDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            UpdateGlobalDropOverlayText(e);
            GlobalDropOverlay.Visibility = Visibility.Visible;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            UpdateGlobalDropOverlayText(e);
            if (GlobalDropOverlay.Visibility != Visibility.Visible)
                GlobalDropOverlay.Visibility = Visibility.Visible;

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowPreviewDragLeave(object sender, DragEventArgs e)
    {
        // Ocultar el overlay cuando el cursor sale de los límites de la ventana
        Point pos = e.GetPosition(this);
        if (pos.X <= 0 || pos.Y <= 0 || pos.X >= ActualWidth || pos.Y >= ActualHeight)
        {
            GlobalDropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWindowPreviewDrop(object sender, DragEventArgs e)
    {
        GlobalDropOverlay.Visibility = Visibility.Collapsed;

        var paths = GetDroppedPaths(e);
        if (paths is null || DataContext is not MainViewModel vm) return;

        e.Handled = true;

        // Si el usuario suelta directamente encima de un TextBox de ruta específico (ej. Salida o Carpeta B), respetarlo
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is { IsReadOnly: false } targetTb)
        {
            string? boundProp = targetTb.GetBindingExpression(TextBox.TextProperty)?.ParentBinding?.Path?.Path;
            if (boundProp is "OutputFile" or "OutputMovPath" or "BatchOutputFolder" or "FolderPathB" or "OutputFolder")
            {
                ReplaceTextBoxValue(targetTb, ResolvePathForTextBox(targetTb, paths[0]));
                return;
            }
        }

        // Enrutamiento inteligente según la pestaña activa (o en toda la ventana)
        switch (vm.SelectedTabIndex)
        {
            case 0: // Convertir Videos & Lotes
                if (vm.Converter.SelectedSubTabIndex == 1)
                    vm.Converter.HandleBatchFilesDrop(paths);
                else
                    vm.Converter.HandleSingleFileDrop(paths);
                break;

            case 1: // Recuperación ProRes
                var recFile = paths.FirstOrDefault(f => File.Exists(f) && !Directory.Exists(f)) ?? paths[0];
                if (File.Exists(recFile))
                    vm.Recovery.SetSourceFile(recFile);
                break;

            case 2: // Descargar de YouTube / Web
                if (GetDroppedFolder(e) is { Length: > 0 } dlFolder)
                    vm.Downloader.OutputFolder = dlFolder;
                break;

            case 3: // Biblioteca & Herramientas
                if (GetDroppedFolder(e) is { Length: > 0 } libFolder)
                {
                    if (vm.LibraryTools.SelectedToolIndex == 4 && vm.LibraryTools.CompareTwoFolders && !string.IsNullOrWhiteSpace(vm.LibraryTools.FolderPathA) && string.IsNullOrWhiteSpace(vm.LibraryTools.FolderPathB))
                        vm.LibraryTools.FolderPathB = libFolder;
                    else
                        vm.LibraryTools.FolderPathA = libFolder;
                }
                break;
        }
    }

    private void UpdateGlobalDropOverlayText(DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var paths = GetDroppedPaths(e);
        int count = paths?.Length ?? 0;

        (GlobalDropTitle.Text, GlobalDropSubtitle.Text) = vm.SelectedTabIndex switch
        {
            0 => count > 1 || (count == 1 && Directory.Exists(paths![0]))
                ? ($"Soltá los archivos para agregar al lote ({count} seleccionados)", "Se cargarán en Procesamiento por lotes")
                : ("Soltá el video para convertir", "Se cargará automáticamente como archivo de origen"),
            1 => ("Soltá el archivo para Recuperación ProRes", "Se cargará en el escáner forense de tramas"),
            2 => ("Soltá para elegir la carpeta de descarga", "Se asignará como carpeta de destino"),
            3 => ("Soltá la carpeta o archivo para analizar la biblioteca", "Se tomará la ubicación para las herramientas de biblioteca"),
            _ => ("Soltá el archivo o carpeta aquí", "Se cargará automáticamente en la herramienta activa")
        };
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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
