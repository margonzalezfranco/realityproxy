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

    private float _releaseTime = 0f;

    private GameObject labelObj;

    // Add a new field to reference the currently active (toggled on) anchor
    private static SceneObjectAnchor _currentActiveAnchor = null;
    
    // Add a method to set the currently active anchor when a toggle is turned on
    public static void SetCurrentActiveAnchor(SceneObjectAnchor anchor)
    {
        _currentActiveAnchor = anchor;
        Debug.Log($"Set current active anchor to: {(anchor != null ? anchor.label : "null")}");
    }
    
    // Add a method to check if there's an active anchor
    public static bool HasActiveAnchor()
    {
        return _currentActiveAnchor != null;
    }

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

    // Modify OnGeminiGrabbingDetected method to only work with the current active anchor
    private void OnGeminiGrabbingDetected(GrabbingInfo grabbingInfo)
    {
        // Only process if this is the correct hand
        bool isCorrectHand = grabbingInfo.grabbingHand == handType;
        
        // If onlyAllowLeftHandGrab is enabled, only the left hand can grab
        if (grabbingDetector != null && grabbingDetector.onlyAllowLeftHandGrab)
        {
            isCorrectHand = (handType == "left");
            
            if (handType == "right" && enableDebugLogging())
            {
                Debug.Log($"HandGrabTrigger: Right hand is ignoring explicit grab event because onlyAllowLeftHandGrab is enabled");
                return;
            }
        }
        
        if (!isCorrectHand)
        {
            if (enableDebugLogging())
            {
                Debug.Log($"HandGrabTrigger ({handType}): Ignoring grab event for {grabbingInfo.grabbingHand} hand");
            }
            return;
        }
        
        // If we're already holding something, check if it's the same object
        if (_grabbedAnchor != null)
        {
            if (_grabbedAnchor.label == grabbingInfo.grabbedObject)
            {
                // Already holding the correct object, nothing to do
                return;
            }
            else
            {
                // Release the current anchor before grabbing a new one
                ReleaseAnchor();
            }
        }
        
        // Don't process if confidence is too low
        if (grabbingInfo.confidence < confidenceThreshold)
        {
            return;
        }
        
        // Don't re-grab immediately after release unless some time has passed
        if (_justReleased && _lastReleasedAnchor != null && 
            _lastReleasedAnchor.label == grabbingInfo.grabbedObject && 
            Time.time - _releaseTime < 0.5f)
        {
            return;
        }
        
        // Reset the just released flag if we're grabbing a different object
        if (_lastReleasedAnchor != null && _lastReleasedAnchor.label != grabbingInfo.grabbedObject)
        {
            _justReleased = false;
        }

        // MODIFIED: Only work with the current active anchor, don't find by label
        SceneObjectAnchor anchorToGrab = _currentActiveAnchor;
        
        if (anchorToGrab != null)
        {
            // Check if this anchor is already being grabbed by another hand
            if (_allGrabbedAnchors.TryGetValue(anchorToGrab, out HandGrabTrigger otherHand))
            {
                if (otherHand != this)
                {
                    Debug.Log($"HandGrabTrigger: {handType} hand cannot grab anchor '{anchorToGrab.label}' because it's already grabbed by {otherHand.handType} hand");
                    return;
                }
                // If it's already grabbed by this hand, nothing to do
                return;
            }

            // Grab the anchor
            _grabbedAnchor = anchorToGrab;
            _allGrabbedAnchors[anchorToGrab] = this;
            
            // Add a lazy follow component to make the anchor follow the hand
            DualTargetLazyFollow lazyFollow = _grabbedAnchor.sphereObj.AddComponent<DualTargetLazyFollow>();
            lazyFollow.positionTarget = transform;
            lazyFollow.rotationTarget = Camera.main.transform;
            
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
            
            // Set the offset
            lazyFollow.targetOffset = grabOffset;

            // Initialize labelObj when we grab an anchor
            labelObj = _grabbedAnchor.sphereObj.transform.GetComponentInChildren<LookAtCamera>()?.gameObject;
            if (labelObj != null)   labelObj.SetActive(false);

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

            Debug.Log($"HandGrabTrigger: {handType} hand grabbed active anchor '{anchorToGrab.label}' using Gemini detection");
        }
        else
        {
            // No active anchor to grab
            Debug.Log("No current active anchor to grab. Make sure a toggle is in ON state.");
        }
    }

    // New method to handle Gemini grabbing updates (when the label changes)
    private void OnGeminiGrabbingUpdated(GrabbingInfo grabbingInfo)
    {
        // Only process if this is the correct hand
        bool isCorrectHand = grabbingInfo.grabbingHand == handType;
        
        // If onlyAllowLeftHandGrab is enabled, only the left hand can grab
        if (grabbingDetector != null && grabbingDetector.onlyAllowLeftHandGrab)
        {
            isCorrectHand = (handType == "left");
            
            if (handType == "right" && enableDebugLogging())
            {
                Debug.Log($"HandGrabTrigger: Right hand is ignoring grab update because onlyAllowLeftHandGrab is enabled");
                return;
            }
        }
        
        if (!isCorrectHand)
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
            Debug.Log($"HandGrabTrigger: {handType} hand releasing anchor '{_grabbedAnchor.label}' because Gemini explicitly detected release event");
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
    /// Continuously checks if Gemini still thinks we're grabbing the object.
    /// </summary>
    private void Update()
    {
        // Make sure we're subscribed to events
        if (!eventsSubscribed)
        {
            InitializeGrabbingDetector();
        }

        // MODIFIED: Only proceed with grabbing logic if there's an active anchor
        if (_currentActiveAnchor == null)
        {
            return;
        }

        // Get current grabbing info
        GrabbingInfo currentGrabInfo = grabbingDetector?.GetCurrentGrabbingInfo();
        
        // If we're currently holding an anchor
        if (_grabbedAnchor != null)
        {
            if (_twinObject != null)
            {
                UpdateTwinPosition();
            }

            // ONLY release if Gemini explicitly detects we're not grabbing anymore
            if (currentGrabInfo != null && !currentGrabInfo.isGrabbing)
            {
                // Gemini explicitly detected we're not grabbing anymore => release
                Debug.Log($"HandGrabTrigger: {handType} hand releasing anchor '{_grabbedAnchor.label}' because Gemini detected hand is no longer grabbing");
                ReleaseAnchor();
                _justReleased = true;
            }
        }
        // If we're not holding an anchor but Gemini thinks we should be grabbing
        else if (currentGrabInfo != null && currentGrabInfo.isGrabbing)
        {
            // Check if this is the correct hand (accounting for onlyAllowLeftHandGrab setting)
            bool isCorrectHand = currentGrabInfo.grabbingHand == handType;
            
            // If onlyAllowLeftHandGrab is enabled, only the left hand can grab
            if (grabbingDetector != null && grabbingDetector.onlyAllowLeftHandGrab)
            {
                isCorrectHand = (handType == "left");
                
                if (handType == "right" && enableDebugLogging())
                {
                    Debug.Log($"HandGrabTrigger: Right hand is ignoring grab command because onlyAllowLeftHandGrab is enabled");
                }
            }
            
            // Only proceed if this is the correct hand
            if (isCorrectHand)
            {
                // Try to find and grab the anchor if we're not in the cooldown period
                if (!_justReleased || Time.time - _releaseTime > 0.5f)
                {
                    _justReleased = false;
                    
                    // MODIFIED: Use the current active anchor instead of finding by label
                    SceneObjectAnchor anchorToGrab = _currentActiveAnchor;
                    
                    if (anchorToGrab != null)
                    {
                        // Check if this anchor is already being grabbed by another hand
                        if (!_allGrabbedAnchors.ContainsKey(anchorToGrab))
                        {
                            // Grab the anchor
                            _grabbedAnchor = anchorToGrab;
                            _allGrabbedAnchors[anchorToGrab] = this;
                            
                            // Add a lazy follow component to make the anchor follow the hand
                            DualTargetLazyFollow lazyFollow = _grabbedAnchor.sphereObj.AddComponent<DualTargetLazyFollow>();
                            lazyFollow.positionTarget = transform;
                            lazyFollow.rotationTarget = Camera.main.transform;
                            
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
                            
                            // Set the offset
                            lazyFollow.targetOffset = grabOffset;
                            
                            // Invoke the grab event
                            OnAnchorGrabbed?.Invoke(_grabbedAnchor);
                            
                            Debug.Log($"HandGrabTrigger: {handType} hand grabbed active anchor '{anchorToGrab.label}' based on Gemini detection");
                        }
                    }
                }
            }
        }
        else
        {
            // Reset the just released flag after a delay
            if (_justReleased && Time.time - _releaseTime > 0.5f)
            {
                _justReleased = false;
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

        // Safety check: Don't release if Gemini still thinks we're grabbing
        GrabbingInfo currentGrabInfo = grabbingDetector?.GetCurrentGrabbingInfo();
        if (currentGrabInfo != null && currentGrabInfo.isGrabbing && currentGrabInfo.grabbingHand == handType)
        {
            Debug.Log($"HandGrabTrigger: Prevented release of anchor '{_grabbedAnchor.label}' because Gemini still thinks {handType} hand is grabbing");
            return;
        }

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

        _grabbedAnchor.sphereObj.transform.localRotation = Quaternion.identity;

        // Invoke the release event before changing references
        OnAnchorReleased?.Invoke(_grabbedAnchor);

        // Enable the mesh renderer
        if (sphereMeshRenderer != null)
        {
            sphereMeshRenderer.enabled = true;
        }

        // Find and enable the label if we haven't stored a reference to it
        if (labelObj == null && _grabbedAnchor != null && _grabbedAnchor.sphereObj != null)
        {
            labelObj = _grabbedAnchor.sphereObj.transform.GetComponentInChildren<LookAtCamera>()?.gameObject;
        }
        
        // Enable the label
        if (labelObj != null)
        {
            labelObj.SetActive(true);
        }

        // Store the last released anchor and mark as just released
        _lastReleasedAnchor = _grabbedAnchor;
        _justReleased = true;
        _releaseTime = Time.time;

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
        labelObj = null;
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
