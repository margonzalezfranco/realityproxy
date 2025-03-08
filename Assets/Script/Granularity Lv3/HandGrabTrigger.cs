using UnityEngine;
using System.Collections;
using UnityEngine.XR.Hands;  // Add this for XRHand
using Unity.XR.CoreUtils;    // Add this if needed for other XR utilities
using UnityEngine.XR.Interaction.Toolkit.UI;

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

    [Header("Grab Settings")]
    [Tooltip("Offset from hand center where the anchor should be positioned")]
    public Vector3 grabOffset = Vector3.zero;

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

    // Add these event declarations at the top of the class
    public delegate void AnchorGrabEventHandler(SceneObjectAnchor anchor);
    public static event AnchorGrabEventHandler OnAnchorGrabbed;
    public static event AnchorGrabEventHandler OnAnchorReleased;

    [Header("Twin Object Settings")]
    [Tooltip("Offset position of the twin object relative to the anchor")]
    public Vector3 twinOffset = new Vector3(0.03f, -0.05f, 0f); // Default: 3cm right, 5cm down

    private GameObject _twinObject = null;
    private ObjectMeshGenerator objectMeshGenerator;

    private XRHand currentHandPose;

    private MeshRenderer labelMeshRenderer;
    private MeshRenderer sphereMeshRenderer;

    private void Start()
    {
        // Get reference to ObjectMeshGenerator
        objectMeshGenerator = FindAnyObjectByType<ObjectMeshGenerator>();
        if (objectMeshGenerator == null)
        {
            Debug.LogWarning("No ObjectMeshGenerator found in scene!");
        }
    }

    /// <summary>
    /// Called when the hand's trigger collider intersects another collider.
    /// We check if it's an anchor's sphere, and if so, attempt to grab it
    /// if it's currently on-plane.
    /// </summary>
    private void OnTriggerEnter(Collider other)
    {
        // Check if the collider has the SphereToggleScript component
        if (other.gameObject.GetComponent<SphereToggleScript>() == null)
        {
            return;  // Not a sphere with the required component, ignore
        }

        Debug.Log("OnTriggerEnter - Start");

        // If we already grabbed something, ignore
        if (_grabbedAnchor != null)
        {
            Debug.Log("OnTriggerEnter - Already holding an anchor, ignoring");
            return;
        }

        // If we just released an anchor and haven't fully exited that anchor's collider,
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
            
            // Make the anchor a sibling of the hand instead of a child
            _grabbedAnchor.sphereObj.transform.SetParent(transform.parent);

            // // Position the anchor at the hand's center
            // UpdateAnchorPosition();
            
            // Add and configure the DualTargetLazyFollow
            var lazyFollow = _grabbedAnchor.sphereObj.AddComponent<DualTargetLazyFollow>();
            
            // Configure following parameters
            lazyFollow.movementSpeed = 12f; // Adjust this value as needed
            lazyFollow.movementSpeedVariancePercentage = 0.25f;
            lazyFollow.minDistanceAllowed = 0.02f;
            lazyFollow.maxDistanceAllowed = 0.1f;
            lazyFollow.timeUntilThresholdReachesMaxDistance = 0.5f;
            
            // Configure rotation parameters
            lazyFollow.minAngleAllowed = 2f;
            lazyFollow.maxAngleAllowed = 10f;
            lazyFollow.timeUntilThresholdReachesMaxAngle = 0.5f;
            
            // Set the follow modes
            lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;
            lazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAt;
            
            // Set the targets separately
            lazyFollow.positionTarget = transform; // Hand is the position target
            lazyFollow.rotationTarget = Camera.main.transform; // Main camera is the rotation target
            
            // Set the offset
            lazyFollow.targetOffset = grabOffset;

            // Reset the label's rotation (find the child named "Label_*")
            Transform labelTransform = _grabbedAnchor.sphereObj.transform.GetComponentInChildren<LookAtCamera>()?.transform;
            if (labelTransform != null)
            {
                labelTransform.localRotation = Quaternion.identity;
                // Disable the mesh renderer
                labelMeshRenderer = labelTransform.gameObject.GetComponent<MeshRenderer>();
                if (labelMeshRenderer != null)
                {
                    labelMeshRenderer.enabled = false;
                }
            }

            sphereMeshRenderer = _grabbedAnchor.sphereObj.GetComponent<MeshRenderer>();
            if (sphereMeshRenderer != null)
            {
                sphereMeshRenderer.enabled = false;
            }


            // Generate twin object
            if (objectMeshGenerator != null)
            {
                StartCoroutine(objectMeshGenerator.EstimateAndGenerateObject(null, (generatedObj) => {
                    _twinObject = generatedObj;
                    _twinObject.transform.SetParent(transform.parent);
                    UpdateTwinPosition();
                }));
            }

            // Invoke the grab event
            OnAnchorGrabbed?.Invoke(_grabbedAnchor);

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
        // If we're currently holding an anchor, update its position
        if (_grabbedAnchor != null)
        {
            // UpdateAnchorPosition();
            
            if (_twinObject != null)
            {
                UpdateTwinPosition();
            }

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

    // New method to handle anchor position updates
    private void UpdateAnchorPosition()
    {
        if (_grabbedAnchor != null && _grabbedAnchor.sphereObj != null)
        {
            // Update position to match hand position plus offset
            _grabbedAnchor.sphereObj.transform.position = transform.position + transform.TransformDirection(grabOffset);
            
            // Reset the label's rotation (find the child named "Label_*")
            Transform labelTransform = _grabbedAnchor.sphereObj.transform.GetComponentInChildren<LookAtCamera>()?.transform;
            if (labelTransform != null && labelTransform.localRotation != Quaternion.identity)
            {
                labelTransform.localRotation = Quaternion.identity;
            }

            // Make the grabbed anchor look at the main camera
            if (Camera.main != null)
            {
                Vector3 directionToCamera = Camera.main.transform.position - _grabbedAnchor.sphereObj.transform.position;
                _grabbedAnchor.sphereObj.transform.rotation = Quaternion.LookRotation(-directionToCamera);
            }
        }
    }

    private void UpdateTwinPosition()
    {
        if (_twinObject != null && _grabbedAnchor != null && _grabbedAnchor.sphereObj != null)
        {
            // Get the anchor's position and rotation
            Vector3 anchorPos = _grabbedAnchor.sphereObj.transform.position;
            Quaternion anchorRot = _grabbedAnchor.sphereObj.transform.rotation;

            // Apply the offset in the anchor's local space
            Vector3 offsetPosition = anchorPos + anchorRot * twinOffset;

            // Update twin's transform
            _twinObject.transform.position = offsetPosition;
            _twinObject.transform.rotation = anchorRot;
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

        // Remove the lazy follow component
        var lazyFollow = _grabbedAnchor.sphereObj.GetComponent<DualTargetLazyFollow>();
        if (lazyFollow != null)
        {
            Destroy(lazyFollow);
        }

        // Invoke the release event before changing references
        OnAnchorReleased?.Invoke(_grabbedAnchor);

        // Enable the mesh renderer
        if (sphereMeshRenderer != null)
        {
            sphereMeshRenderer.enabled = true;
        }

        // also the label
        Transform labelTransform = _grabbedAnchor.sphereObj.transform.GetComponentInChildren<LookAtCamera>()?.transform;
        if (labelTransform != null)
        {
            labelMeshRenderer = labelTransform.gameObject.GetComponent<MeshRenderer>();
            if (labelMeshRenderer != null)
            {
                labelMeshRenderer.enabled = true;
            }
        }

        // Mark this anchor as "just released" to avoid immediate re-pickup
        _lastReleasedAnchor = _grabbedAnchor;
        _justReleased = true;

        // Final position update
        _grabbedAnchor.position = _grabbedAnchor.sphereObj.transform.position;

        // Clean up twin object
        if (_twinObject != null)
        {
            Destroy(_twinObject);
            _twinObject = null;
        }

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

        // Check both up and down directions with a larger check length to account for table height
        RaycastHit hitDown, hitUp;
        bool hitDownPlane = Physics.Raycast(anchorPos, Vector3.down, out hitDown, checkRayLength, planeLayer);
        bool hitUpPlane = Physics.Raycast(anchorPos, Vector3.up, out hitUp, checkRayLength, planeLayer);

        float minDistance = float.PositiveInfinity;

        // Calculate minimum distance from all successful hits
        if (hitDownPlane)
        {
            minDistance = Mathf.Min(minDistance, hitDown.distance);
        }
        if (hitUpPlane)
        {
            minDistance = Mathf.Min(minDistance, hitUp.distance);
        }

        // Also check with SphereCast for more forgiving detection
        if (Physics.SphereCast(anchorPos, 0.05f, Vector3.down, out RaycastHit sphereHitDown, checkRayLength, planeLayer))
        {
            minDistance = Mathf.Min(minDistance, sphereHitDown.distance);
        }
        if (Physics.SphereCast(anchorPos, 0.05f, Vector3.up, out RaycastHit sphereHitUp, checkRayLength, planeLayer))
        {
            minDistance = Mathf.Min(minDistance, sphereHitUp.distance);
        }

        // If we found any hit
        if (minDistance != float.PositiveInfinity)
        {
            distanceToPlane = minDistance;
            Debug.Log($"IsOnPlane: Found plane at distance: {distanceToPlane}");
            return true;
        }

        Debug.Log($"IsOnPlane: No plane found in any check. Anchor position: {anchorPos}");
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

    public void UpdateHandPose(XRHand hand)
    {
        currentHandPose = hand;
    }

    public XRHand GetCurrentHandPose()
    {
        return currentHandPose;
    }
}
