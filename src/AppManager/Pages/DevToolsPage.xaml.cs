using AppManager.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AppManager.Pages;

public sealed partial class DevToolsPage : Page, IDetachable
{
    public DevToolsPage()
    {
        InitializeComponent();
        View.MinEntryBytes = 1024; // hide sub-KB noise entries (locks, marker files)
        View.Configure(AppServices.DevTools, "DevTools_Title", "DevTools_Subtitle",
            "DevTools_Empty", "DevTools_Scanning");
    }

    public void Detach() => View.Detach();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        View.Activate(e.Parameter);
    }
}
