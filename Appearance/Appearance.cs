using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sons.Ai.Vail;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Virginia;

internal sealed partial class VirginiaAppearance : IDisposable
{
    private readonly Dictionary<IntPtr, Scalar<SkinnedMeshRenderer, SkinQuality>> _skinning = new();
    private readonly Dictionary<IntPtr, LodState> _lods = new();
    private Scalar<Animator, AnimatorCullingMode>? _animation;
    private VailActor? _actor;
    private bool _disposed;
    private int _errors;
    internal string Status { get; private set; } = "Appearance is off.";

    internal void Update(VirginiaSettings settings, VirginiaPresentationObservation observation)
    {
        if (_disposed) return;
        _errors = 0;
        // The master-off path must never resolve an actor or search its hierarchy.
        if (!settings.AppearanceEnabled)
        {
            Restore();
            Status = _errors == 0 ? "Appearance is off." : "Appearance is off; some native objects could not be restored.";
            if (_retired.Count != 0) Status += " Externally retained materials or pending cleanup remain.";
            return;
        }

        try
        {
            if (!observation.TryResolve(out var actor, out var reason) || !Live(actor))
            {
                Restore();
                Status = string.IsNullOrEmpty(reason) ? "Virginia is unavailable." : reason;
                return;
            }
            if (!Same(_actor, actor))
            {
                Restore();
                _actor = actor;
            }

            var garments = new Dictionary<IntPtr, SkinnedMeshRenderer>();
            foreach (var renderer in observation.Renderers)
            {
                Try(() =>
                {
                    if (Live(renderer) && TryGarment(renderer, out _, out _))
                        garments[renderer.Pointer] = renderer;
                });
            }
            UpdateSkinning(garments, settings.SmoothSkinning);
            UpdateAnimation(actor, settings.FullRateAnimation);
            UpdateLods(actor, garments, settings.DetailedMesh);
            UpdateMaterials(garments, settings);
            var skinCount = _skinning.Values.Count(s => !s.Yielded);
            var lodCount = _lods.Values.Count(s => !s.Yielded);
            Status = $"Verified garments: {garments.Count}. "
                + (settings.SmoothSkinning ? $"Four-bone skinning: {skinCount}. " : "")
                + (settings.DetailedMesh ? $"Extended authored LOD groups: {lodCount} (garment-only). " : "")
                + (settings.FullRateAnimation
                    ? (_animation is { Yielded: false } ? "Animator culling: always animate. " : "Animator culling: unavailable or externally controlled. ")
                    : "")
                + MaterialStatus(settings);
            if (_errors != 0) Status += $" Native operations unavailable: {_errors}.";
        }
        catch (Exception)
        {
            Restore();
            Status = "Appearance unavailable; owned changes were released where native objects remain accessible.";
        }
    }

    private void UpdateSkinning(Dictionary<IntPtr, SkinnedMeshRenderer> garments, bool enabled)
    {
        foreach (var pair in _skinning.ToArray())
        {
            if (!enabled || !garments.ContainsKey(pair.Key) || !Live(pair.Value.Target))
            {
                if (Try(pair.Value.Restore)) _skinning.Remove(pair.Key);
            }
        }
        if (!enabled) return;
        foreach (var pair in garments)
        {
            Try(() =>
            {
                if (!_skinning.TryGetValue(pair.Key, out var state))
                {
                    state = new(pair.Value, r => r.quality, (r, v) => r.quality = v, SkinQuality.Bone4);
                    _skinning.Add(pair.Key, state);
                    state.Apply();
                }
                else state.Check();
            });
        }
    }

    private void UpdateAnimation(VailActor actor, bool enabled)
    {
        var animator = enabled ? actor.GetAnimator() : null;
        if (_animation != null && (!enabled || !Same(_animation.Target, animator)))
        {
            if (!Try(_animation.Restore)) return;
            _animation = null;
        }
        if (!enabled || !Live(animator)) return;
        if (_animation == null)
        {
            _animation = new(animator!, a => a.cullingMode, (a, v) => a.cullingMode = v,
                AnimatorCullingMode.AlwaysAnimate);
            _animation.Apply();
        }
        else _animation.Check();
    }

