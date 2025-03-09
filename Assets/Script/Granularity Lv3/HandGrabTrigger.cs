using UnityEngine;
using System.Collections;
using UnityEngine.XR.Hands;  // Add this for XRHand
using Unity.XR.CoreUtils;    // Add this if needed for other XR utilities
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Put this script on each spawned hand (Left/Right).
/// It detects when we "grab" an anchor using Gemini vision model,
/// moves the anchor with the hand,
/// and "releases" once Gemini detects the hand is no longer grabbing.
/// </summary>
[RequireComponent(typeof(Collider))]
public class HandGrabTrigger : MonoBehaviour
{
    [Header("Hand Settings")]
    [Tooltip("Which hand this script is attached to (left or right)")]
    public string handType = "right"; // Default to right hand

    [Header("Grab Settings")]
    [Tooltip("Offset from hand center where the anchor should be positioned")]
    public Vector3 grabOffset = Vector3.zero;

    // The anchor currently grabbed (if any)
    private SceneObjectAnchor _grabbedAnchor = null;

    // We store the last anchor we released, so we don't immediately pick it back up
    private SceneObjectAnchor _lastReleasedAnchor = null;
    private bool _justReleased = false;

    // Static reference to track which anchors are currently grabbed by any hand
    private static System.Collections.Generic.Dictionary<SceneObjectAnchor, HandGrabTrigger> _allGrabbedAnchors = 
        new System.Collections.Generic.Dictionary<SceneObjectAnchor, HandGrabTrigger>();

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

    [Header("Gemini Grabbing Detection")]
    [Tooltip("Reference to the HandGrabbingDetector component")]
    public HandGrabbingDetector grabbingDetector;

    [Tooltip("Minimum confidence threshold for grabbing detection (0-1)")]
    public float confidenceThreshold = 0.7f;

    private bool eventsSubscribed = false;

    private void Start()
    {
        // Auto-detect which hand this is based on the GameObject name if not set
        if (string.IsNullOrEmpty(handType) || (handType != "left" && handType != "right"))
        {
            AutoDetectHandType();
        }
        
        // Get reference to ObjectMeshGenerator
        objectMeshGenerator = FindAnyObjectByType<ObjectMeshGenerator>();
        if (objectMeshGenerator == null)
        {
            Debug.LogWarning("No ObjectMeshGenerator found in scene!");
        }

        // Initialize the HandGrabbingDetector reference and subscribe to events
        InitializeGrabbingDetector();
        
        Debug.Log($"HandGrabTrigger initialized on {handType} hand: {gameObject.name}");
    }
    
    private void AutoDetectHandType()
    {
        string objName = gameObject.name.ToLower();
        
        if (objName.Contains("left"))
        {
            handType = "left";
        }
        else if (objName.Contains("right"))
        {
            handType = "right";
        }
        else
        {
            // Default to right if we can't determine
            handType = "right";
            Debug.LogWarning($"Could not determine hand type from name '{gameObject.name}', defaulting to 'right'");
        }
    }

    private void InitializeGrabbingDetector()
    {
        if (grabbingDetector == null)
        {
            grabbingDetector = FindAnyObjectByType<HandGrabbingDetector>();
        }

        if (grabbingDetector != null && !eventsSubscribed)
        {
            // Subscribe to all events
            grabbingDetector.OnGrabbingDetected += OnGeminiGrabbingDetected;
            grabbingDetector.OnGrabbingReleased += OnGeminiGrabbingReleased;
            grabbingDetector.OnGrabbingUpdated += OnGeminiGrabbingUpdated;
            
            // Register this hand with the detector
            if (handType == "left" && grabbingDetector.leftHandObject == null)
            {
                grabbingDetector.leftHandObject = this.gameObject;
                Debug.Log($"Registered as left hand with HandGrabbingDetector");
            }
            else if (handType == "right" && grabbingDetector.rightHandObject == null)
            {
                grabbingDetector.rightHandObject = this.gameObject;
                Debug.Log($"Registered as right hand with HandGrabbingDetector");
            }
            
            eventsSubscribed = true;
            Debug.Log("HandGrabTrigger: Successfully subscribed to HandGrabbingDetector events");
        }
        else if (grabbingDetector == null)
        {
            Debug.LogError("HandGrabTrigger: Failed to find HandGrabbingDetector! Please assign it in the inspector.");
        }
    }

