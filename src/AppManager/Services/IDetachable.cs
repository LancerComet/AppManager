namespace AppManager.Services;

/// <summary>
/// Pages cached with NavigationCacheMode=Required subscribe to singleton
/// events. When the shell is rebuilt (language switch) the old pages are
/// detached so they stop receiving events and can be collected.
/// </summary>
public interface IDetachable
{
    void Detach();
}
