using System.Diagnostics.CodeAnalysis;
using Sons.Ai.Vail;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Virginia;

internal sealed class VirginiaSkin : IDisposable
{
    internal const string HeadHash = "DF63A71BEE3C4905192085F6F07A7A7F89CB11866C9FC1F76CA825EED3865719";
    internal const string BodyHash = "3A26548DF88ECA3EC32193170D1A00FF31CCB67CAB713EAC6C1494A329B74DBD";
    private const string Albedo = "_BaseColorMap";
    // Disposed instances may still own objects adopted by another native writer.
    private static readonly List<VirginiaSkin> PendingCleanup = new();
    private readonly List<SkinnedMeshRenderer> _renderers = new();
    private readonly List<global::VirginiaWrinkleController> _controllers = new();
    private readonly List<Binding> _retired = new();
    private VailActor? _actor;
    private Binding? _binding;
    private Texture2D? _head, _body;
    private bool _assetsAttempted, _yielded, _disposed, _enabled;
    internal string Status { get; private set; } = "4K skin is off.";

    // Called by VirginiaRuntime's existing 500 ms appearance cadence.
    internal void Update(bool enabled, VirginiaPresentationObservation observation)
    {
        _enabled = enabled;
        SweepPending();
        if (_disposed) return;
        if (!enabled)
        {
            Release();
            _actor = null;
            _yielded = false;
            Sweep();
            if (!Live(_head) && !Live(_body)) _assetsAttempted = false;
            Status = _retired.Count == 0 ? "4K skin is off."
                : "4K skin is off; retained materials or pending cleanup remain.";
            return;
        }

        try
        {
            if (!observation.TryResolve(out var actor, out var reason) || !Live(actor))
            {
                Release();
                _actor = null;
                _yielded = false;
                Sweep();
                Status = string.IsNullOrEmpty(reason) ? "Virginia is unavailable." : reason;
                return;
            }
            if (!Same(_actor, actor))
            {
                Release();
                _actor = actor;
                _yielded = false;
            }

            var renderers = observation.Renderers;
            var controllers = actor.GetComponentsInChildren<global::VirginiaWrinkleController>(true);
            foreach (var renderer in renderers)
                if (Live(renderer) && !_renderers.Any(r => Same(r, renderer))) _renderers.Add(renderer);
            foreach (var controller in controllers)
                if (Live(controller) && !_controllers.Any(c => Same(c, controller))) _controllers.Add(controller);
            Sweep();

            if (_binding != null)
            {
                if (!_binding.Intact())
                {
                    _yielded = true;
                    Release();
                    Sweep();
                }
                Status = _yielded ? "4K skin yielded to external material changes." : "Local 4K head and body skin active.";
                return;
            }
            if (_yielded)
            {
                Status = "4K skin yielded to external material changes.";
                return;
            }

            SkinnedMeshRenderer? target = null;
            foreach (var renderer in renderers)
            {
                if (!Live(renderer) || !Live(renderer.sharedMesh)
                    || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                    || renderer.sharedMesh.name != "C1:Virginia_body") continue;
                if (target != null)
                {
                    Status = "4K skin unavailable; body renderer is ambiguous.";
                    return;
                }
                target = renderer;
            }
            if (!Live(target))
            {
                Status = "4K skin waiting for Virginia's body renderer.";
                return;
            }
            if (_retired.Any(state => state.Targets(target!)))
            {
                Status = "4K skin waiting; retained materials or pending cleanup remain.";
                return;
            }
            var slots = target!.sharedMaterials;
            if (slots == null || slots.Length < 2
                || !Original(slots[0], "VirginiaHeadNoDirt") || !Original(slots[1], "VirginiaBodyNoDirt"))
            {
                Status = "4K skin waiting for native head and body materials.";
                return;
            }
            global::VirginiaWrinkleController? writer = null;
            foreach (var controller in controllers)
            {
                if (!Live(controller) || !Same(controller.TargetRenderer, target)) continue;
                if (writer != null)
                {
                    Status = "4K skin unavailable; wrinkle controller is ambiguous.";
                    return;
                }
                writer = controller;
            }
            // A populated matching cache is the observable evidence of native Start initialization.
            // Never invoke Start ourselves or replace the native Update writer.
            if (!Live(writer) || !Live(writer!._targetMat))
            {
                Status = "4K skin waiting for the native wrinkle material cache.";
                return;
            }
            var cachedSlot = Same(writer!._targetMat, slots[0]) ? 0 : Same(writer!._targetMat, slots[1]) ? 1 : -1;
            if (cachedSlot < 0)
            {
                Status = "4K skin unavailable; wrinkle material is externally controlled.";
                return;
            }

            if (!LoadAssets())
            {
                Status = "4K skin unavailable; paired texture assets could not be loaded. Toggle to retry.";
                return;
            }
            _binding = new Binding(target!, writer!, slots[0], slots[1], cachedSlot, _head!, _body!);
            if (!_binding.Install())
            {
                _yielded = true;
                Release();
                Sweep();
                Status = "4K skin yielded to external material changes.";
                return;
            }
            Status = "Local 4K head and body skin active.";
        }
        catch (Exception)
        {
            _yielded = true;
            Release();
            Sweep();
            Status = "4K skin unavailable; owned changes released where accessible.";
        }
    }

