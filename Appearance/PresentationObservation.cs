using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sons.Ai.Vail;
using UnityEngine;

namespace Modstoke.Virginia;

// One synchronous presentation update only; consumers still check live state and ownership.
internal sealed class VirginiaPresentationObservation
{
    private readonly Lazy<(bool Available, VailActor Actor, string Reason)> _actor = new(() =>
    {
        var available = VirginiaActorAccess.TryResolve(out var actor, out var reason);
        return (available, actor, reason);
    });
    private readonly Lazy<Il2CppArrayBase<SkinnedMeshRenderer>> _renderers;

    internal VirginiaPresentationObservation()
    {
        // Lazy also rethrows discovery failures inside each requesting consumer's error boundary.
        _renderers = new(() => _actor.Value.Actor.GetComponentsInChildren<SkinnedMeshRenderer>(true));
    }

    internal bool TryResolve(out VailActor actor, out string reason)
    {
        var value = _actor.Value;
        actor = value.Actor;
        reason = value.Reason;
        return value.Available;
    }

    // Read only after successful resolution and the consumer's live-actor check.
    internal Il2CppArrayBase<SkinnedMeshRenderer> Renderers => _renderers.Value;
}
