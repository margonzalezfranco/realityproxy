using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Unity.Mathematics;
using Unity.XR.CoreUtils;
using Unity.XR.CoreUtils.Bindings;
using UnityEngine.XR.Interaction.Toolkit.Utilities;
using UnityEngine.XR.Interaction.Toolkit.Utilities.Tweenables.SmartTweenableVariables;

/// <summary>
/// Extends LazyFollow to allow separate targets for position and rotation.
/// Position target is typically the hand for following,
/// while rotation target is typically the main camera for looking at.
/// </summary>
public class DualTargetLazyFollow : LazyFollow
{
    private Transform _positionTarget;
    private Transform _rotationTarget;

    /// <summary>
    /// The target to follow for position updates.
    /// </summary>
    public Transform positionTarget
    {
        get => _positionTarget;
        set
        {
            _positionTarget = value;
            // Also update the base target for position-related calculations
            if (positionFollowMode != PositionFollowMode.None)
            {
                base.target = value;
            }
        }
    }

    /// <summary>
    /// The target to look at for rotation updates.
    /// </summary>
    public Transform rotationTarget
    {
        get => _rotationTarget;
        set
        {
            _rotationTarget = value;
            // Update the base target for rotation-related calculations
            if (rotationFollowMode == RotationFollowMode.LookAt || 
                rotationFollowMode == RotationFollowMode.LookAtWithWorldUp)
            {
                base.target = value;
            }
        }
    }

    /// <summary>
    /// Override to handle the different targets for position and rotation
    /// </summary>
    protected override bool TryGetThresholdTargetPosition(out Vector3 newTarget)
    {
        if (positionFollowMode == PositionFollowMode.None || _positionTarget == null)
        {
            newTarget = followInLocalSpace ? transform.localPosition : transform.position;
            return false;
        }

        // Use position target for position following
        if (followInLocalSpace)
            newTarget = _positionTarget.localPosition + targetOffset;
        else
            newTarget = _positionTarget.position + _positionTarget.TransformVector(targetOffset);

        return true;
    }

    /// <summary>
    /// Override to handle the different targets for position and rotation
    /// </summary>
    protected override bool TryGetThresholdTargetRotation(out Quaternion newTarget)
    {
        switch (rotationFollowMode)
        {
            case RotationFollowMode.None:
                newTarget = followInLocalSpace ? transform.localRotation : transform.rotation;
                return false;

            case RotationFollowMode.LookAt:
            case RotationFollowMode.LookAtWithWorldUp:
                if (_rotationTarget == null)
                {
                    newTarget = transform.rotation;
                    return false;
                }
                // Use rotation target for look-at modes
                var forward = (transform.position - _rotationTarget.position).normalized;
                if (rotationFollowMode == RotationFollowMode.LookAt)
                    BurstMathUtility.OrthogonalLookRotation(forward, Vector3.up, out newTarget);
                else
                    BurstMathUtility.LookRotationWithForwardProjectedOnPlane(forward, Vector3.up, out newTarget);
                break;

            case RotationFollowMode.Follow:
                if (_positionTarget == null)
                {
                    newTarget = transform.rotation;
                    return false;
                }
                // Use position target for rotation following
                newTarget = followInLocalSpace ? _positionTarget.localRotation : _positionTarget.rotation;
                break;

            default:
                Debug.LogError($"Unhandled {nameof(RotationFollowMode)}={rotationFollowMode}", this);
                newTarget = transform.rotation;
                return false;
        }

        return true;
    }
}