    private void OnDestroy()
    {
        // Release any grabbed anchor when destroyed
        if (_grabbedAnchor != null)
        {
            ReleaseAnchor();
        }
        
        // Unsubscribe from events when destroyed
        if (grabbingDetector != null && eventsSubscribed)
        {
            grabbingDetector.OnGrabbingDetected -= OnGeminiGrabbingDetected;
            grabbingDetector.OnGrabbingReleased -= OnGeminiGrabbingReleased;
            grabbingDetector.OnGrabbingUpdated -= OnGeminiGrabbingUpdated;
            eventsSubscribed = false;
        }
    }

    // New method to handle Gemini grabbing detection
    private void OnGeminiGrabbingDetected(GrabbingInfo grabbingInfo)
    {
        // Only process if this is the correct hand
        if (grabbingInfo.grabbingHand != handType)
        {
            if (enableDebugLogging())
            {
                Debug.Log($"HandGrabTrigger ({handType}): Ignoring grab event for {grabbingInfo.grabbingHand} hand");
            }
            return;
        }
        
        // Only process if we're not already holding something and the confidence is high enough
        if (_grabbedAnchor != null || grabbingInfo.confidence < confidenceThreshold)
        {
            return;
        }

        // Find the anchor with the matching label
        SceneObjectAnchor anchorToGrab = FindAnchorByLabel(grabbingInfo.grabbedObject);
        
        if (anchorToGrab != null)
        {
            // Check if this anchor is already being grabbed by another hand
            if (_allGrabbedAnchors.TryGetValue(anchorToGrab, out HandGrabTrigger otherHand))
            {
                Debug.Log($"Anchor '{anchorToGrab.label}' is already being grabbed by {otherHand.handType} hand");
                
                // Force the other hand to release this anchor
                otherHand.ReleaseAnchor();
            }
            
            // Check if the anchor already has a DualTargetLazyFollow component
            DualTargetLazyFollow existingLazyFollow = anchorToGrab.sphereObj.GetComponent<DualTargetLazyFollow>();
            if (existingLazyFollow != null)
            {
                Debug.Log($"Removing existing DualTargetLazyFollow component from anchor '{anchorToGrab.label}'");
                Destroy(existingLazyFollow);
            }
            
            Debug.Log($"Gemini detected {handType} hand grabbing {grabbingInfo.grabbedObject} with {grabbingInfo.confidence:P0} confidence");
            
            // Grab this anchor
            _grabbedAnchor = anchorToGrab;
            
            // Register this anchor as grabbed by this hand
            _allGrabbedAnchors[_grabbedAnchor] = this;
            
            // Make the anchor a sibling of the hand instead of a child
            _grabbedAnchor.sphereObj.transform.SetParent(transform.parent);
            
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

            Debug.Log($"HandGrabTrigger: {handType} hand grabbed anchor '{anchorToGrab.label}' using Gemini detection");
        }
        else
        {
            Debug.LogWarning($"Could not find anchor with label: {grabbingInfo.grabbedObject}");
            
            // Debug all available anchors
            SceneObjectManager manager = FindAnyObjectByType<SceneObjectManager>();
            if (manager != null)
            {
                var anchors = manager.GetAllAnchors();
                if (anchors != null && anchors.Count > 0)
                {
                    Debug.Log($"Available anchors: {string.Join(", ", anchors.ConvertAll(a => a.label))}");
                }
                else
                {
                    Debug.LogWarning("No anchors found in scene!");
                }
            }
        }
    }