    private bool LoadAssets()
    {
        if (_assetsAttempted) return Live(_head) && Live(_body);
        _assetsAttempted = true;
        try
        {
            var head = VirginiaModelAssets.Read("virginia-head-4k.png", HeadHash);
            var body = VirginiaModelAssets.Read("virginia-body-4k.png", BodyHash);
            _head = LoadTexture(head, "VirginiaHead.Modstoke4K");
            _body = LoadTexture(body, "VirginiaBody.Modstoke4K");
            return true;
        }
        catch (Exception)
        {
            if (Live(_head)) UnityObject.Destroy(_head);
            if (Live(_body)) UnityObject.Destroy(_body);
            _head = _body = null;
            return false;
        }
    }

    private static Texture2D LoadTexture(byte[] bytes, string name)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
        try
        {
            if (!ImageConversion.LoadImage(texture, bytes, true)
                || texture.width != 4096 || texture.height != 4096
                || texture.format != TextureFormat.RGBA32 || texture.mipmapCount < 2 || texture.isReadable)
                throw new InvalidDataException("Unsupported paired skin texture.");
            texture.name = name;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 8;
            return texture;
        }
        catch
        {
            UnityObject.Destroy(texture);
            throw;
        }
    }

    private static bool Original(Material material, string name)
    {
        if (!Live(material) || (material.name != name && material.name != name + " (Instance)")
            || !material.HasProperty(Albedo)) return false;
        var source = material.GetTexture(Albedo);
        return Live(source) && source.width == 2048 && source.height == 2048;
    }

    private void Release()
    {
        if (_binding == null) return;
        _retired.Add(_binding);
        _binding = null;
    }

    private void Sweep()
    {
        try
        {
            _renderers.RemoveAll(r => !Live(r));
            _controllers.RemoveAll(c => !Live(c));
            foreach (var state in _retired.ToArray())
            {
                state.Restore();
                if (state.RestorationPending) continue;
                if (Live(state.Head) && !Referenced(state.Head)) UnityObject.Destroy(state.Head);
                if (Live(state.Body) && !Referenced(state.Body)) UnityObject.Destroy(state.Body);
                if (!Live(state.Head) && !Live(state.Body)) _retired.Remove(state);
            }
            if ((!_disposed && _enabled) || _retired.Count != 0) return;
            if (Live(_head) && !Referenced(_head)) { UnityObject.Destroy(_head); _head = null; }
            if (Live(_body) && !Referenced(_body)) { UnityObject.Destroy(_body); _body = null; }
            if (_head == null && _body == null)
            {
                _renderers.Clear();
                _controllers.Clear();
            }
        }
        catch (Exception)
        {
            // Unreadable native references are not evidence that an owned object is unused.
        }
    }

    private bool Referenced(UnityObject owned)
    {
        // Inspect every remembered slot and cache, including moved clones and adopted albedos.
        foreach (var renderer in _renderers)
        {
            if (!Live(renderer)) continue;
            var slots = renderer.sharedMaterials;
            if (slots == null) continue;
            foreach (var material in slots)
                if (Uses(material, owned)) return true;
        }
        foreach (var controller in _controllers)
            if (Live(controller) && Uses(controller._targetMat, owned)) return true;
        return false;
    }

    private static bool Uses(Material? material, UnityObject owned) =>
        Same(material, owned) || Live(material) && material!.HasProperty(Albedo)
            && Same(material.GetTexture(Albedo), owned);

    private static void SweepPending()
    {
        foreach (var skin in PendingCleanup.ToArray())
        {
            skin.Sweep();
            if (skin._retired.Count == 0 && !Live(skin._head) && !Live(skin._body))
                PendingCleanup.Remove(skin);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        _actor = null;
        Sweep();
        if (_retired.Count != 0 || Live(_head) || Live(_body)) PendingCleanup.Add(this);
        Status = "4K skin stopped.";
    }

    private static bool Live([NotNullWhen(true)] UnityObject? value)
    {
        try { return value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value; }
        catch (Exception) { return false; }
    }

    private static bool Same(UnityObject? left, UnityObject? right) =>
        Live(left) && Live(right) && left!.Pointer == right!.Pointer;

    private sealed class Binding
    {
        private readonly SkinnedMeshRenderer _renderer;
        private readonly global::VirginiaWrinkleController _writer;
        private readonly Material _originalHead, _originalBody;
        private readonly Texture2D _headTexture, _bodyTexture;
        private readonly int _cachedSlot;
        private bool _restoreHead, _restoreBody, _restoreCache;
        internal readonly Material Head, Body;
        internal bool RestorationPending => _restoreHead || _restoreBody || _restoreCache;
        internal bool Targets(SkinnedMeshRenderer renderer) => Same(_renderer, renderer);
        private Material CachedClone => _cachedSlot == 0 ? Head : Body;
        private Material CachedOriginal => _cachedSlot == 0 ? _originalHead : _originalBody;

        internal Binding(SkinnedMeshRenderer renderer, global::VirginiaWrinkleController writer,
            Material head, Material body, int cachedSlot, Texture2D headTexture, Texture2D bodyTexture)
        {
            _renderer = renderer;
            _writer = writer;
            _originalHead = head;
            _originalBody = body;
            _cachedSlot = cachedSlot;
            _headTexture = headTexture;
            _bodyTexture = bodyTexture;
            Head = new Material(head);
            try
            {
                Body = new Material(body);
                Head.name = "VirginiaHead.Modstoke4K";
                Body.name = "VirginiaBody.Modstoke4K";
                headTexture.wrapModeU = head.GetTexture(Albedo).wrapModeU;
                headTexture.wrapModeV = head.GetTexture(Albedo).wrapModeV;
                bodyTexture.wrapModeU = body.GetTexture(Albedo).wrapModeU;
                bodyTexture.wrapModeV = body.GetTexture(Albedo).wrapModeV;
                Head.SetTexture(Albedo, headTexture);
                Body.SetTexture(Albedo, bodyTexture);
            }
            catch
            {
                UnityObject.Destroy(Head);
                if (Live(Body)) UnityObject.Destroy(Body);
                throw;
            }
        }

        internal bool Install()
        {
            var slots = _renderer.sharedMaterials;
            if (slots == null || slots.Length < 2 || !Same(slots[0], _originalHead)
                || !Same(slots[1], _originalBody) || !Same(_writer.TargetRenderer, _renderer)
                || !Same(_writer._targetMat, CachedOriginal)) return false;
            // Record ownership before each native write so partial installation can be restored.
            _restoreHead = _restoreBody = true;
            slots[0] = Head;
            slots[1] = Body;
            _renderer.sharedMaterials = slots;
            _restoreCache = true;
            _writer._targetMat = CachedClone;
            return Intact();
        }

        internal bool Intact()
        {
            // No CopyPropertiesFromMaterial: native wrinkles now write directly into CachedClone.
            if (!Live(_renderer) || !Live(_writer) || !Same(_writer.TargetRenderer, _renderer)
                || !Same(_writer._targetMat, CachedClone)) return false;
            var slots = _renderer.sharedMaterials;
            return slots != null && slots.Length >= 2 && Same(slots[0], Head) && Same(slots[1], Body)
                && Same(Head.GetTexture(Albedo), _headTexture) && Same(Body.GetTexture(Albedo), _bodyTexture);
        }

        internal void Restore()
        {
            if (_restoreCache)
            {
                try
                {
                    if (Live(_writer) && Same(_writer._targetMat, CachedClone) && Live(CachedOriginal))
                        _writer._targetMat = CachedOriginal;
                    _restoreCache = false;
                }
                catch (Exception) { }
            }
            if (!_restoreHead && !_restoreBody) return;
            try
            {
                if (Live(_renderer))
                {
                    var slots = _renderer.sharedMaterials;
                    var changed = false;
                    if (slots != null && slots.Length > 0 && _restoreHead
                        && Same(slots[0], Head) && Live(_originalHead))
                    {
                        slots[0] = _originalHead;
                        changed = true;
                    }
                    if (slots != null && slots.Length > 1 && _restoreBody
                        && Same(slots[1], Body) && Live(_originalBody))
                    {
                        slots[1] = _originalBody;
                        changed = true;
                    }
                    if (changed) _renderer.sharedMaterials = slots!;
                }
                _restoreHead = _restoreBody = false;
            }
            catch (Exception) { }
        }
    }
}
