using System.ComponentModel;
using AppManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppManager.Pages;

public sealed partial class SettingsPage : Page, IDetachable
{
    private bool _themeInitDone;
    private bool _suppressEvents;
    private readonly PropertyChangedEventHandler _settingsHandler;

    public SettingsPage()
    {
        InitializeComponent();
        _settingsHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.Theme))
                AppServices.MainWindow?.ApplyTheme(Settings.Theme);
        };
        Settings.PropertyChanged += _settingsHandler;
        Loaded += (_, _) =>
        {
            if (_themeInitDone)
                return;
            _themeInitDone = true;
            _suppressEvents = true;
            ThemeBox.SelectedIndex = Settings.Theme switch
            {
                ThemeSetting.Light => 1,
                ThemeSetting.Dark => 2,
                _ => 0,
            };
            LanguageBox.SelectedIndex = Settings.Language switch
            {
                "zh-Hans" => 1,
                "zh-Hant" => 2,
                "ja-JP" => 3,
                "en-US" => 4,
                _ => 0,
            };
            _suppressEvents = false;
        };

        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            AboutVersion.Text = AppServices.Loc.Format("About_Version",
                $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
        }
        catch
        {
            AboutVersion.Text = AppServices.Loc.Format("About_Version",
                GetType().Assembly.GetName().Version?.ToString() ?? "?");
        }
    }

    public void Detach() => Settings.PropertyChanged -= _settingsHandler;

    internal Services.SettingsService Settings => AppServices.Settings;

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || ThemeBox.SelectedIndex < 0)
            return;
        Settings.Theme = ThemeBox.SelectedIndex switch
        {
            1 => ThemeSetting.Light,
            2 => ThemeSetting.Dark,
            _ => ThemeSetting.System,
        };
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || LanguageBox.SelectedIndex < 0)
            return;
        Settings.Language = LanguageBox.SelectedIndex switch
        {
            1 => "zh-Hans",
            2 => "zh-Hant",
            3 => "ja-JP",
            4 => "en-US",
            _ => "system",
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
