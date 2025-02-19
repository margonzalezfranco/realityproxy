using UnityEngine;

/// <summary>
/// Put this script on each spawned hand (Left/Right).
/// It detects when we "grab" an anchor by OnTriggerEnter, 
/// moves the anchor with the hand,
/// and "releases" once we place it back down on a plane.
/// 
/// This version includes a simple "cooldown" so the anchor won't
/// be immediately grabbed again upon release.
/// </summary>
[RequireComponent(typeof(Collider))]
public class HandGrabTrigger : MonoBehaviour
{
    [Header("Plane Thresholds")]
    [Tooltip("How close to the plane the anchor must be to count as 'on-plane'.")]
    public float planeDistanceThreshold = 0.1f;

    // The anchor currently grabbed (if any)
    private SceneObjectAnchor _grabbedAnchor = null;

    // Tracks whether we've lifted the anchor off the plane
    private bool _isOutOfPlane = false;

    [Header("Layer for Plane Check")]
    [Tooltip("Which layers to treat as the plane/floor for raycast.")]
    public LayerMask planeLayer = 1 << 7; // Example: layer 7

    [Tooltip("Raycast length for checking distance to plane.")]
    public float checkRayLength = 2.0f;

    // We store the last anchor we released, so we don't immediately pick it back up
    private SceneObjectAnchor _lastReleasedAnchor = null;
    private bool _justReleased = false;


    /// <summary>
    /// Called when the hand's trigger collider intersects another collider.
    /// We check if it's an anchor's sphere, and if so, attempt to grab it
    /// if it's currently on-plane.
    /// </summary>
    private void OnTriggerEnter(Collider other)
    {
        Debug.Log("OnTriggerEnter - Start");

        // If we already grabbed something, ignore
        if (_grabbedAnchor != null)
        {
            Debug.Log("OnTriggerEnter - Already holding an anchor, ignoring");
            return;
        }

        // If we just released an anchor and haven't fully exited that anchor’s collider,
        // we skip re-grabbing the same anchor
        if (_justReleased)
        {
            var tmpAnchor = SceneObjectManager.Instance.GetAnchorByGameObject(other.gameObject);
            if (tmpAnchor != null && tmpAnchor == _lastReleasedAnchor)
            {
                Debug.Log("OnTriggerEnter - We just released this anchor, ignoring until OnTriggerExit");
                return;
            }
        }

        // Is the other collider part of an anchor's sphere?
        var anchor = SceneObjectManager.Instance.GetAnchorByGameObject(other.gameObject);
        if (anchor == null)
        {
            Debug.Log("OnTriggerEnter - Not an anchor's sphere, ignoring");
            return;
        }

        // Check if the anchor is "on-plane" (within threshold)
        if (IsOnPlane(anchor, out float dist) && dist < planeDistanceThreshold)
        {
            Debug.Log($"OnTriggerEnter - Found valid anchor on plane. Distance: {dist}");
            // Grab this anchor
            _grabbedAnchor = anchor;
            _grabbedAnchor.sphereObj.transform.SetParent(transform);

            Debug.Log($"HandGrabTrigger: Grabbed anchor '{anchor.label}'");
        }
        else
        {
            Debug.Log($"OnTriggerEnter - Anchor not on plane or too far. Distance: {dist}");
        }
    }

    /// <summary>
    /// Continuously checks if the grabbed anchor is lifted away from plane,
    /// and if so, waits for it to be placed back down to release.
    /// </summary>
    private void Update()
    {
        // If we're currently holding an anchor, check plane distance
        if (_grabbedAnchor != null)
        {
            bool onPlane = IsOnPlane(_grabbedAnchor, out float dist);

            // If we haven't lifted it off-plane yet...
            if (!_isOutOfPlane)
            {
                // The user lifts the anchor away from plane
                if (!onPlane || dist > planeDistanceThreshold)
                {
                    // Now we consider it "in the air"
                    _isOutOfPlane = true;
                }
            }
            else
            {
                // Once lifted, we wait for the user to put it back
                if (onPlane && dist < planeDistanceThreshold)
                {
                    // It's placed back on-plane => release
                    ReleaseAnchor();
                }
            }
        }
    }

    /// <summary>
    /// Releases the currently held anchor, stopping it from following the hand.
    /// </summary>
    private void ReleaseAnchor()
    {
        if (_grabbedAnchor == null)
            return;

        Debug.Log($"HandGrabTrigger: Released anchor '{_grabbedAnchor.label}'");

        // Mark this anchor as "just released" to avoid immediate re-pickup
        _lastReleasedAnchor = _grabbedAnchor;
        _justReleased = true;

        // Stop following the hand
        _grabbedAnchor.sphereObj.transform.SetParent(null);

        // Final position update
        _grabbedAnchor.position = _grabbedAnchor.sphereObj.transform.position;

        // Reset state
        _grabbedAnchor = null;
        _isOutOfPlane = false;
    }

    /// <summary>
    /// Performs a downward raycast from the anchor to see if it hits the plane,
    /// returning whether it is on-plane, plus the distance to plane (out param).
    /// </summary>
    private bool IsOnPlane(SceneObjectAnchor anchor, out float distanceToPlane)
    {
        distanceToPlane = float.PositiveInfinity;
        var anchorPos = anchor.sphereObj.transform.position;

        if (Physics.Raycast(anchorPos, Vector3.down, out RaycastHit hit, checkRayLength, planeLayer))
        {
            distanceToPlane = hit.distance;
            Debug.Log("IsOnPlane: true, distanceToPlane: " + distanceToPlane);
            return true;
        }
        Debug.Log("IsOnPlane: false");
        return false;
    }

    /// <summary>
    /// We reset _justReleased once our hand's trigger actually exits the last released anchor's collider,
    /// allowing us to pick it up again in the future.
    /// </summary>
    private void OnTriggerExit(Collider other)
    {
        var anchor = SceneObjectManager.Instance.GetAnchorByGameObject(other.gameObject);
        if (_justReleased && anchor == _lastReleasedAnchor)
        {
            // The hand has left the collider for the anchor we just released,
            // so we can safely reset.
            Debug.Log("OnTriggerExit - lastReleasedAnchor is out of the area, allowing re-grab");
            _lastReleasedAnchor = null;
            _justReleased = false;
        }
    }
}
