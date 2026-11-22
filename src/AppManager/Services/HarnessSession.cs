using AppManager.Core.Harnesses;
using AppManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppManager.Services;

/// <summary>
/// Holds the scan state of a tool-usage page (AI tools or developer tools):
/// the HarnessScanResult and whether a scan is running. Scan starts lazily the
/// first time the page is shown. The scanner factory selects the catalog.
/// </summary>
public partial class HarnessSession : ObservableObject
{
    private readonly Func<HarnessScanner> _scannerFactory;
    private bool _scanned;

    public HarnessSession(Func<HarnessScanner>? scannerFactory = null)
        => _scannerFactory = scannerFactory ?? (() => new HarnessScanner());

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
            Result = await _scannerFactory().ScanAsync(apps);
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