    // New method to handle Gemini grabbing updates (when the label changes)
    private void OnGeminiGrabbingUpdated(GrabbingInfo grabbingInfo)
    {
        // Only process if this is the correct hand
        if (grabbingInfo.grabbingHand != handType)
        {
            return;
        }
        
        // If we're already holding something but the label changed
        if (_grabbedAnchor != null && grabbingInfo.confidence >= confidenceThreshold)
        {
            // Check if the new label matches a different anchor
            SceneObjectAnchor newAnchor = FindAnchorByLabel(grabbingInfo.grabbedObject);
            
            if (newAnchor != null && newAnchor != _grabbedAnchor)
            {
                Debug.Log($"Gemini updated {handType} hand grabbing from '{_grabbedAnchor.label}' to '{newAnchor.label}'");
                
                // Release the current anchor
                ReleaseAnchor();
                
                // Grab the new anchor
                OnGeminiGrabbingDetected(grabbingInfo);
            }
        }
    }

    // New method to handle Gemini grabbing release
    private void OnGeminiGrabbingReleased()
    {
        // Only release if we're actually holding something
        if (_grabbedAnchor != null)
        {
            ReleaseAnchor();
        }
    }

    private SceneObjectAnchor FindAnchorByLabel(string label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return null;
        }
        
        // Find the SceneObjectManager instance
        SceneObjectManager manager = FindAnyObjectByType<SceneObjectManager>();
        if (manager != null)
        {
            var anchors = manager.GetAllAnchors();
            
            // First try exact match
            foreach (var anchor in anchors)
            {
                if (string.Equals(anchor.label, label, System.StringComparison.OrdinalIgnoreCase))
                {
                    return anchor;
                }
            }
            
            // If no exact match, try contains match
            foreach (var anchor in anchors)
            {
                if (anchor.label.ToLower().Contains(label.ToLower()) || 
                    label.ToLower().Contains(anchor.label.ToLower()))
                {
                    return anchor;
                }
            }
            
            // If still no match, try word-by-word match
            string[] labelWords = label.ToLower().Split(' ');
            foreach (var anchor in anchors)
            {
                string anchorLower = anchor.label.ToLower();
                foreach (string word in labelWords)
                {
                    if (word.Length > 3 && anchorLower.Contains(word))
                    {
                        return anchor;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Called when the hand's trigger collider intersects another collider.
    /// We check if it's an anchor's sphere, and if so, attempt to grab it
    /// if it's currently on-plane.
    /// </summary>
    /* Original OnTriggerEnter logic - commented out but preserved
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
    */

    /// <summary>
    /// Continuously checks if Gemini still thinks we're grabbing the object.
    /// </summary>
    private void Update()
    {
        // Make sure we're subscribed to events
        if (!eventsSubscribed)
        {
            InitializeGrabbingDetector();
        }

        // If we're currently holding an anchor, update its position
        if (_grabbedAnchor != null)
        {
            if (_twinObject != null)
            {
                UpdateTwinPosition();
            }

            // Check if Gemini thinks we're still grabbing
            GrabbingInfo currentGrabInfo = grabbingDetector?.GetCurrentGrabbingInfo();
            if (currentGrabInfo == null || !currentGrabInfo.isGrabbing || currentGrabInfo.grabbingHand != handType)
            {
                // Gemini thinks we're not grabbing anymore => release
                ReleaseAnchor();
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

        Debug.Log($"HandGrabTrigger: {handType} hand released anchor '{_grabbedAnchor.label}'");

        // Remove from the static dictionary of grabbed anchors
        if (_allGrabbedAnchors.ContainsKey(_grabbedAnchor))
        {
            _allGrabbedAnchors.Remove(_grabbedAnchor);
        }

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
    
    private bool enableDebugLogging()
    {
        return grabbingDetector != null && grabbingDetector.enableDebugLogging;
    }
}
