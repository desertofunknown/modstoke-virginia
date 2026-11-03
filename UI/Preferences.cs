using System.Text.Json;

namespace Modstoke.Virginia;

internal sealed record VirginiaSettings
{
    public int Schema { get; init; } = 1;
    public bool AppearanceEnabled { get; init; }
    public bool DetailedMesh { get; init; }
    public bool SmoothSkinning { get; init; }
    public bool FullRateAnimation { get; init; }
    public bool TextureFiltering { get; init; }
    public bool FabricDetail { get; init; }
    public bool EnhancedDressTexture { get; init; }
    public bool EnhancedSkin { get; init; }
    public bool RefinedProportions { get; init; }
    public bool IdleAnimation { get; init; }
    public bool SecondaryMotion { get; init; }
    public float MotionStrength { get; init; } = 0.35f;
    public bool OutfitTint { get; init; }
    public string OutfitColor { get; init; } = "Original";
    public bool RomanceConsent { get; init; }
    public bool HostRomanceEnabled { get; init; }
    public bool Greetings { get; init; }
    public bool Appreciation { get; init; }
    public bool QuietCompany { get; init; }
}

internal sealed class VirginiaPreferences
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 8 };
    internal static readonly string[] OutfitColors = ["Original", "Crimson", "Forest", "Ocean", "Charcoal", "Ivory"];
    private readonly string _path;
    internal VirginiaSettings Value { get; private set; } = new();
    internal string Problem { get; private set; } = "";

    internal VirginiaPreferences(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 16384) throw new InvalidDataException();
            var settings = JsonSerializer.Deserialize<VirginiaSettings>(File.ReadAllText(path), Json);
            if (settings == null || settings.Schema != 1 || !OutfitColors.Contains(settings.OutfitColor)
                || !float.IsFinite(settings.MotionStrength) || settings.MotionStrength is < 0 or > 1)
                throw new InvalidDataException();
            Value = settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Problem = "Virginia settings could not be read. All features are off; the existing file was preserved.";
        }
    }

    internal bool TrySave(VirginiaSettings settings, out string status)
    {
        if (Problem.Length > 0) { status = Problem; return false; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            global::Modstoke.Loader.LoaderConfig.SaveText(_path, JsonSerializer.Serialize(settings, Json));
            Value = settings;
            status = "Virginia setting saved.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status = "The Virginia setting could not be saved; the previous setting remains active.";
            return false;
        }
    }
}
