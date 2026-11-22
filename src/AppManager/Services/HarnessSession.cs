using AppManager.Core.Harnesses;
using AppManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppManager.Services;

/// <summary>
/// Holds the harness (AI tool) scan state: the HarnessScanResult and whether a
/// scan is running. Scan starts lazily the first time the page is shown.
/// </summary>
public partial class HarnessSession : ObservableObject
{
    private bool _scanned;

    [ObservableProperty]
    public partial HarnessScanResult? Result { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsScanning)
            return;
        IsScanning = true;
        Error = null;
        try
        {
            var apps = AppServices.Session.Result?.InstalledApps;
            Result = await new HarnessScanner().ScanAsync(apps);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Kicks off the first scan; subsequent calls are no-ops.</summary>
    public void EnsureScanned()
    {
        if (_scanned || IsScanning)
            return;
        _scanned = true;
        _ = StartCommand.ExecuteAsync(null);
    }
}
