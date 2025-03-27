using UnityEngine;
using System.Collections;
using System.Collections.Generic;  // Add this for HashSet<>
using UnityEngine.XR.Hands;  // Add this for XRHand
using Unity.XR.CoreUtils;    // Add this if needed for other XR utilities
using UnityEngine.XR.Interaction.Toolkit.UI;
using PolySpatial.Template;

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

    [SerializeField]
    private GameObject objectTrackingToggle;

    [SerializeField]
    private SpatialUIToggle objectTrackingUIToggle;
    
    // Add flag to track if the current grab is manual
    private bool _isManualGrab = false;
    
    // Add flag to prevent auto-grabbing after manual release
    private bool _preventAutoGrabAfterManualRelease = false;
    
    // Add a method to set the currently active anchor when a toggle is turned on
    public static void SetCurrentActiveAnchor(SceneObjectAnchor anchor)
    {
        _currentActiveAnchor = anchor;
        Debug.Log($"Set current active anchor to: {(anchor != null ? anchor.label : "null")}");
    }
    
    // Add method to allow toggle to reset the prevent auto grab flag
    public void AllowAutoGrab()
    {
        _preventAutoGrabAfterManualRelease = false;
        Debug.Log($"HandGrabTrigger: {handType} hand allowing auto grab again");
    }

    // Add a method to check if there's an active anchor
    public static bool HasActiveAnchor()
    {
        return _currentActiveAnchor != null;
    }

    [Header("Proximity Relationship Detection")]
    [Tooltip("Distance threshold for detecting proximity to other anchors")]
    public float proximityThreshold = 0.15f; // 15cm default

    [Tooltip("Minimum time between proximity relationship checks")]
    public float proximityCheckInterval = 1.0f; // 1 second default

    [Tooltip("Material to use for proximity highlight effect")]
    public Material proximityHighlightMaterial;

    [Tooltip("Default material to restore when highlight ends")]
    public Material sphereMaterial;

    [Tooltip("Duration of the highlight effect in seconds")]
    public float highlightDuration = 0.5f;

    [Tooltip("Sound to play when objects come into proximity")]
    public AudioClip proximitySound;

    [Tooltip("Volume of the proximity sound")]
    [Range(0f, 1f)]
    public float proximitySoundVolume = 0.5f;

    private float lastProximityCheckTime = 0f;
    private SceneObjectManager sceneObjectManager;
    private SphereToggleScript sphereToggleScript;
    private HashSet<SceneObjectAnchor> recentlyHighlighted = new HashSet<SceneObjectAnchor>();
    private AudioSource audioSource;

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

        // Find the SceneObjectManager
        sceneObjectManager = FindAnyObjectByType<SceneObjectManager>();
        if (sceneObjectManager == null)
        {
            Debug.LogWarning("No SceneObjectManager found in scene!");
        }

        // Setup audio source if needed
        SetupAudioSource();

        // Initialize the HandGrabbingDetector reference and subscribe to events
        InitializeGrabbingDetector();
        
        Debug.Log($"HandGrabTrigger initialized on {handType} hand: {gameObject.name}");

        if (objectTrackingToggle == null) objectTrackingToggle = GameObject.Find("ObjectTrackingToggle");
        if (objectTrackingUIToggle == null) objectTrackingUIToggle = objectTrackingToggle.GetComponent<SpatialUIToggle>();
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

    private void SetupAudioSource()
    {
        // Check if we already have an AudioSource
        audioSource = GetComponent<AudioSource>();
        
        // If not, add one
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 1.0f; // Full 3D sound
            audioSource.minDistance = 0.1f;
            audioSource.maxDistance = 10.0f;
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
            ReleaseAnchor(true); // Use manual release when destroying component
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
                ReleaseAnchor(false); // Normal release for automatic Gemini detection
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

            _grabbedAnchor.sphereObj.GetComponent<LazyFollow>().enabled = false;
            DualTargetLazyFollow lazyFollow = _grabbedAnchor.sphereObj.AddComponent<DualTargetLazyFollow>();
            ConfigureDualTargetLazyFollow(lazyFollow);

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

            if (objectTrackingUIToggle != null) objectTrackingUIToggle.PassiveToggleWithoutInvokeOn();

            Debug.Log($"HandGrabTrigger: {handType} hand grabbed active anchor '{anchorToGrab.label}' using Gemini detection");
        }
        else
        {
            // No active anchor to grab
            Debug.Log("No current active anchor to grab. Make sure a toggle is in ON state.");
        }
    }

    public void ManualGrabAnchor()
    {
        // Check if there's an active anchor to grab
        if (_currentActiveAnchor == null)
        {
            Debug.Log("No current active anchor to grab. Make sure a toggle is in ON state.");
            return;
        }

        // Reset the auto-grab prevention flag since this is an explicit manual grab
        _preventAutoGrabAfterManualRelease = false;

        // Check if we're already holding something
        if (_grabbedAnchor != null)
        {
            if (_grabbedAnchor == _currentActiveAnchor)
            {
                // Already holding the correct anchor, nothing to do
                return;
            }
            else
            {
                // Release the current anchor before grabbing a new one
                // Use manual release to bypass Gemini checks
                ReleaseAnchor(true);
            }
        }

        // Check if onlyAllowLeftHandGrab is enabled
        if (grabbingDetector != null && grabbingDetector.onlyAllowLeftHandGrab && handType != "left")
        {
            Debug.Log($"HandGrabTrigger: Right hand cannot manually grab because onlyAllowLeftHandGrab is enabled");
            return;
        }

        // Check if this anchor is already being grabbed by another hand
        if (_allGrabbedAnchors.TryGetValue(_currentActiveAnchor, out HandGrabTrigger otherHand))
        {
            if (otherHand != this)
            {
                Debug.Log($"HandGrabTrigger: {handType} hand cannot grab anchor '{_currentActiveAnchor.label}' because it's already grabbed by {otherHand.handType} hand");
                return;
            }
            // If it's already grabbed by this hand, nothing to do
            return;
        }

        // Grab the anchor
        _grabbedAnchor = _currentActiveAnchor;
        _allGrabbedAnchors[_currentActiveAnchor] = this;
        
        // Set the manual grab flag to true
        _isManualGrab = true;

        // Disable the original lazy follow and add our dual target lazy follow
        _grabbedAnchor.sphereObj.GetComponent<LazyFollow>().enabled = false;
        DualTargetLazyFollow lazyFollow = _grabbedAnchor.sphereObj.AddComponent<DualTargetLazyFollow>();
        ConfigureDualTargetLazyFollow(lazyFollow);

        // Hide the label and sphere mesh
        labelObj = _grabbedAnchor.sphereObj.transform.GetComponentInChildren<LookAtCamera>()?.gameObject;
        if (labelObj != null) labelObj.SetActive(false);

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

        Debug.Log($"HandGrabTrigger: {handType} hand manually grabbed active anchor '{_currentActiveAnchor.label}'");
    }

    private void ConfigureDualTargetLazyFollow(DualTargetLazyFollow lazyFollow)
    {
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
                ReleaseAnchor(false); // Normal release for automatic Gemini detection
                
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
            ReleaseAnchor(true); // Use manual release for explicit Gemini release detection
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

            // Check for proximity to other anchors
            CheckProximityToOtherAnchors();

            // ONLY release if Gemini explicitly detects we're not grabbing anymore
            // BUT ignore Gemini's detection if this is a manual grab
            if (!_isManualGrab && currentGrabInfo != null && !currentGrabInfo.isGrabbing)
            {
                // Gemini explicitly detected we're not grabbing anymore => release
                Debug.Log($"HandGrabTrigger: {handType} hand releasing anchor '{_grabbedAnchor.label}' because Gemini detected hand is no longer grabbing");
                ReleaseAnchor(true); // Use manual release for explicit Gemini release detection
                _justReleased = true;
            }
        }
        // If we're not holding an anchor but Gemini thinks we should be grabbing
        // AND we haven't prevented auto-grabbing due to a manual release
        else if (currentGrabInfo != null && currentGrabInfo.isGrabbing && !_preventAutoGrabAfterManualRelease)
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

                            // Set the toggle to active
                            if (objectTrackingUIToggle != null) objectTrackingUIToggle.PassiveToggleWithoutInvokeOn();
                                                
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
    public void ReleaseAnchor(bool isManualRelease = false)
    {
        if (_grabbedAnchor == null)
            return;

        // Safety check: Don't release if Gemini still thinks we're grabbing
        // Skip this check if it's a manual release from the toggle
        GrabbingInfo currentGrabInfo = grabbingDetector?.GetCurrentGrabbingInfo();
        if (!isManualRelease && currentGrabInfo != null && currentGrabInfo.isGrabbing && currentGrabInfo.grabbingHand == handType)
        {
            Debug.Log($"HandGrabTrigger: Prevented release of anchor '{_grabbedAnchor.label}' because Gemini still thinks {handType} hand is grabbing");
            return;
        }

        Debug.Log($"HandGrabTrigger: {handType} hand released anchor '{_grabbedAnchor.label}'{(isManualRelease ? " (manual release)" : "")}");

        // If this is a manual release, prevent auto grabbing until explicitly allowed
        if (isManualRelease)
        {
            _preventAutoGrabAfterManualRelease = true;
            Debug.Log($"HandGrabTrigger: Preventing auto-grab after manual release");
        }

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

        // Set the toggle to inactive
        if (objectTrackingUIToggle != null) objectTrackingUIToggle.PassiveToggleWithoutInvokeOff();

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
        
        // Reset the manual grab flag
        _isManualGrab = false;
        
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

    /// <summary>
    /// Checks if the grabbed anchor is near any other anchors in the scene
    /// and generates relationship information if they are close enough.
    /// </summary>
    private void CheckProximityToOtherAnchors()
    {
        // Only check at the specified interval to avoid performance issues
        if (Time.time - lastProximityCheckTime < proximityCheckInterval || sceneObjectManager == null || _grabbedAnchor == null)
        {
            return;
        }

        lastProximityCheckTime = Time.time;
        
        // Get all anchors from SceneObjectManager
        var allAnchors = sceneObjectManager.GetAllAnchors();
        if (allAnchors == null || allAnchors.Count <= 1)
        {
            return;
        }
        
        // Get the position of our grabbed anchor
        Vector3 grabbedAnchorPos = _grabbedAnchor.sphereObj.transform.position;
        string grabbedLabel = _grabbedAnchor.label;
        
        // Make a copy of the recently highlighted list to avoid modification during iteration
        HashSet<SceneObjectAnchor> stillHighlighted = new HashSet<SceneObjectAnchor>();
        
        // Check distance to each other anchor
        foreach (var otherAnchor in allAnchors)
        {
            // Skip if it's the same anchor we're grabbing
            if (otherAnchor == _grabbedAnchor)
            {
                continue;
            }
            
            // Calculate distance
            float distance = Vector3.Distance(grabbedAnchorPos, otherAnchor.sphereObj.transform.position);
            
            // If within threshold, generate relationship
            if (distance <= proximityThreshold)
            {
                // Skip if we recently highlighted this anchor
                if (recentlyHighlighted.Contains(otherAnchor))
                {
                    // Keep track that this anchor is still within range
                    stillHighlighted.Add(otherAnchor);
                    continue;
                }
                
                // Add to recently highlighted set
                recentlyHighlighted.Add(otherAnchor);
                stillHighlighted.Add(otherAnchor);
                
                Debug.Log($"Proximity detected between '{grabbedLabel}' and '{otherAnchor.label}' (distance: {distance}m)");
                
                // Highlight the nearby object
                StartCoroutine(HighlightAnchorBriefly(otherAnchor));
                
                // Play proximity sound
                PlayProximitySound();
                
                // Get SphereToggleScript if we don't have it yet
                if (sphereToggleScript == null)
                {
                    sphereToggleScript = _grabbedAnchor.sphereObj.GetComponent<SphereToggleScript>();
                    if (sphereToggleScript == null)
                    {
                        Debug.LogWarning("No SphereToggleScript found on grabbed anchor!");
                        return;
                    }
                }
                
                // Generate relationship between the two objects
                sphereToggleScript.GenerateProximityRelationship(otherAnchor.label);
                
                // Only generate one relationship at a time to avoid overwhelming the user
                // and to prevent multiple concurrent API calls
                break;
            }
            else if (recentlyHighlighted.Contains(otherAnchor))
            {
                // This anchor was previously highlighted but is now out of range
                // Reset its appearance immediately
                ResetAnchorHighlight(otherAnchor);
                Debug.Log($"Anchor '{otherAnchor.label}' moved out of proximity range, resetting highlight");
            }
        }
        
        // Update the recently highlighted set to only include anchors still within range
        recentlyHighlighted = stillHighlighted;
    }

    /// <summary>
    /// Immediately resets an anchor's highlight without waiting for the coroutine
    /// </summary>
    private void ResetAnchorHighlight(SceneObjectAnchor anchor)
    {
        var renderer = anchor.sphereObj.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            return;
        }
        
        // Check if the current color is green (our highlight color)
        bool isCurrentlyGreen = false;
        foreach (Material mat in renderer.materials)
        {
            if (mat.HasProperty("_Color") && mat.GetColor("_Color") == Color.green)
            {
                isCurrentlyGreen = true;
                break;
            }
        }
        
        // Only reset if it's currently highlighted
        if (isCurrentlyGreen || proximityHighlightMaterial != null)
        {
            // Reset to original material
            Material defaultMaterial = sphereMaterial;
            if (defaultMaterial != null)
            {
                renderer.material = defaultMaterial;
            }
            else
            {
                // If no default material is available, reset color to white
                foreach (Material mat in renderer.materials)
                {
                    if (mat.HasProperty("_Color"))
                    {
                        mat.SetColor("_Color", Color.white);
                    }
                }
            }
            
            Debug.Log($"Reset highlight on anchor '{anchor.label}' from green to original material");
        }
    }

    /// <summary>
    /// Highlights an anchor briefly to indicate proximity interaction
    /// </summary>
    private IEnumerator HighlightAnchorBriefly(SceneObjectAnchor anchor)
    {
        // Store original materials
        var renderer = anchor.sphereObj.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            yield break;
        }
        
        Material[] originalMaterials = renderer.materials;
        
        // Apply highlight material if available
        if (proximityHighlightMaterial != null)
        {
            Material[] highlightMaterials = new Material[originalMaterials.Length];
            for (int i = 0; i < highlightMaterials.Length; i++)
            {
                highlightMaterials[i] = proximityHighlightMaterial;
            }
            renderer.materials = highlightMaterials;
        }
        else
        {
            // If no highlight material provided, modify the existing material's color to green
            foreach (Material mat in renderer.materials)
            {
                if (mat.HasProperty("_Color"))
                {
                    mat.SetColor("_Color", Color.green);
                }
            }
        }
        
        // Wait for the highlight duration
        yield return new WaitForSeconds(highlightDuration);
        
        // Restore original materials only if the anchor is still in the highlighted set
        // This prevents conflicts with the main proximity check
        if (!recentlyHighlighted.Contains(anchor))
        {
            renderer.materials = originalMaterials;
        }
    }

    /// <summary>
    /// Plays a sound effect when objects come into proximity
    /// </summary>
    private void PlayProximitySound()
    {
        if (audioSource == null || proximitySound == null)
        {
            return;
        }
        
        // Set the clip and volume
        audioSource.clip = proximitySound;
        audioSource.volume = proximitySoundVolume;
        
        // Play the sound
        audioSource.Play();
    }
}
