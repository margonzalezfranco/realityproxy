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
    [Tooltip("Distance threshold for detecting proximity to other anchors (activation distance)")]
    public float proximityThreshold = 0.5f; // 50cm default - activation distance

    [Tooltip("Distance threshold for maintaining an already established relationship (deactivation distance)")]
    public float maintainRelationshipThreshold = 0.8f; // 80cm default - larger than activation to create hysteresis

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

    [Header("Hand-to-Anchor Proximity")]
    [Tooltip("Distance threshold for detecting when hand is close to any anchor")]
    public float proximityThresholdFromHandToAnchor = 0.15f; // 30cm default

    private float lastProximityCheckTime = 0f;
    private SceneObjectManager sceneObjectManager;
    private SphereToggleScript sphereToggleScript;
    private HashSet<SceneObjectAnchor> recentlyHighlighted = new HashSet<SceneObjectAnchor>();
    private AudioSource audioSource;

    // Static variable to track if any hand is near any anchor
    private static bool isAnyHandNearAnyAnchor = false;
    // Static variable to track last time the toggle visibility changed
    private static float lastToggleVisibilityChangeTime = 0f;
    // Hysteresis time to prevent rapid visibility changes
    private static float toggleVisibilityHysteresis = 0.5f;

    private float lastDetectionStartTime = 0f;

    [Header("Proximity Selection Settings")]
    [Tooltip("Hysteresis threshold to prevent switching between similarly distanced objects")]
    public float proximityHysteresis = 0.05f; // 5cm default

    // Track the currently active relationship anchor
    private SceneObjectAnchor currentRelationshipAnchor = null;
    private float timeStartedRelationship = 0f;
    private float minRelationshipDuration = 1.5f; // Minimum time to keep a relationship before switching

    [Header("User Study Logging")]
    [SerializeField] private bool enableUserStudyLogging = true;

    [Header("Directional Aiming Relationship Detection")]
    [Tooltip("Maximum angle in degrees to consider an object as being aimed at")]
    public float maxAimAngle = 30f; // Objects within this angular threshold can be selected

    [Tooltip("Minimum angle change in degrees needed to switch to a new target (hysteresis)")]
    public float aimHysteresis = 5f; // Prevent rapid switching between targets

    [Tooltip("Maximum detection distance for aiming (set to very large for infinite)")]
    public float maxAimDistance = 100f; // Can aim at objects at any distance within view

    [Tooltip("Minimum time between aim relationship checks")]
    public float aimCheckInterval = 0.2f; // Check more frequently than proximity since aiming is more responsive

    [Tooltip("Minimum time to keep a relationship before switching to a new target")]
    public float minAimRelationshipDuration = 1.0f; // Seconds

    [Header("Aiming Visualization")]
    [Tooltip("Enable visualization of the aiming direction")]
    public bool visualizeAiming = true;

    [Tooltip("Line renderer prefab for aiming visualization")]
    public GameObject aimingVisualizerPrefab;

    [Tooltip("Color of the aiming visualization (alpha will be adjusted automatically)")]
    public Color aimingColor = Color.white;

    [Tooltip("Width of the aiming visualization line")]
    public float aimingLineWidth = 0.005f;

    [Tooltip("Length of the aiming visualization line")]
    public float aimingLineLength = 2.0f;

    // Add a new field to track the current aiming direction
    private Vector3 _currentAimDirection;

    // Reference to the aiming visualizer line renderer
    private LineRenderer _aimingVisualizer;

    // Add these private fields after the existing toggle references
    [SerializeField]
    private GameObject relationToggle;

    [SerializeField]
    private GameObject questionToggle;

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
        
        // Find the relation and question toggles if they're not assigned
        if (relationToggle == null) relationToggle = GameObject.Find("RelationToggle");
        if (questionToggle == null) questionToggle = GameObject.Find("QuestionToggle");
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

        // Clean up the aiming visualizer if it exists
        if (_aimingVisualizer != null)
        {
            Destroy(_aimingVisualizer.gameObject);
            _aimingVisualizer = null;
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
            
            // Hide relation and question toggles
            ActivateRelationAndQuestionToggles(false);
            
            // Log the auto-grab event for user study
            LogUserStudy($"[GRAB] AUTO_GRAB: Hand=\"{handType}\", Object=\"{anchorToGrab.label}\", Confidence={grabbingInfo.confidence:F2}");

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

    public void ActivateRelationAndQuestionToggles(bool activate)
    {
        if (relationToggle != null) relationToggle.SetActive(activate);
        if (questionToggle != null) questionToggle.SetActive(activate);
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

        // Hide relation and question toggles
        ActivateRelationAndQuestionToggles(false);

        // Clear the sphereToggleScript reference and get a new one
        sphereToggleScript = null;
        sphereToggleScript = _grabbedAnchor.sphereObj.GetComponent<SphereToggleScript>();

        // Generate twin object
        if (objectMeshGenerator != null)
        {
            StartCoroutine(objectMeshGenerator.EstimateAndGenerateObject(null, (generatedObj) => {
                _twinObject = generatedObj;
                _twinObject.transform.SetParent(transform.parent);
                UpdateTwinPosition();
            }));
        }
        
        // Log the manual grab event for user study
        LogUserStudy($"[GRAB] MANUAL_GRAB: Hand=\"{handType}\", Object=\"{_currentActiveAnchor.label}\"");

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
        
        // Initialize aiming direction from camera
        _currentAimDirection = Camera.main?.transform.forward ?? Vector3.forward;
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
            
            // Log the auto-release event for user study
            LogUserStudy($"[GRAB] AUTO_RELEASE: Hand=\"{handType}\", Object=\"{_grabbedAnchor.label}\", DetectionType=\"explicit\"");
            
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

        // Check if either hand is near any anchor
        CheckHandProximityToAnchors();

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
            
            // Update the aiming direction before checking relationships
            UpdateAimingDirection();

            // Check for directional aiming to other anchors
            CheckDirectionalAimingToOtherAnchors();

            // Add recovery mechanism for tracking loss
            if (!_isManualGrab && currentGrabInfo != null)
            {
                // If we detect a temporary tracking loss, try to recover
                if (!currentGrabInfo.isGrabbing && Time.time - lastDetectionStartTime < 1.0f)
                {
                    // Try to recover the grab state
                    if (enableDebugLogging())
                    {
                        Debug.Log($"HandGrabTrigger: {handType} hand detected temporary tracking loss, attempting recovery");
                    }
                    
                    // Force a new detection
                    grabbingDetector?.TriggerDetection();
                    return;
                }
                
                // Only release if we're sure we're not grabbing anymore
                if (!currentGrabInfo.isGrabbing && Time.time - lastDetectionStartTime >= 1.0f)
                {
                    // Gemini explicitly detected we're not grabbing anymore => release
                    Debug.Log($"HandGrabTrigger: {handType} hand releasing anchor '{_grabbedAnchor.label}' because Gemini detected hand is no longer grabbing");
                    ReleaseAnchor(true); // Use manual release for explicit Gemini release detection
                    _justReleased = true;
                }
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
                            
                            // Configure following parameters with more lenient settings
                            lazyFollow.movementSpeed = 8f; // Reduced from 12f for smoother movement
                            lazyFollow.movementSpeedVariancePercentage = 0.15f; // Reduced from 0.25f for more stability
                            lazyFollow.minDistanceAllowed = 0.03f; // Increased from 0.02f for more tolerance
                            lazyFollow.maxDistanceAllowed = 0.15f; // Increased from 0.1f for more range
                            lazyFollow.timeUntilThresholdReachesMaxDistance = 0.7f; // Increased from 0.5f for smoother transitions
                            
                            // Configure rotation parameters with more lenient settings
                            lazyFollow.minAngleAllowed = 3f; // Increased from 2f for more tolerance
                            lazyFollow.maxAngleAllowed = 15f; // Increased from 10f for more range
                            lazyFollow.timeUntilThresholdReachesMaxAngle = 0.7f; // Increased from 0.5f for smoother transitions
                            
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
        
        // Log the release event for user study
        if (isManualRelease && _isManualGrab)
        {
            LogUserStudy($"[GRAB] MANUAL_RELEASE: Hand=\"{handType}\", Object=\"{_grabbedAnchor.label}\"");
        }
        else if (isManualRelease && !_isManualGrab)
        {
            LogUserStudy($"[GRAB] FORCED_RELEASE: Hand=\"{handType}\", Object=\"{_grabbedAnchor.label}\"");
        }
        else
        {
            LogUserStudy($"[GRAB] AUTO_RELEASE: Hand=\"{handType}\", Object=\"{_grabbedAnchor.label}\", DetectionType=\"timeout\"");
        }

        // If this is a manual release, prevent auto grabbing until explicitly allowed
        if (isManualRelease)
        {
            _preventAutoGrabAfterManualRelease = true;
            Debug.Log($"HandGrabTrigger: Preventing auto-grab after manual release");
        }

        // Show relation and question toggles again
        ActivateRelationAndQuestionToggles(true);

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

        // Clear any active relationship if there's a current relationship anchor
        if (currentRelationshipAnchor != null)
        {
            // Get the sphereToggleScript if we haven't already
            if (sphereToggleScript == null)
            {
                sphereToggleScript = _grabbedAnchor.sphereObj.GetComponent<SphereToggleScript>();
            }
            
            // Clear the relationship line
            if (sphereToggleScript != null && sphereToggleScript.relationLineManager != null)
            {
                Debug.Log($"Clearing relationship line with '{currentRelationshipAnchor.label}' due to anchor release");
                sphereToggleScript.ClearSpecificRelationship(currentRelationshipAnchor.label);
            }
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
        
        // Hide the aiming visualizer
        if (_aimingVisualizer != null)
        {
            _aimingVisualizer.gameObject.SetActive(false);
        }
        
        // Clear references
        sphereToggleScript = null;
        currentRelationshipAnchor = null;
        timeStartedRelationship = 0f;
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
    /// Checks if the grabbed anchor is aimed towards any other anchors in the scene
    /// and generates relationship information based on which anchor is most directly aligned.
    /// </summary>
    private void CheckDirectionalAimingToOtherAnchors()
    {
        // Only check at the specified interval to avoid performance issues
        if (Time.time - lastProximityCheckTime < aimCheckInterval || sceneObjectManager == null || _grabbedAnchor == null)
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
        
        // Get the position and forward direction of our grabbed anchor
        Vector3 grabbedAnchorPos = _grabbedAnchor.sphereObj.transform.position;
        Vector3 aimDirection = _currentAimDirection;
        string grabbedLabel = _grabbedAnchor.label;
        
        // Make a copy of the recently highlighted list to avoid modification during iteration
        HashSet<SceneObjectAnchor> stillHighlighted = new HashSet<SceneObjectAnchor>();
        HashSet<SceneObjectAnchor> toDeselect = new HashSet<SceneObjectAnchor>(recentlyHighlighted);
        
        // First find the closest anchor in terms of angular alignment
        SceneObjectAnchor bestAlignedAnchor = null;
        float smallestAngle = maxAimAngle; // Only consider objects within maxAimAngle
        
        // Check angular alignment to each other anchor
        foreach (var otherAnchor in allAnchors)
        {
            // Skip if it's the same anchor we're grabbing
            if (otherAnchor == _grabbedAnchor)
            {
                continue;
            }
            
            // Calculate direction vector to this anchor
            Vector3 directionToAnchor = otherAnchor.sphereObj.transform.position - grabbedAnchorPos;
            
            // Skip if the anchor is too far away (optional distance limit)
            float distance = directionToAnchor.magnitude;
            if (distance > maxAimDistance)
            {
                continue;
            }
            
            // Calculate the angle between our forward direction and the direction to this anchor
            float angle = Vector3.Angle(aimDirection, directionToAnchor);
            
            // Check if this anchor could be a candidate for relationship
            bool isCandidate = false;
            
            // Determine if this anchor is a candidate based on aiming angle
            if (otherAnchor == currentRelationshipAnchor)
            {
                // If this is our current relationship, add some hysteresis to the max angle
                isCandidate = angle <= (maxAimAngle + aimHysteresis);
                
                // Even if it's not the best candidate anymore, keep it highlighted if within range
                if (angle <= (maxAimAngle + aimHysteresis))
                {
                    stillHighlighted.Add(otherAnchor);
                    toDeselect.Remove(otherAnchor);
                }
            }
            else
            {
                // For new potential relationships, use the standard max angle
                isCandidate = angle <= maxAimAngle;
            }
            
            // Debug information about angles
            if (enableDebugLogging() && angle <= maxAimAngle * 1.5f)
            {
                Debug.Log($"Aiming check: Anchor '{otherAnchor.label}' at angle {angle:F1}° (distance {distance:F2}m)");
            }
            
            // If it's a candidate and more aligned than current best, update
            if (isCandidate && angle < smallestAngle)
            {
                smallestAngle = angle;
                bestAlignedAnchor = otherAnchor;
            }
            
            // If this anchor is beyond the aiming threshold and was highlighted, reset it immediately
            if (angle > (maxAimAngle + aimHysteresis) && recentlyHighlighted.Contains(otherAnchor))
            {
                // This anchor was previously highlighted but is now out of aim
                // Reset its appearance immediately
                ResetAnchorHighlight(otherAnchor);
                Debug.Log($"Anchor '{otherAnchor.label}' moved outside aiming angle ({angle:F1}°), clearing relationship");
                
                // Log the relationship end for user study
                LogUserStudy($"[GRAB] AIMING_RELATIONSHIP_ENDED: Held=\"{_grabbedAnchor.label}\", Other=\"{otherAnchor.label}\", Angle={angle:F1}°, Reason=\"angle_exceeded\"");
                
                // Clear the relationship line explicitly if this was our active relationship
                if (otherAnchor == currentRelationshipAnchor)
                {
                    if (sphereToggleScript == null)
                    {
                        sphereToggleScript = _grabbedAnchor.sphereObj.GetComponent<SphereToggleScript>();
                    }
                    
                    if (sphereToggleScript != null && sphereToggleScript.relationLineManager != null)
                    {
                        // Clear relationship specifically with this anchor
                        sphereToggleScript.ClearSpecificRelationship(otherAnchor.label);
                        currentRelationshipAnchor = null;
                        timeStartedRelationship = 0f;
                    }
                }
            }
        }
        
        // Now determine if we should establish a new relationship or maintain the current one
        if (bestAlignedAnchor != null)
        {
            // Calculate angle to best aligned anchor
            Vector3 directionToBest = bestAlignedAnchor.sphereObj.transform.position - grabbedAnchorPos;
            float angleToClosest = Vector3.Angle(aimDirection, directionToBest);
            
            // Update the aiming visualizer color based on whether we have a target in range
            if (_aimingVisualizer != null && visualizeAiming)
            {
                // If aligned with a target, use white with 70% alpha, otherwise use white with 40% alpha
                float alphaValue = (angleToClosest < maxAimAngle * 0.6f) ? 0.7f : 0.4f;
                _aimingVisualizer.startColor = new Color(1f, 1f, 1f, alphaValue);
                _aimingVisualizer.endColor = new Color(1f, 1f, 1f, 0f); // Fade out to transparent
                
                // Optionally make the line thicker when aligned
                _aimingVisualizer.startWidth = (angleToClosest < maxAimAngle * 0.6f) ? 
                    aimingLineWidth * 1.5f : aimingLineWidth;
                _aimingVisualizer.endWidth = (angleToClosest < maxAimAngle * 0.6f) ? 
                    aimingLineWidth * 0.75f : aimingLineWidth * 0.5f;
            }
            
            // Always add best aligned to stillHighlighted
            stillHighlighted.Add(bestAlignedAnchor);
            toDeselect.Remove(bestAlignedAnchor);
            
            bool shouldEstablishNewRelationship = false;
            
            // Case 1: No current relationship exists
            if (currentRelationshipAnchor == null)
            {
                shouldEstablishNewRelationship = true;
                Debug.Log($"Establishing new relationship with best aligned anchor: '{bestAlignedAnchor.label}' at angle {angleToClosest:F1}°");
                
                // Log aiming relationship start for user study
                LogUserStudy($"[GRAB] AIMING_RELATIONSHIP_STARTED: Held=\"{_grabbedAnchor.label}\", Other=\"{bestAlignedAnchor.label}\", Angle={angleToClosest:F1}°");
            }
            // Case 2: Current relationship anchor is no longer within aiming threshold
            else if (!allAnchors.Contains(currentRelationshipAnchor))
            {
                shouldEstablishNewRelationship = true;
                Debug.Log($"Current relationship anchor no longer exists, switching to '{bestAlignedAnchor.label}'");
                
                // Log switching aiming relationship for user study
                LogUserStudy($"[GRAB] AIMING_RELATIONSHIP_SWITCHED: Held=\"{_grabbedAnchor.label}\", OldTarget=\"{currentRelationshipAnchor.label}\", NewTarget=\"{bestAlignedAnchor.label}\", Angle={angleToClosest:F1}°, Reason=\"target_missing\"");
            }
            // Case 3: Current relationship is out of aiming angle
            else
            {
                Vector3 directionToCurrent = currentRelationshipAnchor.sphereObj.transform.position - grabbedAnchorPos;
                float angleToCurrent = Vector3.Angle(aimDirection, directionToCurrent);
                
                if (angleToCurrent > (maxAimAngle + aimHysteresis))
                {
                    shouldEstablishNewRelationship = true;
                    Debug.Log($"Current relationship anchor '{currentRelationshipAnchor.label}' beyond aiming threshold ({angleToCurrent:F1}°), switching to '{bestAlignedAnchor.label}' ({angleToClosest:F1}°)");
                    
                    // Log switching aiming relationship for user study
                    LogUserStudy($"[GRAB] AIMING_RELATIONSHIP_SWITCHED: Held=\"{_grabbedAnchor.label}\", OldTarget=\"{currentRelationshipAnchor.label}\", NewTarget=\"{bestAlignedAnchor.label}\", Angle={angleToClosest:F1}°, Reason=\"angle_exceeded\"");
                }
                // Case 4: Found a significantly better aligned anchor AND minimum duration has passed
                else if (currentRelationshipAnchor != bestAlignedAnchor && 
                        Time.time - timeStartedRelationship > minAimRelationshipDuration &&
                        (angleToCurrent - angleToClosest) > aimHysteresis)
                {
                    shouldEstablishNewRelationship = true;
                    Debug.Log($"Found better aligned anchor '{bestAlignedAnchor.label}' ({angleToClosest:F1}°) than current '{currentRelationshipAnchor.label}' ({angleToCurrent:F1}°), switching");
                    
                    // Log switching aiming relationship for user study
                    LogUserStudy($"[GRAB] AIMING_RELATIONSHIP_SWITCHED: Held=\"{_grabbedAnchor.label}\", OldTarget=\"{currentRelationshipAnchor.label}\", NewTarget=\"{bestAlignedAnchor.label}\", Angle={angleToClosest:F1}°, Reason=\"better_alignment\"");
                    
                    // Clear the old relationship first
                    if (sphereToggleScript != null && sphereToggleScript.relationLineManager != null)
                    {
                        sphereToggleScript.ClearSpecificRelationship(currentRelationshipAnchor.label);
                    }
                }
                else
                {
                    // Not significantly better aligned, maintain current relationship
                    stillHighlighted.Add(currentRelationshipAnchor);
                    toDeselect.Remove(currentRelationshipAnchor);
                    Debug.Log($"Maintaining relationship with '{currentRelationshipAnchor.label}' as new anchor '{bestAlignedAnchor.label}' is not significantly better aligned");
                }
            }
            
            // If we should establish a new relationship and the best aligned anchor is not the same as our current relationship
            if (shouldEstablishNewRelationship && (currentRelationshipAnchor != bestAlignedAnchor))
            {
                // Update our current relationship anchor
                currentRelationshipAnchor = bestAlignedAnchor;
                timeStartedRelationship = Time.time;
                
                // Highlight the nearby object if it's not already highlighted
                if (!recentlyHighlighted.Contains(bestAlignedAnchor))
                {
                    StartCoroutine(HighlightAnchorBriefly(bestAlignedAnchor));
                    
                    // Play proximity sound
                    PlayProximitySound();
                    
                    // Add to recently highlighted
                    recentlyHighlighted.Add(bestAlignedAnchor);
                }
                
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
                
                // IMPORTANT: Always get a fresh reference to the SphereToggleScript for the current grabbed anchor
                // This ensures we're using the correct object when generating relationships
                sphereToggleScript = _grabbedAnchor.sphereObj.GetComponent<SphereToggleScript>();
                if (sphereToggleScript == null)
                {
                    Debug.LogWarning("No SphereToggleScript found on current grabbed anchor!");
                    return;
                }
                
                // Generate relationship between the two objects
                sphereToggleScript.GenerateProximityRelationship(bestAlignedAnchor.label);
            }
        }
        else if (currentRelationshipAnchor != null)
        {
            // No anchors in aim range now, clear current relationship
            if (sphereToggleScript != null && sphereToggleScript.relationLineManager != null)
            {
                Debug.Log($"No anchors in aiming range, clearing current relationship with '{currentRelationshipAnchor.label}'");
                
                // Log relationship ending for user study
                LogUserStudy($"[GRAB] AIMING_RELATIONSHIP_ENDED: Held=\"{_grabbedAnchor.label}\", Other=\"{currentRelationshipAnchor.label}\", Reason=\"no_objects_in_aim\"");
                
                sphereToggleScript.ClearSpecificRelationship(currentRelationshipAnchor.label);
                currentRelationshipAnchor = null;
                timeStartedRelationship = 0f;
            }
        }
        
        // For any anchors that are still in the highlighted set but not in stillHighlighted,
        // they must have moved out of range and need to be deselected
        foreach (var anchorToDeselect in toDeselect)
        {
            ResetAnchorHighlight(anchorToDeselect);
            
            // Don't clear relationships here as we already handle that in the main logic
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
        
        // Define the highlight color we're checking for (hex: #3089CF)
        Color highlightColor = new Color(0.188f, 0.537f, 0.812f, 1.0f);
        
        // Check if the current color is our highlight color
        bool isCurrentlyHighlighted = false;
        foreach (Material mat in renderer.materials)
        {
            if (mat.HasProperty("_Color"))
            {
                Color currentColor = mat.GetColor("_Color");
                // Check if colors are approximately equal (floating point comparison)
                if (Mathf.Approximately(currentColor.r, highlightColor.r) &&
                    Mathf.Approximately(currentColor.g, highlightColor.g) &&
                    Mathf.Approximately(currentColor.b, highlightColor.b))
                {
                    isCurrentlyHighlighted = true;
                    break;
                }
            }
        }
        
        // Only reset if it's currently highlighted
        if (isCurrentlyHighlighted || proximityHighlightMaterial != null)
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
            
            Debug.Log($"Reset highlight on anchor '{anchor.label}' from highlight color to original material");
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
        
        // Define highlight color (hex: #3089CF)
        Color highlightColor = new Color(
            r: 0.188f,  // 48/255
            g: 0.537f,  // 137/255
            b: 0.812f,  // 207/255
            a: 1.0f
        );
        
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
            // If no highlight material provided, modify the existing material's color
            foreach (Material mat in renderer.materials)
            {
                if (mat.HasProperty("_Color"))
                {
                    mat.SetColor("_Color", highlightColor);
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

    // Helper method for creating timestamped user study logs
    private void LogUserStudy(string message)
    {
        if (!enableUserStudyLogging) return;
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
    }

    // Add an UpdateAimingDirection method
    /// <summary>
    /// Updates the current aiming direction based on the camera's direction or other input
    /// </summary>
    private void UpdateAimingDirection()
    {
        if (Camera.main == null || _grabbedAnchor == null)
            return;
        
        // Get camera forward as the base aiming direction
        Vector3 cameraForward = Camera.main.transform.forward;
        
        // Option 1: Use pure camera forward direction
        _currentAimDirection = cameraForward;
        
        // Option 2 (alternative): Use the direction from the grabbed anchor to where the camera is looking
        // This creates a "pointing" effect from the held object
        // Vector3 rayDirection = _grabbedAnchor.sphereObj.transform.position - Camera.main.transform.position;
        // _currentAimDirection = cameraForward; 
        
        // Debug information - visualize aiming direction
        if (enableDebugLogging())
        {
            Debug.DrawRay(_grabbedAnchor.sphereObj.transform.position, _currentAimDirection * 2.0f, Color.cyan, aimCheckInterval);
        }
        
        // Update visual aiming indicator if enabled
        UpdateAimingVisualizer();
    }

    /// <summary>
    /// Updates or creates a visual indicator for the aiming direction
    /// </summary>
    private void UpdateAimingVisualizer()
    {
        if (!visualizeAiming || _grabbedAnchor == null)
        {
            // Hide visualizer if it exists
            if (_aimingVisualizer != null)
            {
                _aimingVisualizer.gameObject.SetActive(false);
            }
            return;
        }
        
        // Create visualizer if it doesn't exist
        if (_aimingVisualizer == null)
        {
            if (aimingVisualizerPrefab != null)
            {
                // Instantiate from prefab if provided
                var visualizerObj = Instantiate(aimingVisualizerPrefab);
                _aimingVisualizer = visualizerObj.GetComponent<LineRenderer>();
            }
            else
            {
                // Create a new game object with line renderer if no prefab
                var visualizerObj = new GameObject("AimingVisualizer");
                _aimingVisualizer = visualizerObj.AddComponent<LineRenderer>();
                
                // Set up the line renderer
                _aimingVisualizer.startWidth = aimingLineWidth;
                _aimingVisualizer.endWidth = aimingLineWidth * 0.5f; // Taper the end
                _aimingVisualizer.material = new Material(Shader.Find("Sprites/Default"));
                _aimingVisualizer.startColor = new Color(1f, 1f, 1f, 0.4f); // White with 40% alpha
                _aimingVisualizer.endColor = new Color(1f, 1f, 1f, 0f); // Fade out to transparent
                _aimingVisualizer.positionCount = 2;
            }
        }
        
        // Ensure the visualizer is active
        _aimingVisualizer.gameObject.SetActive(true);
        
        // Update the line positions
        Vector3 startPosition = _grabbedAnchor.sphereObj.transform.position;
        Vector3 endPosition = startPosition + _currentAimDirection * aimingLineLength;
        
        _aimingVisualizer.SetPosition(0, startPosition);
        _aimingVisualizer.SetPosition(1, endPosition);
    }

    /// <summary>
    /// Checks if this hand is close to any anchor and shows/hides the objectTrackingToggle accordingly.
    /// This is done in a static context across all hands to prevent multiple hands from conflicting.
    /// </summary>
    private void CheckHandProximityToAnchors()
    {
        // Only check at the specified interval to avoid performance issues
        if (Time.time - lastProximityCheckTime < proximityCheckInterval || sceneObjectManager == null)
        {
            return;
        }

        lastProximityCheckTime = Time.time;
        
        // Get all anchors from SceneObjectManager
        var allAnchors = sceneObjectManager.GetAllAnchors();
        if (allAnchors == null || allAnchors.Count == 0)
        {
            // No anchors to check, hide toggle if it's visible
            if (objectTrackingToggle != null && objectTrackingToggle.activeSelf)
            {
                SetObjectTrackingToggleVisibility(false);
            }
            return;
        }
        
        // Get this hand's position
        Vector3 handPosition = transform.position;
        
        // Check distance to each anchor
        bool isNearAnchor = false;
        foreach (var anchor in allAnchors)
        {
            if (anchor.sphereObj != null)
            {
                float distance = Vector3.Distance(handPosition, anchor.sphereObj.transform.position);
                
                // Check if hand is within proximity threshold
                if (distance <= proximityThresholdFromHandToAnchor)
                {
                    isNearAnchor = true;
                    break;
                }
            }
        }
        
        // Update the static flag that tracks if any hand is near any anchor
        if (isNearAnchor)
        {
            isAnyHandNearAnyAnchor = true;
        }
        else
        {
            // Check if the other hand is also not near any anchor
            // We do this by checking if this is the left or right hand
            bool isLeftHand = handType == "left";
            
            // Find the other hand's HandGrabTrigger
            HandGrabTrigger[] allHandGrabTriggers = FindObjectsOfType<HandGrabTrigger>();
            HandGrabTrigger otherHand = null;
            
            foreach (var trigger in allHandGrabTriggers)
            {
                if ((isLeftHand && trigger.handType == "right") || (!isLeftHand && trigger.handType == "left"))
                {
                    otherHand = trigger;
                    break;
                }
            }
            
            // If there's no other hand, or if the other hand is also not near any anchor,
            // then no hand is near any anchor
            if (otherHand == null)
            {
                isAnyHandNearAnyAnchor = false;
            }
            else
            {
                // If the other hand is also not near any anchor, set the flag to false
                bool isOtherHandNearAnchor = false;
                Vector3 otherHandPosition = otherHand.transform.position;
                
                foreach (var anchor in allAnchors)
                {
                    if (anchor.sphereObj != null)
                    {
                        float distance = Vector3.Distance(otherHandPosition, anchor.sphereObj.transform.position);
                        
                        // Check if hand is within proximity threshold
                        if (distance <= proximityThresholdFromHandToAnchor)
                        {
                            isOtherHandNearAnchor = true;
                            break;
                        }
                    }
                }
                
                // Only set to false if both hands are not near any anchor
                if (!isOtherHandNearAnchor)
                {
                    isAnyHandNearAnyAnchor = false;
                }
            }
        }
        
        // Update the toggle visibility based on the static flag
        if (objectTrackingToggle != null)
        {
            // Only change visibility if sufficient time has passed since last change
            // This prevents rapid toggling when right at the threshold distance
            if (Time.time - lastToggleVisibilityChangeTime > toggleVisibilityHysteresis)
            {
                SetObjectTrackingToggleVisibility(isAnyHandNearAnyAnchor);
            }
        }
    }

    /// <summary>
    /// Sets the visibility of the object tracking toggle by adjusting colors and disabling hover effects
    /// </summary>
    private void SetObjectTrackingToggleVisibility(bool isVisible)
    {
        if (objectTrackingToggle == null)
            return;
        
        // Keep the object active regardless of visibility
        if (objectTrackingToggle.activeSelf != true)
        {
            // Make sure the object is active so we can modify its renderers
            objectTrackingToggle.SetActive(true);
        }
        
        // Get renderer on the main toggle object
        Renderer mainRenderer = objectTrackingToggle.GetComponent<Renderer>();
        if (mainRenderer != null)
        {
            // For the main toggle object, change color based on visibility
            foreach (Material material in mainRenderer.materials)
            {
                if (material == null)
                    continue;
                    
                if (material.HasProperty("_Color"))
                {
                    if (isVisible)
                    {
                        Color color = new Color(0.435f, 0.435f, 0.435f, 1.0f); // 6F6F6F
                        material.color = color;
                    }
                    else
                    {
                        Color color = Color.black;
                        material.color = color;
                    }
                }
                
                if (material.HasProperty("_BaseColor"))
                {
                    if (isVisible)
                    {
                        Color color = new Color(0.435f, 0.435f, 0.435f, 1.0f); // 6F6F6F
                        material.SetColor("_BaseColor", color);
                    }
                    else
                    {
                        Color color = Color.black;
                        material.SetColor("_BaseColor", color);
                    }
                }
            }
        }
        
        // Enable/disable the VisionOSHoverEffect component
        Unity.PolySpatial.VisionOSHoverEffect hoverEffect = objectTrackingToggle.GetComponent<Unity.PolySpatial.VisionOSHoverEffect>();
        if (hoverEffect != null)
        {
            hoverEffect.enabled = isVisible;
        }
        
        // For child objects like Text, adjust alpha instead
        Transform[] childTransforms = objectTrackingToggle.GetComponentsInChildren<Transform>();
        foreach (Transform childTransform in childTransforms)
        {
            // Skip the main toggle object
            if (childTransform == objectTrackingToggle.transform)
                continue;
            
            Renderer renderer = childTransform.GetComponent<Renderer>();
            if (renderer != null)
            {
                float targetAlpha = isVisible ? 1.0f : 0.0f;
                
                foreach (Material material in renderer.materials)
                {
                    if (material == null)
                        continue;
                        
                    if (material.HasProperty("_Color"))
                    {
                        Color color = material.color;
                        color.a = targetAlpha;
                        material.color = color;
                    }
                    
                    if (material.HasProperty("_BaseColor"))
                    {
                        Color color = material.GetColor("_BaseColor");
                        color.a = targetAlpha;
                        material.SetColor("_BaseColor", color);
                    }
                }
            }
            
            // Handle TextMeshPro components
            TMPro.TextMeshPro tmp = childTransform.GetComponent<TMPro.TextMeshPro>();
            if (tmp != null)
            {
                Color textColor = tmp.color;
                textColor.a = isVisible ? 1.0f : 0.0f;
                tmp.color = textColor;
            }
        }
        
        // Log the visibility change
        lastToggleVisibilityChangeTime = Time.time;
    }
}
