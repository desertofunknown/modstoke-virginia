using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Virginia;

internal sealed partial class VirginiaAppearance
{
    private const string GarmentShader = "Sons/Characters/Lit BloodReveal DIrt Wetness";
    private const string BottomShader = "Sons/HDRPLit";
    private static readonly string[] FilterMaps = ["_BaseColorMap", "_NormalMap", "_MaskMap"];
    private readonly Dictionary<IntPtr, GarmentState> _materials = new();
    private readonly List<GarmentState> _retired = new();

    private static string AuthoredName(string name) =>
        name.EndsWith(" (Instance)", StringComparison.Ordinal) ? name[..^11] : name;

    private bool TryGarment(SkinnedMeshRenderer renderer, out Material original, out bool camouflage)
    {
        original = null!;
        camouflage = false;
        var mesh = renderer.sharedMesh;
        if (!Live(mesh) || mesh.subMeshCount != 1) return false;
        var slots = renderer.sharedMaterials;
        if (slots == null || slots.Length != 1 || !Live(slots[0])) return false;
        var material = slots[0];
        if (_materials.TryGetValue(renderer.Pointer, out var state) && Same(material, state.Clone))
            material = state.Original;
        if (!Live(material) || !Live(material.shader)) return false;
        var meshName = AuthoredName(mesh.name);
        var materialName = AuthoredName(material.name);
        var expected = meshName switch
        {
            "C1:dress" => materialName is "VirginiaDressBlue" or "VirginiaDressGreen" or "VirginiaDressRed",
            "C1:camosuit" or "C1:camosuitShoes" => materialName == "VirginiaCamouflageSuitMaterial",
            "C1:LeatherSuit" or "C1:LeathersuitShoes" => materialName == "VirginiaLeatherSuitMaterial",
            "C1:tracksuitShirt" => materialName == "VirginiaTrackSuitTopMaterial",
            "C1:tracksuitPants" => materialName == "VirginiaTrackSuitBottomMaterial",
            _ => false
        };
        if (!expected || material.shader.name != (meshName == "C1:tracksuitPants" ? BottomShader : GarmentShader))
            return false;
        original = material;
        camouflage = meshName is "C1:camosuit" or "C1:camosuitShoes";
        return true;
    }

    private void UpdateMaterials(Dictionary<IntPtr, SkinnedMeshRenderer> garments, VirginiaSettings settings)
    {
        var wanted = settings.TextureFiltering || settings.OutfitTint && settings.OutfitColor != "Original"
            || settings.FabricDetail || settings.EnhancedDressTexture;
        foreach (var pair in _materials.ToArray())
        {
            var state = pair.Value;
            Try(() =>
            {
                if (wanted && garments.ContainsKey(pair.Key) && state.Bound()
                    && Same(state.Mesh, state.Renderer.sharedMesh)) return;
                RetireMaterial(pair.Key, state);
            });
        }
        SweepRetired();
        if (!wanted) return;
        foreach (var pair in garments)
        {
            Try(() =>
            {
                if (!_materials.TryGetValue(pair.Key, out var state))
                {
                    // A clone adopted by another writer remains theirs until its slot changes.
                    if (_retired.Any(s => Same(s.Renderer, pair.Value) && s.Bound())) return;
                    if (!TryGarment(pair.Value, out var original, out var camouflage)) return;
                    if (!settings.TextureFiltering && !(settings.OutfitTint && TryTint(settings.OutfitColor, out _))
                        && !(settings.FabricDetail && HasFabric(original, camouflage))
                        && !(settings.EnhancedDressTexture && IsBlueDress(pair.Value, original))) return;
                    state = new(pair.Value, original, camouflage);
                    _materials.Add(pair.Key, state);
                    state.Install();
                }
                state.Update(settings);
            });
        }
    }

    private static bool HasFabric(Material material, bool camouflage) =>
        camouflage && Live(material) && Live(material.shader) && material.shader.name == GarmentShader
        && material.HasProperty("_EnableDetailMap") && material.HasProperty("_DetailMap")
        && Live(material.GetTexture("_DetailMap"));

