using System.Windows;
using MediaRecovery.Services;

namespace MediaRecovery;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EmbeddedToolsService.EnsureToolsAvailable();
    }
}
