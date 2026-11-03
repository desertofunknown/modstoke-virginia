using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sons.Ai.Vail;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Virginia;

internal sealed partial class VirginiaSecondaryMotion : IDisposable
{
    private readonly List<Binding> _bindings = [];
    private readonly List<GameObject> _roots = [];
    private readonly List<DynamicBone> _solvers = [];
    private VirginiaModelAssets.MotionAsset? _asset;
    private VailActor? _actor;
    private bool _failed, _disposed;
    private Vector3 _lastPosition;
    private long _lastUpdate;
    internal string Status { get; private set; } = "Secondary motion is off.";

    internal void Update(VirginiaSettings settings, VirginiaPresentationObservation observation)
    {
        if (_disposed) return;
        var physics = settings.SecondaryMotion && settings.MotionStrength > 0;
        if (!settings.AppearanceEnabled || !(physics || settings.RefinedProportions || settings.IdleAnimation))
        {
            Release();
            _failed = false;
            Status = _roots.Count == 0 ? "Body presentation is off."
                : "Body presentation is off; externally retained helper references remain.";
            return;
        }
        if (_failed) return;
        try
        {
            if (!observation.TryResolve(out var actor, out var reason) || !Live(actor))
            {
                Release();
                Status = reason;
                return;
            }
            var camera = Camera.main;
            if (!Live(camera) || (camera.transform.position - actor.transform.position).sqrMagnitude > 625f)
            {
                Release();
                Status = "Secondary motion is resting outside the 25 m viewing range.";
                return;
            }
            var now = Environment.TickCount64;
            if (!Same(_actor, actor) || now - _lastUpdate > 1500
                || (actor.transform.position - _lastPosition).sqrMagnitude > 9f)
                Release();
            _lastUpdate = now;
            _lastPosition = actor.transform.position;
            if (_bindings.Any(b => !b.Intact()))
            {
                Release();
                _failed = true;
                Status = "Secondary motion yielded to a native or external mesh/bone change. Toggle it to retry.";
                return;
            }
            var renderers = observation.Renderers;
            var body = Find(renderers, "C1:Virginia_body");
            var cloth = Find(renderers, "C1:Virginia_cloth");
            if (!Live(body) || !Live(cloth) || !Active(body!) || !Active(cloth!)
                || renderers.Any(r => Live(r) && Active(r) && Live(r.sharedMesh)
                    && IsOtherOutfit(r.sharedMesh.name)))
            {
                Release();
                Status = "Secondary motion requires Virginia's original leotard and body.";
                return;
            }
            if (_bindings.Count == 0)
            {
                if (_roots.Count != 0)
                {
                    Status = "Secondary motion is waiting for previously retained helpers to be released.";
                    return;
                }
                _asset ??= VirginiaModelAssets.Load();
                var targets = new[] { body!, cloth! };
                for (var i = 0; i < targets.Length; i++)
                {
                    if (!Matches(targets[i], _asset.Meshes[i]))
                    {
                        Status = "Waiting for the matching native body/cloth bone remap.";
                        return;
                    }
                }
                _actor = actor;
                Bind(targets, _asset);
            }
            foreach (var solver in _solvers)
            {
                if (!Live(solver)) continue;
                solver.SetWeight(physics ? Math.Clamp(settings.MotionStrength, 0f, 1f) * 0.5f : 0);
                solver.enabled = physics;
            }
            foreach (var binding in _bindings)
                binding.SetProportions(settings.RefinedProportions ? 100 : 0);
            _proportionsRequested = settings.RefinedProportions;
            UpdateIdleEligibility(actor, settings.IdleAnimation);
            Status = "Original leotard: "
                + (settings.RefinedProportions ? "refined silhouette; " : "")
                + (physics ? "restrained secondary motion; " : "")
                + (settings.IdleAnimation ? (_idleReady ? "idle breathing enabled. " : "idle motion waiting for a compatible animator. ") : "")
                + "Native limb and facial animation retained.";
        }
        catch (Exception error)
        {
            Release();
            _failed = true;
            Status = "Secondary motion unavailable; toggle to retry after checking the paired assets.";
            Console.WriteLine("Modstoke Virginia secondary motion: " + error.Message);
        }
    }

    private static bool IsOtherOutfit(string name) => name is "C1:dress" or "C1:camosuit"
        or "C1:LeatherSuit" or "C1:tracksuitShirt" or "C1:tracksuitPants"
        || name.Contains("armor", StringComparison.OrdinalIgnoreCase)
        || name.Contains("armour", StringComparison.OrdinalIgnoreCase);

