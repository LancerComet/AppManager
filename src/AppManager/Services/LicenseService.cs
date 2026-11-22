using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Windows.Services.Store;

namespace AppManager.Services;

public enum LicenseState
{
    Unknown,
    Free,
    Pro,
    StoreUnavailable,
}

public interface ILicenseService
{
    bool IsPro { get; }
    LicenseState State { get; }
    Task RefreshAsync();
    Task<bool> PurchaseProAsync();
    event EventHandler? LicenseChanged;
}

public static class StoreConfig
{
    /// <summary>Partner Center add-on Store ID — replace before shipping.</summary>
    public const string ProAddOnStoreId = "9NXXXXXXXXXX";
}

public partial class StoreLicenseService : ObservableObject, ILicenseService
{
    private StoreContext? _context;
    private DispatcherQueue? _dispatcher;
    private bool _initialized;
    private string? _lastPurchaseStatus;

    [ObservableProperty]
    public partial LicenseState State { get; set; } = LicenseState.Unknown;

    [ObservableProperty]
    public partial bool StorePro { get; set; }

    public event EventHandler? LicenseChanged;

    public bool IsPro => StorePro;

    public string? LastPurchaseStatus
    {
        get => _lastPurchaseStatus;
        private set => SetProperty(ref _lastPurchaseStatus, value);
    }

    /// <summary>Call once the main window exists so purchase dialogs can be parented.</summary>
    public void Initialize(Microsoft.UI.Xaml.Window window)
    {
        if (_initialized)
            return;
        _initialized = true;
        _dispatcher = window.DispatcherQueue;

        try
        {
            _ = Windows.ApplicationModel.Package.Current;
        }
        catch
        {
            State = LicenseState.StoreUnavailable;
            return;
        }

        try
        {
            _context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(
                _context, WinRT.Interop.WindowNative.GetWindowHandle(window));
            _context.OfflineLicensesChanged += OnLicensesChanged;
            _ = RefreshAsync();
        }
        catch
        {
            _context = null;
            State = LicenseState.StoreUnavailable;
        }
    }

    public async Task RefreshAsync()
    {
        if (_context is null)
        {
            SetState(_initialized ? LicenseState.StoreUnavailable : LicenseState.Unknown);
            return;
        }

        try
        {
            var license = await _context.GetAppLicenseAsync();
            var pro = license.AddOnLicenses.Values.Any(l =>
                l.IsActive &&
                l.SkuStoreId.StartsWith(StoreConfig.ProAddOnStoreId, StringComparison.OrdinalIgnoreCase));
            StorePro = pro;
            SetState(pro ? LicenseState.Pro : LicenseState.Free);
        }
        catch
        {
            SetState(LicenseState.StoreUnavailable);
        }
    }

    public async Task<bool> PurchaseProAsync()
    {
        if (_context is null)
        {
            State = LicenseState.StoreUnavailable;
            LastPurchaseStatus = "StoreUnavailable";
            return false;
        }

        try
        {
            var result = await _context.RequestPurchaseAsync(StoreConfig.ProAddOnStoreId);
            LastPurchaseStatus = result.Status.ToString();
            if (result.Status is StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased)
            {
                await RefreshAsync();
                return IsPro;
            }
            return false;
        }
        catch
        {
            LastPurchaseStatus = "Error";
            State = LicenseState.StoreUnavailable;
            return false;
        }
    }

    private void OnLicensesChanged(StoreContext sender, object args)
    {
        // Store raises this on a background thread — marshal to the UI
        // dispatcher so RefreshAsync's property changes happen there.
        if (_dispatcher is not null)
            _dispatcher.TryEnqueue(async () => await RefreshAsync());
        else
            _ = RefreshAsync();
    }

    private void SetState(LicenseState state)
    {
        if (State == state)
            return;
        State = state;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(IsPro));
        var handler = LicenseChanged;
        if (handler is null)
            return;
        if (_dispatcher is not null)
            _dispatcher.TryEnqueue(() => handler(this, EventArgs.Empty));
        else
            handler(this, EventArgs.Empty);
    }
}