    private void UpdateLods(VailActor actor, Dictionary<IntPtr, SkinnedMeshRenderer> garments, bool enabled)
    {
        var seen = new HashSet<IntPtr>();
        if (enabled)
        {
            foreach (var group in actor.GetComponentsInChildren<LODGroup>(true))
            {
                Try(() =>
                {
                    if (!Live(group)) return;
                    var levels = group.GetLODs();
                    if (levels == null || levels.Length == 0) return;
                    var hasRenderer = false;
                    foreach (var level in levels)
                    {
                        if (level == null || level.renderers == null) return;
                        foreach (var renderer in level.renderers)
                        {
                            if (!Live(renderer) || !garments.ContainsKey(renderer.Pointer)) return;
                            hasRenderer = true;
                        }
                    }
                    if (!hasRenderer) return;
                    seen.Add(group.Pointer);
                    if (!_lods.TryGetValue(group.Pointer, out var state))
                    {
                        state = new(group, levels);
                        _lods.Add(group.Pointer, state);
                        state.Apply(levels);
                    }
                    else state.Check(levels);
                });
            }
        }
        foreach (var pair in _lods.ToArray())
        {
            if (!seen.Contains(pair.Key) && Try(pair.Value.Restore)) _lods.Remove(pair.Key);
        }
    }

    private void Restore()
    {
        foreach (var pair in _skinning.ToArray())
            if (Try(pair.Value.Restore)) _skinning.Remove(pair.Key);
        foreach (var pair in _lods.ToArray())
            if (Try(pair.Value.Restore)) _lods.Remove(pair.Key);
        if (_animation != null && Try(_animation.Restore)) _animation = null;
        RestoreMaterials();
        _actor = null;
    }

    private bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception) { _errors++; return false; }
    }

    private static bool Live(UnityObject? value)
    {
        try { return value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value; }
        catch (Exception) { return false; }
    }

    private static bool Same(UnityObject? left, UnityObject? right) =>
        Live(left) && Live(right) && left!.Pointer == right!.Pointer;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
        Status = "Appearance stopped.";
    }

    private sealed class Scalar<TObject, TValue>(
        TObject target, Func<TObject, TValue> read, Action<TObject, TValue> write, TValue applied)
        where TObject : UnityObject
        where TValue : struct
    {
        internal readonly TObject Target = target;
        private readonly TValue _original = read(target);
        private bool _applied;
        internal bool Yielded { get; private set; }

        internal void Apply()
        {
            _applied = true;
            write(Target, applied);
        }

        internal void Check()
        {
            if (!Live(Target) || !EqualityComparer<TValue>.Default.Equals(read(Target), applied))
                Yielded = true;
        }

        internal void Restore()
        {
            if (_applied && !Yielded && Live(Target)
                && EqualityComparer<TValue>.Default.Equals(read(Target), applied))
                write(Target, _original);
            _applied = false;
        }
    }

    private sealed class LodState
    {
        private readonly LODGroup _group;
        private readonly float[] _original;
        private readonly IntPtr[][] _renderers;
        private bool _applied;
        internal bool Yielded { get; private set; }

        internal LodState(LODGroup group, Il2CppReferenceArray<LOD> levels)
        {
            _group = group;
            _original = levels.Select(l => l.screenRelativeTransitionHeight).ToArray();
            _renderers = levels.Select(l => l.renderers.Select(r => r.Pointer).ToArray()).ToArray();
        }

        private bool MatchesRoster(Il2CppReferenceArray<LOD> levels)
        {
            if (levels == null || levels.Length != _original.Length) return false;
            for (var i = 0; i < levels.Length; i++)
            {
                if (levels[i] == null) return false;
                var renderers = levels[i].renderers;
                if (renderers == null || renderers.Length != _renderers[i].Length) return false;
                for (var j = 0; j < renderers.Length; j++)
                    if (!Live(renderers[j]) || renderers[j].Pointer != _renderers[i][j]) return false;
            }
            return true;
        }

        internal void Apply(Il2CppReferenceArray<LOD> levels)
        {
            _applied = true;
            for (var i = 0; i < levels.Length; i++)
            {
                var level = levels[i];
                level.screenRelativeTransitionHeight = _original[i] * 0.5f;
                levels[i] = level;
            }
            _group.SetLODs(levels);
        }

        internal void Check(Il2CppReferenceArray<LOD> levels)
        {
            if (Yielded) return;
            if (!MatchesRoster(levels))
            {
                Yielded = true;
                return;
            }
            for (var i = 0; i < levels.Length; i++)
            {
                if (levels[i].screenRelativeTransitionHeight.Equals(_original[i] * 0.5f)) continue;
                Restore();
                Yielded = true;
                return;
            }
        }

        internal void Restore()
        {
            if (!_applied || !Live(_group)) return;
            var levels = _group.GetLODs();
            if (!MatchesRoster(levels)) return;
            var changed = false;
            for (var i = 0; i < levels.Length; i++)
            {
                var level = levels[i];
                if (!level.screenRelativeTransitionHeight.Equals(_original[i] * 0.5f)) continue;
                level.screenRelativeTransitionHeight = _original[i];
                levels[i] = level;
                changed = true;
            }
            if (changed) _group.SetLODs(levels);
            _applied = false;
        }
    }
}
