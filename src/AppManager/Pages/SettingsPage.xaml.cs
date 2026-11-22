using AppManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppManager.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _themeInitDone;

    public SettingsPage()
    {
        InitializeComponent();
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.Theme))
                AppServices.MainWindow?.ApplyTheme(Settings.Theme);
        };
        Loaded += (_, _) =>
        {
            if (_themeInitDone)
                return;
            _themeInitDone = true;
            ThemeBox.SelectedIndex = Settings.Theme switch
            {
                ThemeSetting.Light => 1,
                ThemeSetting.Dark => 2,
                _ => 0,
            };
        };

        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            AboutVersion.Text = $"版本 {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            AboutVersion.Text = $"版本 {GetType().Assembly.GetName().Version}";
        }
    }

    internal Services.SettingsService Settings => AppServices.Settings;

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Settings.Theme = ThemeBox.SelectedIndex switch
        {
            1 => ThemeSetting.Light,
            2 => ThemeSetting.Dark,
            _ => ThemeSetting.System,
        };
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string root)
            Settings.ExtraRoots.Remove(root);
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(AppServices.MainWindow!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !Settings.ExtraRoots.Contains(folder.Path))
            Settings.ExtraRoots.Add(folder.Path);
    }
}
