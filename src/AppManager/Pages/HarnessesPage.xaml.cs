using AppManager.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AppManager.Pages;

public sealed partial class HarnessesPage : Page, IDetachable
{
    public HarnessesPage()
    {
        InitializeComponent();
        View.Configure(AppServices.Harness, "Harness_Title", "Harness_Subtitle",
            "Harness_Empty", "Harness_Scanning");
    }

    public void Detach() => View.Detach();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        View.Activate(e.Parameter);
    }
}
