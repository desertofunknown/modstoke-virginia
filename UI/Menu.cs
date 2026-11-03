using Modstoke.Loader;

namespace Modstoke.Virginia;

internal sealed class VirginiaMenu(VirginiaRuntime runtime, VirginiaPreferences preferences)
    : IMenuContentProvider, ICommandProvider, IConfigProvider
{
    internal const string MenuId = "modstoke.virginia";
    internal const string SettingsId = MenuId + ".settings";
    private bool _active = true;
    public string Id => MenuId;
    string IConfigProvider.Id => SettingsId;
    public string DisplayName => "Virginia";
    string IConfigProvider.DisplayName => "Virginia";
    public string Kind => MenuProviderKinds.Status;
    public int Priority => 450;
    public string Source => "Modstoke Virginia";
    public string Description => "Appearance and companionship";
    public bool RequiresAuthorization => false;
    public bool Enabled => _active;

    public IReadOnlyList<CommandSnapshot> GetCommands()
    {
        return [
            new("greet", "Greet Virginia", "", "", _active && runtime.CanRequest(RomanceAction.Greeting), false),
            new("appreciate", "Show appreciation", "", "", _active && runtime.CanRequest(RomanceAction.Appreciation), false),
            new("company", "Spend quiet time together", "", "", _active && runtime.CanRequest(RomanceAction.QuietCompany), false),
            new("step-back", "Step back from this relationship", "", "", _active && runtime.CanRequest(RomanceAction.StepBack), true)
        ];
    }

    public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
    {
        RomanceAction? action = commandId switch
        {
            "greet" => RomanceAction.Greeting,
            "appreciate" => RomanceAction.Appreciation,
            "company" => RomanceAction.QuietCompany,
            "step-back" => RomanceAction.StepBack,
            _ => null
        };
        if (!_active || !action.HasValue)
        {
            result = CommandResultSnapshot.Failure("This Virginia action is unavailable.");
            return false;
        }
        var accepted = runtime.TryAct(action.Value, out var status);
        result = accepted ? CommandResultSnapshot.Success(status)
            : CommandResultSnapshot.Failure(status);
        return accepted;
    }

    public MenuContentSnapshot CreateContent()
    {
        var view = runtime.View;
        var rows = new List<MenuRowSnapshot>
        {
            Row("Appearance", runtime.AppearanceStatus),
            Row("Relationship", preferences.Value.RomanceConsent ? view.Stage : "Off"),
            Row("Host permission", view.HostEnabled ? "Enabled" : "Off"),
            Row("Bond", view.Bond.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Row("Status", runtime.Feedback.Length > 0 ? runtime.Feedback : view.Feedback)
        };
        if (view.QuietSeconds > 0) rows.Add(Row("Quiet time", view.QuietSeconds + " / 30 s"));
        if (preferences.Problem.Length > 0) rows.Add(Row("Settings", preferences.Problem));
        return new(DisplayName, "Companion extension", [new("Virginia", rows)]);
    }

    public IReadOnlyList<ConfigValueSnapshot> GetSettings()
    {
        var s = preferences.Value;
        var editable = _active && preferences.Problem.Length == 0;
        var host = editable && runtime.CanConfigureHost;
        return [
            Toggle("Local appearance", nameof(s.AppearanceEnabled), "Enable appearance customization", s.AppearanceEnabled, editable),
            Toggle("Local appearance", nameof(s.DetailedMesh), "Extended mesh detail", s.DetailedMesh, editable),
            Toggle("Local appearance", nameof(s.SmoothSkinning), "Four-bone skinning", s.SmoothSkinning, editable),
            Toggle("Local appearance", nameof(s.FullRateAnimation), "Unculled animation", s.FullRateAnimation, editable),
            Toggle("Local appearance", nameof(s.TextureFiltering), "Enhanced texture filtering", s.TextureFiltering, editable),
            Toggle("Local appearance", nameof(s.FabricDetail), "Camouflage fabric detail", s.FabricDetail, editable),
            Toggle("Local appearance", nameof(s.EnhancedDressTexture), "4K blue floral dress", s.EnhancedDressTexture, editable),
            Toggle("Local appearance", nameof(s.EnhancedSkin), "4K skin detail", s.EnhancedSkin, editable),
            Toggle("Local appearance", nameof(s.RefinedProportions), "Refined clothed silhouette", s.RefinedProportions, editable),
            Toggle("Local appearance", nameof(s.IdleAnimation), "Breathing and relaxed idle motion", s.IdleAnimation, editable),
            Toggle("Local appearance", nameof(s.SecondaryMotion), "Subtle chest and hip motion", s.SecondaryMotion, editable),
            new("Local appearance", nameof(s.MotionStrength), "Motion strength", ConfigValueKinds.Choice,
                s.MotionStrength.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), "0.35", "",
                editable, false, options: ["0.15", "0.35", "0.5", "0.75", "1"]),
            Toggle("Local appearance", nameof(s.OutfitTint), "Outfit color", s.OutfitTint, editable),
            new("Local appearance", nameof(s.OutfitColor), "Outfit palette", ConfigValueKinds.Choice,
                s.OutfitColor, "Original", "", editable, false, options: VirginiaPreferences.OutfitColors),
            Toggle("Personal relationship", nameof(s.RomanceConsent), "Participate in romance", s.RomanceConsent, editable),
            Toggle("Host gameplay", nameof(s.HostRomanceEnabled), "Enable romance gameplay", s.HostRomanceEnabled, host),
            Toggle("Host gameplay", nameof(s.Greetings), "Greetings", s.Greetings, host),
            Toggle("Host gameplay", nameof(s.Appreciation), "Appreciation", s.Appreciation, host),
            Toggle("Host gameplay", nameof(s.QuietCompany), "Quiet companionship", s.QuietCompany, host)
        ];
    }

    public bool TrySetValue(string key, string value, out string status)
    {
        if (!_active || !runtime.OnThread)
        { status = "Virginia settings are unavailable on this thread."; return false; }
        var s = preferences.Value;
        var hostSetting = key is nameof(s.HostRomanceEnabled) or nameof(s.Greetings)
            or nameof(s.Appreciation) or nameof(s.QuietCompany);
        if (hostSetting && !runtime.CanConfigureHost)
        { status = "Only the game host can change Virginia gameplay settings."; return false; }
        VirginiaSettings? next;
        if (key == nameof(s.MotionStrength))
        {
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var strength)
                || !float.IsFinite(strength) || strength is < 0 or > 1)
            { status = "Motion strength must be between 0 and 1."; return false; }
            next = s with { MotionStrength = strength };
        }
        else if (key == nameof(s.OutfitColor))
        {
            if (!VirginiaPreferences.OutfitColors.Contains(value))
            { status = "Choose an available outfit palette."; return false; }
            next = s with { OutfitColor = value };
        }
        else
        {
            if (!bool.TryParse(value, out var enabled))
            { status = "An on/off value is required."; return false; }
            next = key switch
            {
                nameof(s.AppearanceEnabled) => s with { AppearanceEnabled = enabled },
                nameof(s.DetailedMesh) => s with { DetailedMesh = enabled },
                nameof(s.SmoothSkinning) => s with { SmoothSkinning = enabled },
                nameof(s.FullRateAnimation) => s with { FullRateAnimation = enabled },
                nameof(s.TextureFiltering) => s with { TextureFiltering = enabled },
                nameof(s.FabricDetail) => s with { FabricDetail = enabled },
                nameof(s.EnhancedDressTexture) => s with { EnhancedDressTexture = enabled },
                nameof(s.EnhancedSkin) => s with { EnhancedSkin = enabled },
                nameof(s.RefinedProportions) => s with { RefinedProportions = enabled },
                nameof(s.IdleAnimation) => s with { IdleAnimation = enabled },
                nameof(s.SecondaryMotion) => s with { SecondaryMotion = enabled },
                nameof(s.OutfitTint) => s with { OutfitTint = enabled },
                nameof(s.RomanceConsent) => s with { RomanceConsent = enabled },
                nameof(s.HostRomanceEnabled) => s with { HostRomanceEnabled = enabled },
                nameof(s.Greetings) => s with { Greetings = enabled },
                nameof(s.Appreciation) => s with { Appreciation = enabled },
                nameof(s.QuietCompany) => s with { QuietCompany = enabled },
                _ => null
            };
            if (next is null)
            { status = "Unknown Virginia setting."; return false; }
        }
        if (!preferences.TrySave(next, out status)) return false;
        runtime.SettingsChanged();
        return true;
    }

    internal void Deactivate() => _active = false;
    private static MenuRowSnapshot Row(string name, string value) => new(name, value, "", "");
    private static ConfigValueSnapshot Toggle(string group, string key, string name, bool value, bool editable)
        => new(group, key, name, ConfigValueKinds.Boolean, value.ToString(), "False", "", editable, false);
}