    private static bool IsBlueDress(SkinnedMeshRenderer renderer, Material material) =>
        Live(renderer.sharedMesh) && AuthoredName(renderer.sharedMesh.name) == "C1:dress"
        && AuthoredName(material.name) == "VirginiaDressBlue";

    private static bool TryTint(string name, out Color tint)
    {
        // Multipliers, not replacement albedo: authored texture and color variation remain.
        tint = name switch
        {
            "Crimson" => new Color(0.80f, 0.24f, 0.28f, 1f),
            "Forest" => new Color(0.30f, 0.60f, 0.34f, 1f),
            "Ocean" => new Color(0.25f, 0.55f, 0.85f, 1f),
            "Charcoal" => new Color(0.28f, 0.30f, 0.32f, 1f),
            "Ivory" => new Color(1f, 0.96f, 0.84f, 1f),
            _ => Color.white
        };
        return name is "Crimson" or "Forest" or "Ocean" or "Charcoal" or "Ivory";
    }

    private string MaterialStatus(VirginiaSettings settings)
    {
        var tinted = 0;
        var filtered = 0;
        var unavailable = 0;
        var detailed = 0;
        var retextured = 0;
        var dressUnavailable = 0;
        var yielded = false;
        foreach (var state in _materials.Values)
        {
            if (state.Yielded)
            {
                yielded = true;
                continue;
            }
            if (settings.OutfitTint && settings.OutfitColor != "Original" && state.Tinted) tinted++;
            if (settings.TextureFiltering)
            {
                var liveTextures = state.FilteredCount;
                filtered = checked(filtered + liveTextures);
                unavailable = checked(unavailable + (FilterMaps.Length - liveTextures));
            }
            if (settings.FabricDetail && state.Detailed) detailed++;
            if (settings.EnhancedDressTexture)
            {
                if (state.Retextured) retextured++;
                if (state.DressUnavailable) dressUnavailable++;
            }
        }
        var text = "";
        if (settings.OutfitTint)
            text += settings.OutfitColor == "Original" ? "Authored outfit color. "
                : $"RGB multiply tint: {tinted} garments; alpha unchanged. ";
        if (settings.TextureFiltering)
            text += $"Filtered texture clones: {filtered}; unavailable: {unavailable}. ";
        if (settings.FabricDetail)
            text += $"Authored camouflage detail enabled: {detailed} garments. ";
        if (settings.EnhancedDressTexture)
            text += $"4K blue floral dress: {retextured}; unavailable: {dressUnavailable}. ";
        if (yielded || _retired.Any(s => s.Bound()))
            text += "External material changes retained. ";
        return text.Length == 0 ? "Authored materials unchanged." : text.TrimEnd();
    }

    private void RestoreMaterials()
    {
        foreach (var pair in _materials.ToArray())
            Try(() => RetireMaterial(pair.Key, pair.Value));
        SweepRetired();
    }

    private void RetireMaterial(IntPtr rendererPointer, GarmentState state)
    {
        state.Release();
        _materials.Remove(rendererPointer);
        _retired.Add(state);
    }

    private void SweepRetired()
    {
        foreach (var state in _retired.ToArray())
            Try(() => { if (state.Cleanup()) _retired.Remove(state); });
    }

    private sealed class GarmentState
    {
        internal readonly SkinnedMeshRenderer Renderer;
        internal readonly Mesh Mesh;
        internal readonly Material Original;
        internal readonly Material Clone;
        private readonly Shader _shader;
        private readonly bool _camouflage;
        private readonly Dictionary<string, FilteredTexture> _textures = new();
        private Texture2D? _dress;
        private bool _dressAttempted;
        private Color _baseColor, _tint;
        private float _detail;
        private bool _keepBound;
        internal bool Yielded { get; private set; }
        internal bool Tinted { get; private set; }
        internal bool Detailed { get; private set; }
        internal bool Retextured { get; private set; }
        internal bool DressUnavailable => _dressAttempted && !Live(_dress);
        internal int FilteredCount
        {
            get
            {
                var count = 0;
                foreach (var texture in _textures.Values)
                    if (Live(texture.Clone)) count++;
                return count;
            }
        }

