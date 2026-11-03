using Sons.Ai.Vail;
using UnityEngine;

namespace Modstoke.Virginia;

internal sealed partial class VirginiaSecondaryMotion
{
    private const int NeutralIdle = 1196481036;
    private Animator? _idleAnimator;
    private bool _idleReady, _idleRequested, _idleSafe, _proportionsRequested;
    private float _idleBlend, _idleSeconds;
    private int _scaredIdle, _holdItemIdle;

    private void UpdateIdleEligibility(VailActor actor, bool requested)
    {
        _idleRequested = requested;
        if (!requested)
        {
            _idleSafe = false;
            foreach (var binding in _bindings) binding.SetIdle(0, 0);
            _idleBlend = _idleSeconds = 0;
            return;
        }
        var animator = actor.GetAnimator();
        if (!Same(_idleAnimator, animator))
        {
            _idleAnimator = animator;
            _idleReady = false;
            _idleBlend = _idleSeconds = 0;
            if (Live(animator))
            {
                var parameters = animator.parameters;
                _idleReady = parameters.Any(p => p.name == "ScaredIdle" && p.type == AnimatorControllerParameterType.Float)
                    && parameters.Any(p => p.name == "HoldItemIdle" && p.type == AnimatorControllerParameterType.Float);
                _scaredIdle = Animator.StringToHash("ScaredIdle");
                _holdItemIdle = Animator.StringToHash("HoldItemIdle");
            }
        }
        _idleSafe = !actor.IsDead() && !actor.IsDying() && !actor.IsOnFire() && !actor.IsSleeping()
            && !actor.IsScared() && !actor.IsAngry() && !actor.IsAlerted()
            && !actor.IsInCombatAwareState() && !actor._meleeIsActive && actor.GetState() == State.World;
    }

    internal void LateUpdate()
    {
        if (_disposed || _failed || _bindings.Count == 0) return;
        try
        {
            foreach (var binding in _bindings) binding.SetProportions(_proportionsRequested ? 100 : 0);
            if (!_idleRequested) return;
            var delta = Time.deltaTime;
            if (delta <= 0) return;
            if (delta > .15f)
            {
                _idleBlend = _idleSeconds = 0;
                foreach (var binding in _bindings) binding.SetIdle(0, 0);
                return;
            }
            var neutral = _idleReady && _idleSafe && Live(_actor) && Live(_idleAnimator)
                && _idleAnimator!.enabled && _idleAnimator.gameObject.activeInHierarchy
                && !_idleAnimator.IsInTransition(0)
                && _idleAnimator.GetCurrentAnimatorStateInfo(0).fullPathHash == NeutralIdle
                && _idleAnimator.GetFloat(_scaredIdle) == 0f && _idleAnimator.GetFloat(_holdItemIdle) == 0f;
            _idleBlend = Mathf.MoveTowards(_idleBlend, neutral ? 1 : 0, delta * 4);
            if (_idleBlend > 0) _idleSeconds = (_idleSeconds + delta) % 148f;
            else _idleSeconds = 0;
            var breath = .5f - .5f * Mathf.Cos(_idleSeconds * (2 * Mathf.PI / 4.5f));
            var cycle = _idleSeconds % 37f;
            var settling = cycle is >= 26 and < 32 ? Mathf.Sin((cycle - 26) * Mathf.PI / 6) : 0;
            settling *= settling;
            var breathWeight = Mathf.Min(1, breath * .7f + settling * .3f) * _idleBlend * 100;
            var relaxWeight = settling * _idleBlend * 65;
            foreach (var binding in _bindings) binding.SetIdle(breathWeight, relaxWeight);
        }
        catch (Exception error)
        {
            Release();
            _failed = true;
            Status = "Body presentation stopped after a native change; toggle appearance to retry.";
            Console.WriteLine("Modstoke Virginia idle animation: " + error.Message);
        }
    }

    private void ResetIdle()
    {
        _idleAnimator = null;
        _idleReady = _idleRequested = _idleSafe = false;
        _proportionsRequested = false;
        _idleBlend = _idleSeconds = 0;
    }
}
