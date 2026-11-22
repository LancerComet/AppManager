using AppManager.Core.Models;
using AppManager.Core.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppManager.Services;

/// <summary>
/// Holds the current scan state: the ScanResult, progress while scanning,
/// and the persisted last scan.
/// </summary>
public partial class ScanSession : ObservableObject
{
    private readonly DiskScanner _scanner = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    public partial ScanResult? Result { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string PhaseText { get; set; }

    [ObservableProperty]
    public partial int FoldersDone { get; set; }

    [ObservableProperty]
    public partial int FoldersTotal { get; set; }

    [ObservableProperty]
    public partial long BytesSoFar { get; set; }

    [ObservableProperty]
    public partial string? CurrentPath { get; set; }

    [ObservableProperty]
    public partial string? ScanError { get; set; }

    /// <summary>Local time when the current result's scan started/finished.</summary>
    [ObservableProperty]
    public partial DateTime? LastScanTime { get; set; }

    public bool HasResult => Result is not null;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsScanning)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ScanError = null;
        FoldersDone = 0;
        FoldersTotal = 0;
        BytesSoFar = 0;
        CurrentPath = null;
        PhaseText = "正在枚举已安装应用";

        var progress = new Progress<ScanProgress>(p =>
        {
            PhaseText = p.Phase switch
            {
                ScanPhase.EnumeratingApps => "正在枚举已安装应用",
                ScanPhase.DiscoveringFolders => "正在发现文件夹",
                ScanPhase.Measuring => "正在统计大小",
                ScanPhase.Matching => "正在匹配",
                ScanPhase.Done => "完成",
                _ => string.Empty,
            };
            FoldersDone = p.FoldersDone;
            FoldersTotal = p.FoldersTotal;
            BytesSoFar = p.BytesSoFar;
            CurrentPath = p.CurrentPath;
        });

        var options = new ScanOptions
        {
            ExtraRoots = AppServices.Settings.ExtraRoots.ToList(),
        };

        try
        {
            var result = await _scanner.ScanAsync(options, progress, _cts.Token);
            Result = result;
            OnPropertyChanged(nameof(HasResult));
            LastScanTime = result.StartedAtUtc.ToLocalTime();
            await ScanResultStore.SaveAsync(result, AppServices.Settings.LastScanPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ScanError = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Loads the persisted last scan (called once at startup).</summary>
    public async Task LoadLastScanAsync()
    {
        var result = await ScanResultStore.LoadAsync(AppServices.Settings.LastScanPath);
        if (result is not null)
        {
            Result = result;
            OnPropertyChanged(nameof(HasResult));
            LastScanTime = result.StartedAtUtc.ToLocalTime();
        }
    }
}
