using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Virginia;

public static class Plugin
{
    private static VirginiaRuntime? _runtime;
    private static VirginiaMenu? _menu;
    private static bool _settingsRegistered;

    public static void Initialize()
    {
        if (_runtime != null) return;
        if (LoaderConfig.TryGetProviderSnapshot(VirginiaMenu.SettingsId, out _))
            throw new InvalidOperationException("Virginia settings are already registered.");
        var preferences = new VirginiaPreferences(Path.Combine(GameRuntime.Environment.GameRootDirectory,
            "BepInEx", "config", "modstoke.virginia.json"));
        try
        {
            _runtime = new VirginiaRuntime(preferences);
            var menu = new VirginiaMenu(_runtime, preferences);
            if (!MenuRegistry.TryRegisterProvider(menu))
                throw new InvalidOperationException("Virginia menu is already registered.");
            _menu = menu;
            _settingsRegistered = true;
            if (!LoaderConfig.RegisterOrUpdateProvider(menu))
                throw new InvalidOperationException("Virginia settings could not be registered.");
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    public static void Update() => _runtime?.Update();
    public static void LateUpdate() => _runtime?.LateUpdate();

    public static void Shutdown()
    {
        var runtime = _runtime;
        _runtime = null;
        var menu = _menu;
        _menu = null;
        menu?.Deactivate();
        try { runtime?.Dispose(); }
        finally
        {
            try
            {
                if (_settingsRegistered)
                {
                    _settingsRegistered = false;
                    LoaderConfig.TryUnregisterProvider(VirginiaMenu.SettingsId);
                }
            }
            finally
            {
                if (menu != null) MenuRegistry.TryUnregisterProvider(VirginiaMenu.MenuId);
            }
        }
    }
}