        internal GarmentState(SkinnedMeshRenderer renderer, Material original, bool camouflage)
        {
            Renderer = renderer;
            Mesh = renderer.sharedMesh;
            Original = original;
            _shader = original.shader;
            _camouflage = camouflage;
            Clone = new Material(original);
        }

        internal bool Bound()
        {
            if (!Live(Renderer) || !Live(Clone)) return false;
            var slots = Renderer.sharedMaterials;
            return slots != null && slots.Length > 0 && Same(slots[0], Clone);
        }

        internal void Install()
        {
            var slots = Renderer.sharedMaterials;
            if (slots.Length != 1 || !Same(slots[0], Original)) return;
            slots[0] = Clone;
            Renderer.sharedMaterials = slots;
        }

        private bool OverridesIntact()
        {
            if (!Live(Clone) || !Live(Original) || !Same(Clone.shader, _shader)) return false;
            if (Tinted && !Clone.GetColor("_BaseColor").Equals(_tint)) return false;
            if (Detailed && !Clone.GetFloat("_EnableDetailMap").Equals(1f)) return false;
            if (Retextured && !Same(Clone.GetTexture("_BaseColorMap"), _dress)) return false;
            foreach (var pair in _textures)
                if (Live(pair.Value.Clone) && !Same(Clone.GetTexture(pair.Key), pair.Value.Clone)) return false;
            return true;
        }

        internal void Update(VirginiaSettings settings)
        {
            if (Yielded || !Bound()) return;
            if (!OverridesIntact())
            {
                _keepBound = true;
                Yielded = true;
                ReleaseOverrides();
                return;
            }
            // Native wardrobe controllers retain their original material references.
            // Refresh those live values before applying only our selected overrides.
            Clone.CopyPropertiesFromMaterial(Original);
            Tinted = Detailed = Retextured = false;
            if (settings.OutfitTint && TryTint(settings.OutfitColor, out var multiplier)
                && Clone.HasProperty("_BaseColor"))
            {
                _baseColor = Clone.GetColor("_BaseColor");
                _tint = new Color(_baseColor.r * multiplier.r, _baseColor.g * multiplier.g,
                    _baseColor.b * multiplier.b, _baseColor.a);
                Tinted = true;
                Clone.SetColor("_BaseColor", _tint);
            }
            if (settings.FabricDetail && HasFabric(Original, _camouflage))
            {
                _detail = Clone.GetFloat("_EnableDetailMap");
                Detailed = true;
                Clone.SetFloat("_EnableDetailMap", 1f);
            }
            foreach (var property in FilterMaps)
                UpdateTexture(property, settings.TextureFiltering
                    && !(property == "_BaseColorMap" && settings.EnhancedDressTexture && IsBlueDress(Renderer, Original)));
            UpdateDress(settings.EnhancedDressTexture && IsBlueDress(Renderer, Original));
        }

        private void UpdateDress(bool enabled)
        {
            if (!enabled)
            {
                if (Live(_dress)) UnityObject.Destroy(_dress);
                _dress = null;
                _dressAttempted = false;
                return;
            }
            if (!_dressAttempted)
            {
                _dressAttempted = true;
                try
                {
                    var bytes = VirginiaModelAssets.Read("virginia-dress-blue.png", VirginiaModelAssets.DressHash);
                    _dress = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
                    if (!ImageConversion.LoadImage(_dress, bytes, true) || _dress.width != 4096 || _dress.height != 4096)
                        throw new InvalidDataException("The paired dress texture could not be decoded.");
                    _dress.name = "VirginiaDressBlue.Modstoke4K";
                    _dress.filterMode = FilterMode.Trilinear;
                    _dress.anisoLevel = 8;
                    var source = Original.GetTexture("_BaseColorMap");
                    if (Live(source))
                    {
                        _dress.wrapModeU = source.wrapModeU;
                        _dress.wrapModeV = source.wrapModeV;
                    }
                }
                catch (Exception error)
                {
                    if (Live(_dress)) UnityObject.Destroy(_dress);
                    _dress = null;
                    Console.WriteLine("Modstoke Virginia dress texture: " + error.Message);
                }
            }
            if (Live(_dress))
            {
                Clone.SetTexture("_BaseColorMap", _dress);
                Retextured = true;
            }
        }

