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

    public MainViewModel()
    {
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
    }
}
