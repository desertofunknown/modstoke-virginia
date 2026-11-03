using Sons.Ai.Vail;

namespace Modstoke.Virginia;

internal static class VirginiaActorAccess
{
    internal static bool TryResolve(out VailActor actor, out string reason)
    {
        actor = null!;
        reason = "Virginia is not currently available nearby.";
        try
        {
            var manager = VailActorManager._instance;
            if (manager == null || manager.WasCollected || !manager) return false;
            var actors = manager._activeActors;
            if (actors == null || actors.WasCollected || actors.Count > 4096) return false;
            var count = actors.Count;
            VailActor? found = null;
            for (var index = 0; index < count; index++)
            {
                var candidate = actors[index];
                if (candidate == null || candidate.WasCollected || !candidate
                    || !candidate.isActiveAndEnabled || candidate.TypeId != VailActorTypeId.Virginia)
                    continue;
                if (found != null && found.Pointer != candidate.Pointer)
                {
                    reason = "More than one active Virginia actor was found.";
                    return false;
                }
                found = candidate;
            }
            var current = VailActorManager._instance;
            if (found == null || actors.Count != count || current == null || current.WasCollected
                || !current || current.Pointer != manager.Pointer
                || current._activeActors == null || current._activeActors.Pointer != actors.Pointer)
                return false;
            actor = found;
            reason = "";
            return true;
        }
        catch (Exception)
        {
            reason = "Virginia's current actor could not be read.";
            return false;
        }
    }
}