    private SkinnedMeshRenderer? Find(Il2CppArrayBase<SkinnedMeshRenderer> renderers, string name)
    {
        SkinnedMeshRenderer? found = null;
        foreach (var renderer in renderers)
        {
            if (!Live(renderer) || !Live(renderer.sharedMesh) || renderer.sharedMesh.name != name) continue;
            if (!Active(renderer)) continue;
            if (found != null) return null;
            found = renderer;
        }
        return found;
    }

    private static bool Active(SkinnedMeshRenderer renderer) => renderer.enabled && renderer.gameObject.activeInHierarchy;
    private static string BonePath(Transform bone)
    {
        var names = new List<string>();
        for (var i = 0; Live(bone) && i < 32; i++, bone = bone.parent)
        {
            names.Add(bone.name);
            if (bone.name != "C1:C_spineAHip_SC") continue;
            names.Reverse();
            return string.Join("/", names);
        }
        return "";
    }

    private static bool Matches(SkinnedMeshRenderer renderer, VirginiaModelAssets.MeshSpec spec)
    {
        var mesh = renderer.sharedMesh;
        var bones = renderer.bones;
        if (!Live(mesh) || mesh.name != spec.Name || mesh.vertexCount != spec.OriginalVertexCount
            || mesh.subMeshCount != spec.Submeshes.Length || mesh.blendShapeCount != spec.OriginalBlendShapeCount
            || bones.Length != spec.OriginalBoneCount) return false;
        for (var i = 0; i < bones.Length; i++)
            if (!Live(bones[i]) || BonePath(bones[i]) != spec.Bones[i].NormalizedSourcePath) return false;
        for (var i = 0; i < mesh.blendShapeCount; i++)
            if (mesh.GetBlendShapeName(i) != spec.BlendShapes[i].Name) return false;
        return true;
    }

    private void Bind(SkinnedMeshRenderer[] targets, VirginiaModelAssets.MotionAsset asset)
    {
        var parents = targets[0].bones.ToDictionary(BonePath, b => b);
        foreach (var bone in targets[1].bones)
            if (!parents.TryGetValue(BonePath(bone), out var expected) || !Same(bone, expected))
                throw new InvalidDataException("The cloth is not attached to the same native skeleton.");
        var bodyMatrix = targets[0].transform.localToWorldMatrix;
        var clothMatrix = targets[1].transform.localToWorldMatrix;
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                if (Math.Abs(bodyMatrix[row, column] - clothMatrix[row, column]) > 0.001f)
                    throw new InvalidDataException("Body and cloth mesh spaces do not match the authored asset.");
        var controls = new Dictionary<string, Transform>();
        foreach (var control in asset.Controls)
        {
            if (!parents.TryGetValue(control.ParentPath, out var parent))
                throw new InvalidDataException("The native motion parent is unavailable.");
            var root = new GameObject("Modstoke.Virginia." + control.Name);
            _roots.Add(root);
            root.SetActive(false);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = VirginiaModelAssets.V3(control.AnchorLocal);
            var motion = Child(root.transform, control.Name, control.BoneLocal);
            Child(motion, "Tip", control.TipLocal);
            controls.Add("$control:" + control.Name, motion);
            var solver = root.AddComponent<DynamicBone>();
            _solvers.Add(solver);
            solver.m_Root = root.transform;
            solver.m_UpdateMode = DynamicBone.UpdateMode.Normal;
            solver.m_UpdateRate = 60;
            solver.m_Damping = 0.65f;
            solver.m_Elasticity = 0.15f;
            solver.m_Stiffness = 0.85f;
            solver.m_Inert = 0.7f;
            solver.m_Friction = 0;
            solver.m_Radius = 0;
            solver.m_EndLength = 0;
            solver.m_Gravity = Vector3.zero;
            solver.m_Force = Vector3.zero;
            solver.m_DistantDisable = false;
            solver.SetWeight(0.175f);
        }
        for (var i = 0; i < targets.Length; i++)
        {
            var spec = asset.Meshes[i];
            var binding = new Binding(targets[i], VirginiaModelAssets.Create(spec));
            _bindings.Add(binding);
            binding.Apply(spec, controls);
        }
        foreach (var root in _roots) root.SetActive(true);
    }