        private void UpdateTexture(string property, bool enabled)
        {
            var source = Original.HasProperty(property) ? Original.GetTexture(property) : null;
            if (_textures.TryGetValue(property, out var previous)
                && (!enabled || !Same(previous.Source, source)))
            {
                previous.Destroy();
                _textures.Remove(property);
            }
            if (!enabled || !Live(source)) return;
            if (!_textures.TryGetValue(property, out var texture))
            {
                texture = new(source!);
                _textures.Add(property, texture);
                texture.Create();
            }
            if (Live(texture.Clone)) Clone.SetTexture(property, texture.Clone);
        }

        private void ReleaseOverrides()
        {
            if (!Live(Clone)) return;
            if (Tinted && Clone.HasProperty("_BaseColor") && Clone.GetColor("_BaseColor").Equals(_tint))
                Clone.SetColor("_BaseColor", Live(Original) && Original.HasProperty("_BaseColor")
                    ? Original.GetColor("_BaseColor") : _baseColor);
            if (Detailed && Clone.HasProperty("_EnableDetailMap") && Clone.GetFloat("_EnableDetailMap").Equals(1f))
                Clone.SetFloat("_EnableDetailMap", Live(Original) && Original.HasProperty("_EnableDetailMap")
                    ? Original.GetFloat("_EnableDetailMap") : _detail);
            foreach (var pair in _textures)
            {
                if (Clone.HasProperty(pair.Key) && Same(Clone.GetTexture(pair.Key), pair.Value.Clone))
                    Clone.SetTexture(pair.Key, Live(Original) && Original.HasProperty(pair.Key)
                        ? Original.GetTexture(pair.Key) : pair.Value.Source);
            }
            if (Retextured && Same(Clone.GetTexture("_BaseColorMap"), _dress))
                Clone.SetTexture("_BaseColorMap", Live(Original) ? Original.GetTexture("_BaseColorMap") : null);
            Tinted = Detailed = Retextured = false;
        }

        internal void Release()
        {
            if (!Bound()) return;
            _keepBound |= !OverridesIntact();
            if (!_keepBound && Live(Original))
            {
                var slots = Renderer.sharedMaterials;
                if (Same(slots[0], Clone))
                {
                    slots[0] = Original;
                    Renderer.sharedMaterials = slots;
                }
            }
            else
            {
                _keepBound = true;
                ReleaseOverrides();
            }
        }

        internal bool Cleanup()
        {
            // An external writer may have moved our clone to another slot on this renderer.
            if (Live(Renderer))
            {
                var slots = Renderer.sharedMaterials;
                if (slots != null && slots.Any(material => Same(material, Clone))) return false;
            }
            foreach (var texture in _textures.Values) texture.Destroy();
            _textures.Clear();
            if (Live(_dress)) UnityObject.Destroy(_dress);
            _dress = null;
            if (Live(Clone)) UnityObject.Destroy(Clone);
            return true;
        }
    }

    private sealed class FilteredTexture(Texture source)
    {
        internal readonly Texture Source = source;
        internal Texture? Clone { get; private set; }

        internal void Create()
        {
            // Instantiate copies CPU pixels. The game's maps are upload-only, so a copy logs
            // "No texture data available" and binds an empty texture; keep the original instead.
            if (!Live(Source) || !Source.isReadable) return;
            try
            {
                var clone = UnityObject.Instantiate<Texture>(Source);
                Clone = clone;
                if (!Live(clone) || Same(clone, Source))
                {
                    Clone = null;
                    return;
                }
                if (clone.width != Source.width || clone.height != Source.height
                    || clone.dimension != Source.dimension || clone.mipmapCount != Source.mipmapCount)
                {
                    Destroy();
                    return;
                }
                clone.filterMode = FilterMode.Trilinear;
                clone.anisoLevel = 8;
            }
            catch (Exception)
            {
                Destroy();
            }
        }

        internal void Destroy()
        {
            if (Live(Clone) && !Same(Clone, Source)) UnityObject.Destroy(Clone);
            Clone = null;
        }
    }
}