    private static Transform Child(Transform parent, string name, float[] position)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        child.localPosition = VirginiaModelAssets.V3(position);
        return child;
    }

    private void Release()
    {
        ResetIdle();
        var released = true;
        foreach (var root in _roots)
        {
            try { if (Live(root)) root.SetActive(false); }
            catch (Exception) { released = false; }
        }
        // Unbind meshes first; no renderer may retain a destroyed helper transform.
        foreach (var binding in _bindings)
        {
            try { released &= binding.Restore(); }
            catch (Exception) { released = false; }
        }
        _actor = null;
        if (!released) return;
        _bindings.Clear();
        foreach (var root in _roots.ToArray())
        {
            try
            {
                if (Live(root)) UnityObject.Destroy(root);
                _roots.Remove(root);
            }
            catch (Exception) { }
        }
        if (_roots.Count == 0) _solvers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        _asset = null;
    }

    private static bool Live(UnityObject? value)
    {
        try { return value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value; }
        catch (Exception) { return false; }
    }
    private static bool Same(UnityObject? a, UnityObject? b) => Live(a) && Live(b) && a!.Pointer == b!.Pointer;

    private sealed class Binding(SkinnedMeshRenderer renderer, Mesh owned)
    {
        private readonly Mesh _original = renderer.sharedMesh;
        private readonly Il2CppReferenceArray<Transform> _bones = renderer.bones;
        private readonly Bounds _bounds = renderer.localBounds;
        private readonly SkinQuality _quality = renderer.quality;
        private Bounds _appliedBounds;
        private Transform[] _appliedBones = [];
        private int _proportions, _breath, _relax;
        private float _lastProportions = float.NaN, _lastBreath = float.NaN, _lastRelax = float.NaN;

        internal void Apply(VirginiaModelAssets.MeshSpec spec, Dictionary<string, Transform> controls)
        {
            _appliedBones = _bones.Concat(spec.Bones.Skip(_bones.Length)
                .Select(b => controls[b.NormalizedSourcePath])).ToArray();
            var weights = ReadShapes();
            renderer.bones = _appliedBones;
            renderer.sharedMesh = owned;
            for (var i = 0; i < weights.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
            _appliedBounds = _bounds;
            _appliedBounds.Expand(0.04f);
            renderer.localBounds = _appliedBounds;
            renderer.quality = SkinQuality.Bone4;
            _proportions = owned.GetBlendShapeIndex("Modstoke.Proportions");
            _breath = owned.GetBlendShapeIndex("Modstoke.Breath");
            _relax = owned.GetBlendShapeIndex("Modstoke.Relax");
            if (_proportions < 0 || _breath < 0 || _relax < 0)
                throw new InvalidDataException("Authored Virginia shapes are missing.");
        }

        internal void SetProportions(float value) => SetShape(_proportions, value, ref _lastProportions);
        internal void SetIdle(float breath, float relax)
        {
            SetShape(_breath, breath, ref _lastBreath);
            SetShape(_relax, relax, ref _lastRelax);
        }

        private void SetShape(int index, float value, ref float last)
        {
            if (!Live(renderer) || !Same(renderer.sharedMesh, owned) || index < 0) return;
            // Native Write Defaults may reset unbound shapes; check the live weight, not just our cache.
            if (!last.Equals(value) || !renderer.GetBlendShapeWeight(index).Equals(value))
                renderer.SetBlendShapeWeight(index, value);
            last = value;
        }

        private float[] ReadShapes() => Enumerable.Range(0, _original.blendShapeCount)
            .Select(renderer.GetBlendShapeWeight).ToArray();

        internal bool Intact()
        {
            if (!Live(renderer) || !Same(renderer.sharedMesh, owned)) return false;
            var bones = renderer.bones;
            if (bones.Length != _appliedBones.Length) return false;
            for (var i = 0; i < bones.Length; i++)
                if (!Same(bones[i], _appliedBones[i])) return false;
            return true;
        }

        internal bool Restore()
        {
            if (Live(renderer))
            {
                if (Same(renderer.sharedMesh, owned) && Live(_original))
                {
                    var weights = ReadShapes();
                    renderer.sharedMesh = _original;
                    for (var i = 0; i < weights.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
                }
                var current = renderer.bones;
                if (_appliedBones.Length != 0 && current.Length == _appliedBones.Length
                    && current.Select((b, i) => Same(b, _appliedBones[i])).All(b => b))
                    renderer.bones = _bones;
                if (renderer.localBounds.Equals(_appliedBounds)) renderer.localBounds = _bounds;
                if (renderer.quality == SkinQuality.Bone4) renderer.quality = _quality;
                // An external writer can adopt a mesh or individual helper bones.
                // Leave those objects alive and inactive until their references are gone.
                current = renderer.bones;
                if (Same(renderer.sharedMesh, owned) || current.Any(b =>
                    _appliedBones.Skip(_bones.Length).Any(helper => Same(b, helper)))) return false;
            }
            if (Live(owned)) UnityObject.Destroy(owned);
            return true;
        }
    }
}
