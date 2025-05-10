using UnityEngine;
using UnityEngine.UI; 
using TMPro;
using PolySpatial.Template;
using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;
using System.Text;

/// <summary>
/// Script attached to each sphere toggled in the scene. 
/// It calls Gemini to (A) generate questions about the object, and (B) show relationships with other items.
/// </summary>
public class SphereToggleScript : MonoBehaviour
{
    // Static reference to the currently active toggle
    public static SphereToggleScript CurrentActiveToggle { get; private set; }

    [Header("References")]
    [Tooltip("The Toggle component on this sphere.")]
    [SerializeField]
    private SpatialUIToggle spatialUIToggle;

    [Tooltip("The Toggle component on the child label object.")]
    [SerializeField]
    private SpatialUIToggle labelToggle;

    [Tooltip("TextMeshPro label that holds the sphere's 'name' or 'content'.")]
    public TextMeshPro labelUnderSphere;

    [Tooltip("Reference to the scene's menu. We call SetMenuTitle(...) on it.")]
    public MenuScript menuScript;

    public GameObject InfoPanel;

    // Flag to prevent toggle loops
    private bool isHandlingToggle = false;

    // Make isOn public so RelationToggleController can access it
    public bool isOn = false;

    // -----------------------------
    // New Fields for Gemini Re-Call
    // -----------------------------
    [Header("Gemini Re-Call Settings")]
    [Tooltip("Your Gemini model name, e.g. 'gemini-2.0-flash'")]
    public string modelName = "gemini-2.0-flash";

    [Tooltip("Your API key")]
    public string geminiApiKey = "AIzaSyAoYU7ZM-AImpfA0faIBBz8ovLb_7n0QF4";

    [Tooltip("A reference to your Gemini API client script. Make sure it's initialized.")]
    public GeminiAPI geminiClient;

    [Tooltip("Reference to a GeminiGeneral component to use for API requests")]
    public GeminiGeneral geminiGeneral;

    [Tooltip("A RenderTexture from the camera feed (like a VisionPro or other XR camera).")]
    public RenderTexture cameraRenderTex;

    [Tooltip("Parent transform/container for the newly created question lines.")]
    [HideInInspector]
    public Transform questionsParent;

    [Tooltip("Prefab that displays each question. Should have a TextMeshProUGUI or Text inside.")]
    public GameObject questionPrefab;

    [Header("Question Answering")]
    [Tooltip("Reference to the GeminiQuestionAnswerer component")]
    public GeminiQuestionAnswerer questionAnswerer;

    public GameObject answerPanel;

    [Header("Level 2 Relationship")]
    [Tooltip("Manager that draws lines between related items.")]
    public RelationshipLineManager relationLineManager;

    [Tooltip("Manager that tracks all recognized objects in the scene.")]
    public SceneObjectManager sceneObjManager;

    [Header("Scene Analysis")]
    public SceneContextManager sceneContextManager;
    private SceneContext currentSceneAnalysis;

    [Header("Menu Positioning")]
    [Tooltip("Offset position of the menu canvas relative to the anchor when grabbed")]
    public Vector3 menuOffset = new Vector3(0f, 1f, 0f); // Default slightly above the anchor
    
    public Vector3 menuOffsetForStatic = new Vector3(0f, 1f, 0f);

    private string currentSceneContext = "unknown environment";
    private string currentTaskContext = "no specific task";

    [Header("Object Inspection")]
    [Tooltip("Panel that shows the object description during inspection")]
    public GameObject descriptionPanel;
    [Tooltip("TextMeshProUGUI component that displays the object description")]
    public TextMeshPro descriptionText;
    [Tooltip("How often to update the description (in seconds)")]
    public float inspectionUpdateInterval = 5f;
    // private string currentDescription = "";
    private List<string> descriptionHistory = new List<string>();
    private Coroutine inspectionRoutine;

    // Add at the top of the class with other event declarations
    public delegate void PointingStateChangedHandler(bool isPointing);
    public static event PointingStateChangedHandler OnPointingStateChanged;

    public SpeechToTextRecorder recorder;

    public BaselineModeController baselineModeController;

    private bool currentlyPointing = false;

    [Header("Hand Tracking")]
    [Tooltip("Reference to the MyHandTracking script")]
    public MyHandTracking handTracking;

    [Header("Pointing Visualization")]
    [Tooltip("Plane to show which part is being pointed at")]
    public GameObject pointingPlane;

    [Tooltip("TextMeshPro component on the pointing plane")]
    public TextMeshPro pointingPlaneText;

    [Tooltip("Offset distance above the finger point")]
    public float planeUpOffset = 0.02f;

    [Tooltip("Offset distance for recorder toggle below the pointing plane (negative = below)")]
    public float recorderOffsetFromPointingPlane = 0.05f;

    [Tooltip("Maximum distance between hands to activate pointing detection (in meters)")]
    public float maxHandProximityDistance = 0.2f;  // 20cm default

    private Vector3 relativePosition; // Store relative position to holding hand

    public GameObject recorderToggle;
    public GameObject objectTrackingToggle;

    [SerializeField]
    Vector3 recorderToggleOffset = new Vector3(0f, 0.1f, -0.05f);

    [SerializeField]
    Vector3 objectTrackingToggleOffset = new Vector3(0f, 0.06f, -0.05f);

    [SerializeField]
    Vector3 questionToggleOffset = new Vector3(-0.07f, 0.1f, -0.05f);

    [SerializeField]
    Vector3 relationToggleOffset = new Vector3(-0.07f, 0.06f, -0.05f);

    [SerializeField]
    Vector3 Offset = new Vector3(0f, 0.06f, -0.05f);

    private Vector3 originalInfoPanelPosition;

    // Add this variable to store original AnimateWindow settings
    private Vector3 originalStartPosition;
    private Vector3 originalEndPosition;

    // Add new variables for auto-recording
    [Header("Auto-Recording")]
    [Tooltip("Whether to automatically start recording when a new part is pointed at")]
    public bool enableAutoRecordOnPointing = true;
    
    [Tooltip("Duration in seconds for automatic recording before stopping")]
    public float autoRecordDuration = 10f;
    
    [Tooltip("Minimum time between auto-recordings to prevent rapid toggling")]
    public float minTimeBetweenAutoRecordings = 3f;
    
    [Tooltip("Color to use when auto-recording is active")]
    public Color autoRecordingActiveColor = new Color(1f, 0.3f, 0.3f, 1f); // Bright red by default

    private bool isAutoRecording = false;
    private float lastAutoRecordTime = 0f;
    private Coroutine autoRecordingStopCoroutine;
    private string lastRecordedPart = "";
    private Material originalRecorderMaterial;

    // Add these fields at an appropriate place in the class
    private Coroutine activeQuestionGenerationCoroutine;
    private Coroutine activeRelationshipQuestionCoroutine;

    [Header("User Study Logging")]
    [SerializeField] private bool enableUserStudyLogging = true;

    public GameObject questionToggle;

    public GameObject relationToggle;

    // New fields for function toggle exclusivity
    private bool isHandlingFunctionToggleExclusivity = false;

    // Define a handler for pointing state changes to avoid recursion
    private void OnPointingStateHandler(bool isPointing)
    {
        // This method only handles external state changes, not ones we initiate
        if (isPointing) {
            // Handle pointing started
            if (pointingPlane != null) {
                pointingPlane.SetActive(true);
                // Update the recorder toggle position
                if (recorderToggle != null && isOn) {
                    UpdateRecorderPositionOnPointing();
                }
                Debug.Log("Pointing event received: started");
                
                // Log pointing state change for user study
                if (pointingPlaneText != null && !string.IsNullOrEmpty(pointingPlaneText.text) && 
                    pointingPlaneText.text != "none" && labelUnderSphere != null)
                {
                    LogUserStudy($"[DETAIL] [POINTING] POINTING_STARTED: Object=\"{labelUnderSphere.text}\", Part=\"{pointingPlaneText.text}\"");
                }
                
                // Generate specialized questions for the part being pointed at
                if (isOn && pointingPlaneText != null && !string.IsNullOrEmpty(pointingPlaneText.text) && 
                    pointingPlaneText.text != "none" && labelUnderSphere != null) {
                    string partName = pointingPlaneText.text;
                    string partDescription = descriptionText != null ? descriptionText.text : "";
                    string objectLabel = labelUnderSphere.text;
                    
                    // Generate specialized questions for this specific part
                    StartCoroutine(GeneratePointingQuestionsRoutine(objectLabel, partName, partDescription));
                }
            }
        } else {
            // Handle pointing ended
            if (pointingPlane != null) {
                pointingPlane.SetActive(false);
                relativePosition = Vector3.zero;
                
                // // Log pointing end for user study
                // if (labelUnderSphere != null)
                // {
                //     LogUserStudy($"POINTING_ENDED: Object=\"{labelUnderSphere.text}\"");
                // }
            }
            
            // Make sure to stop recording if it's active before resetting position
            StopAutoRecordingIfActive();
            
            // Also check if we need to manually stop the recorder if it's active
            if (recorderToggle != null)
            {
                SpatialUIToggle toggle = recorderToggle.GetComponent<SpatialUIToggle>();
                if (toggle != null)
                {
                    // Check if the toggle is in the ON state (recording)
                    // We can't access the active state directly, so we'll use the recorder to check
                    SpeechToTextRecorder recorderComponent = recorderToggle.GetComponent<SpeechToTextRecorder>();
                    if (recorderComponent == null && recorderToggle.transform.parent != null)
                    {
                        recorderComponent = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
                    }
                    
                    if (recorderComponent != null && recorderComponent.enabled)
                    {
                        // Try to determine if it's recording using reflection
                        var isRecordingField = recorderComponent.GetType().GetField("isRecording", 
                            System.Reflection.BindingFlags.Instance | 
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Public);
                            
                        if (isRecordingField != null)
                        {
                            bool isRecording = (bool)isRecordingField.GetValue(recorderComponent);
                            if (isRecording)
                            {
                                // Toggle it off by simulating a press
                                Debug.Log("Stopping active recording when resetting recorder position");
                                toggle.PressStart();
                                toggle.PressEnd();
                            }
                        }
                    }
                }
            }
            
            // Reset recorder toggle position
            if (recorderToggle != null && isOn) {
                UpdateRecorderToggle(true);
            }
            
            // Reset the last recorded part
            lastRecordedPart = "";
            Debug.Log("Pointing event received: ended");
            
            // When pointing ends, regenerate the general questions for the whole object
            if (isOn && labelUnderSphere != null) {
                string labelContent = labelUnderSphere.text;
                StartCoroutine(GenerateQuestionsRoutine(labelContent));
            }
        }
    }

    private void Start()
    {
        if (geminiClient == null)
        {
            geminiClient = new GeminiAPI(modelName, geminiApiKey);
        }

        if (recorderToggle == null)
        {
            recorderToggle = GameObject.Find("RecorderToggle");
        }

        if (objectTrackingToggle == null)
        {
            objectTrackingToggle = GameObject.Find("ObjectTrackingToggle");
        }
        
        if (questionToggle == null)
        {
            questionToggle = GameObject.Find("QuestionToggle");
        }

        if (relationToggle == null)
        {
            relationToggle = GameObject.Find("RelationToggle");
        }

        // Set up the question toggle to control the InfoPanel
        if (questionToggle != null)
        {
            SpatialUIToggle toggle = questionToggle.GetComponent<SpatialUIToggle>();
            if (toggle != null)
            {
                // Clear any existing listeners to avoid duplicates
                toggle.m_ToggleChanged.RemoveAllListeners();
                // Add listener to control the InfoPanel
                toggle.m_ToggleChanged.AddListener(SetInfoPanelVisibility);
                
                // Initialize the toggle state to match the InfoPanel if it exists
                if (InfoPanel != null)
                {
                    bool isPanelActive = InfoPanel.activeSelf;
                    if (toggle.m_Active != isPanelActive)
                    {
                        // Update toggle state without invoking events
                        if (isPanelActive)
                            toggle.PassiveToggleWithoutInvokeOn();
                        else
                            toggle.PassiveToggleWithoutInvokeOff();
                    }
                }
            }
        }

        // Set up the relation toggle to control relationship lines
        if (relationToggle != null)
        {
            SpatialUIToggle toggle = relationToggle.GetComponent<SpatialUIToggle>();
            if (toggle != null)
            {
                // Clear any existing listeners to avoid duplicates
                toggle.m_ToggleChanged.RemoveAllListeners();

                // Check if there's a RelationToggleController on the GameObject
                var controller = relationToggle.GetComponent<RelationToggleController>();
                if (controller == null)
                {
                    // Add the controller if it doesn't exist
                    controller = relationToggle.AddComponent<RelationToggleController>();
                    // Set up references
                    controller.toggle = toggle;
                    controller.ownerSphereToggle = this;
                    controller.relationshipLineManager = relationLineManager;
                    controller.sceneObjectManager = sceneObjManager;
                    controller.geminiGeneral = geminiGeneral;
                    controller.geminiClient = geminiClient;
                }
                
                // Initialize toggle to off state - relationships start hidden
                if (toggle.m_Active)
                {
                    toggle.PassiveToggleWithoutInvokeOff();
                }
            }
        }

        // Keep a reference to the object tracking toggle to restore if needed
        if (objectTrackingToggle != null)
        {
            // Cache a reference to ensure we can restore it if needed
            Transform objectTrackingToggleTransform = objectTrackingToggle.transform;
        }

        if (recorder == null)
        {
            recorder = FindFirstObjectByType<SpeechToTextRecorder>();
        }

        if (baselineModeController == null)
        {
            baselineModeController = FindFirstObjectByType<BaselineModeController>();
        }
        
        // Subscribe to baseline mode changes
        if (baselineModeController != null)
        {
            baselineModeController.OnBaselineModeChanged += HandleBaselineModeChanged;
            
            // Initial check - hide recorder toggle if already in baseline mode
            if (baselineModeController.baselineMode && recorderToggle != null && !isOn)
            {
                recorderToggle.SetActive(false);
            }
        }

        // Ensure we have a reference to a GeminiGeneral component
        if (geminiGeneral == null)
        {
            // Try to find one in the scene
            geminiGeneral = FindFirstObjectByType<GeminiGeneral>();
            
            if (geminiGeneral == null)
            {
                Debug.LogWarning("No GeminiGeneral component found. API calls may not be properly managed for concurrency.");
            }
        }

        // Find the label toggle if not assigned
        if (labelToggle == null)
        {
            // Look for any child that has a SpatialUIToggle component
            foreach (Transform child in transform)
            {
                SpatialUIToggle childToggle = child.GetComponent<SpatialUIToggle>();
                if (childToggle != null && childToggle != spatialUIToggle)
                {
                    labelToggle = childToggle;
                    Debug.Log($"Found label toggle on child: {child.name}");
                    break;
                }
            }

            if (labelToggle == null)
            {
                Debug.LogWarning("No label toggle found among children. Bidirectional toggle functionality will be disabled.");
            }
        }

        // Check if we're already on at start but there's another active toggle
        if (isOn)
        {
            // If there's already a different active toggle, turn this one off
            if (CurrentActiveToggle != null && CurrentActiveToggle != this)
            {
                Debug.Log($"Multiple toggles active at start. Turning off {gameObject.name}");
                isOn = false;
                if (spatialUIToggle != null && spatialUIToggle.enableInteraction)
                {
                    spatialUIToggle.PressStart();
                    spatialUIToggle.PressEnd();
                }
                if (labelToggle != null && labelToggle.enableInteraction)
                {
                    labelToggle.PressStart();
                    labelToggle.PressEnd();
                }
                HandleToggleEffects(false);
            }
            else
            {
                // If no other active toggle, register this one as active
                CurrentActiveToggle = this;
            }
        }

        // Subscribe to the toggle's onValueChanged event
        SubscribeToToggleEvents();

        if (sceneContextManager != null)
        {
            sceneContextManager.OnSceneContextComplete += HandleSceneAnalysis;
        }

        // Subscribe to anchor grab/release events
        HandGrabTrigger.OnAnchorGrabbed += HandleAnchorGrabbed;
        HandGrabTrigger.OnAnchorReleased += HandleAnchorReleased;

        // Subscribe to pointing state changes with our handler method
        OnPointingStateChanged += OnPointingStateHandler;

        // Set up the recorder toggle listener for exclusivity
        if (recorderToggle != null)
        {
            SpatialUIToggle toggle = recorderToggle.GetComponent<SpatialUIToggle>();
            if (toggle != null)
            {
                toggle.m_ToggleChanged.AddListener(OnRecorderFunctionToggleChanged);
            }
        }
    }

    private void OnDestroy()
    {
        if (sceneContextManager != null)
        {
            sceneContextManager.OnSceneContextComplete -= HandleSceneAnalysis;
        }

        // Unsubscribe from all events
        HandGrabTrigger.OnAnchorGrabbed -= HandleAnchorGrabbed;
        HandGrabTrigger.OnAnchorReleased -= HandleAnchorReleased;
        UnsubscribeFromToggleEvents();
        OnPointingStateChanged -= OnPointingStateHandler;
        
        // Unsubscribe from baseline mode changes
        if (baselineModeController != null)
        {
            baselineModeController.OnBaselineModeChanged -= HandleBaselineModeChanged;
        }
        
        // Unsubscribe from question toggle
        if (questionToggle != null)
        {
            SpatialUIToggle toggle = questionToggle.GetComponent<SpatialUIToggle>();
            if (toggle != null)
            {
                toggle.m_ToggleChanged.RemoveListener(SetInfoPanelVisibility);
            }
        }

        // Unsubscribe from relation toggle
        if (relationToggle != null)
        {
            SpatialUIToggle toggle = relationToggle.GetComponent<SpatialUIToggle>();
            if (toggle != null)
            {
                // Don't need to remove listener since we're not adding it directly anymore
                // toggle.m_ToggleChanged.RemoveListener(ToggleRelationshipLines);
            }
        }

        // Unsubscribe from recorder toggle exclusivity listener
        if (recorderToggle != null)
        {
            SpatialUIToggle toggle = recorderToggle.GetComponent<SpatialUIToggle>();
            if (toggle != null)
            {
                toggle.m_ToggleChanged.RemoveListener(OnRecorderFunctionToggleChanged);
            }
        }

        // Make sure to set pointing state to false when destroyed
        if (currentlyPointing)
        {
            currentlyPointing = false;
            OnPointingStateChanged?.Invoke(false);
        }

        if (pointingPlane != null)
        {
            pointingPlane.SetActive(false);
        }
        
        // Clean up auto-recording if active
        StopAutoRecordingIfActive();
        
        // Stop any active coroutines
        if (activeQuestionGenerationCoroutine != null)
        {
            StopCoroutine(activeQuestionGenerationCoroutine);
        }
        
        if (activeRelationshipQuestionCoroutine != null)
        {
            StopCoroutine(activeRelationshipQuestionCoroutine);
        }
        
        // Clear the current active toggle reference if this is being destroyed
        if (CurrentActiveToggle == this)
        {
            CurrentActiveToggle = null;
            Debug.Log("Cleared CurrentActiveToggle reference as this toggle is being destroyed");
        }
    }
    
    // Add handler for baseline mode changes
    private void HandleBaselineModeChanged(bool isBaselineMode)
    {
        // If baseline mode is turned on and recorder toggle exists
        if (isBaselineMode && recorderToggle != null && !isOn)
        {
            // Hide the recorder toggle
            recorderToggle.SetActive(false);
        }
        // If baseline mode is turned off, always make recorder toggle visible
        else if (!isBaselineMode && recorderToggle != null)
        {
            // Show the recorder toggle regardless of sphere toggle state
            recorderToggle.SetActive(true);
            
            // Make sure to update its position if sphere toggle is on
            if (isOn)
            {
                UpdateRecorderToggle(true);
            }
        }
        
        // Check if objectTrackingToggle reference is lost and restore it if needed
        if (objectTrackingToggle == null)
        {
            objectTrackingToggle = GameObject.Find("ObjectTrackingToggle");
            Debug.Log("Restored missing objectTrackingToggle reference");
        }
        
        // Do the same for object tracking toggle
        if (isBaselineMode && objectTrackingToggle != null && !isOn)
        {
            objectTrackingToggle.SetActive(false);
        }
        else if (!isBaselineMode && objectTrackingToggle != null)
        {
            // Show the object tracking toggle regardless of sphere toggle state
            objectTrackingToggle.SetActive(true);
            
            // Make sure to update its position if sphere toggle is on
            if (isOn)
            {
                UpdateObjectTrackingToggle(true);
            }
        }
        
        // If baseline mode is turned off, also make sure to update HandleToggleEffects
        // for the current state to ensure toggles are positioned correctly
        if (!isBaselineMode && isOn)
        {
            // Update the object tracking toggle since it would have been skipped in baseline mode
            UpdateObjectTrackingToggle(true);
        }
    }

    private void HandleSceneAnalysis(SceneContext analysis)
    {
        currentSceneAnalysis = analysis;
    }

    private void UpdateSceneContext()
    {
        currentSceneContext = "unknown environment";
        currentTaskContext = "no specific task";
        
        // Get current analysis from sceneContextManager
        if (sceneContextManager != null && sceneContextManager.GetCurrentAnalysis() != null)
        {
            var analysis = sceneContextManager.GetCurrentAnalysis();
            currentSceneContext = analysis.sceneType ?? "unknown environment";
            if (analysis.possibleTasks != null && analysis.possibleTasks.Count > 0)
            {
                currentTaskContext = string.Join(", ", analysis.possibleTasks);
            }
        }

        Debug.Log($"Using scene context: {currentSceneContext}");
        Debug.Log($"Using task context: {currentTaskContext}");
    }

    // Handler for when the main sphere is toggled
    private void OnSphereToggled(bool toggledOn)
    {
        // If we're already handling a toggle event, ignore this one to prevent loops
        if (isHandlingToggle) return;
        
        isHandlingToggle = true;
        
        // Before activating, deactivate any other toggle in the scene
        if (toggledOn) {
            DeactivateAllOtherToggles();
        }
        
        // Update our internal state first
        isOn = toggledOn;
        
        // Log the toggling action for user study
        if (toggledOn && labelUnderSphere != null)
        {
            LogUserStudy($"[OBJECT] OBJECT_TOGGLED: Object=\"{labelUnderSphere.text}\", State=ON");
            
            // Set as current active toggle
            CurrentActiveToggle = this;
        }
        else if (!toggledOn && labelUnderSphere != null)
        {
            LogUserStudy($"[OBJECT] OBJECT_TOGGLED: Object=\"{labelUnderSphere.text}\", State=OFF");
            
            // If this was the current active toggle, clear the reference
            if (CurrentActiveToggle == this) {
                CurrentActiveToggle = null;
            }
        }
        
        // Update the label toggle to match this toggle's state
        if (labelToggle != null && labelToggle.enableInteraction)
        {
            // Since we can't access m_Active directly (it's private), simulate a press if needed
            SimulateToggleIfNeeded(labelToggle, toggledOn);
        }

        // Handle the toggle effects
        HandleToggleEffects(isOn);
        
        isHandlingToggle = false;
    }

    // Handler for when the label toggle is toggled
    private void OnLabelToggled(bool toggledOn)
    {
        // If we're already handling a toggle event, ignore this one to prevent loops
        if (isHandlingToggle) return;
        
        isHandlingToggle = true;

        // Before activating, deactivate any other toggle in the scene
        if (toggledOn) {
            DeactivateAllOtherToggles();
        }
        
        // Update our internal state first
        isOn = toggledOn;
        
        // If toggling ON, update current active reference
        if (toggledOn) {
            CurrentActiveToggle = this;
        } else if (CurrentActiveToggle == this) {
            CurrentActiveToggle = null;
        }
        
        // Update the sphere toggle to match the label toggle's state
        if (spatialUIToggle != null && spatialUIToggle.enableInteraction)
        {
            // Since we can't access m_Active directly (it's private), simulate a press if needed
            SimulateToggleIfNeeded(spatialUIToggle, toggledOn);
        }
        
        // Handle the toggle effects
        HandleToggleEffects(isOn);
        
        isHandlingToggle = false;
    }
    
    // New method to deactivate all other toggles when this one is activated
    private void DeactivateAllOtherToggles()
    {
        // If there's already an active toggle that isn't this one, deactivate it
        if (CurrentActiveToggle != null && CurrentActiveToggle != this && CurrentActiveToggle.isOn)
        {
            // Log that we're auto-turning off the previous toggle
            if (CurrentActiveToggle.labelUnderSphere != null)
            {
                Debug.Log($"Auto-toggling off previous sphere: {CurrentActiveToggle.labelUnderSphere.text}");
                LogUserStudy($"[OBJECT] AUTO_TOGGLE_OFF: Object=\"{CurrentActiveToggle.labelUnderSphere.text}\", Reason=\"New toggle activated\"");
            }
            
            // Turn off the other toggle
            CurrentActiveToggle.TurnOffToggle();
        }
    }
    
    // New method to programmatically turn off this toggle
    public void TurnOffToggle()
    {
        // Only do something if we're currently on
        if (!isOn) return;
        
        // Use isHandlingToggle to prevent any feedback loops
        isHandlingToggle = true;
        
        // Update internal state
        isOn = false;
        
        // Simulate pressing the toggle buttons to turn them off
        if (spatialUIToggle != null && spatialUIToggle.enableInteraction)
        {
            spatialUIToggle.PressStart();
            spatialUIToggle.PressEnd();
        }
        
        if (labelToggle != null && labelToggle.enableInteraction)
        {
            labelToggle.PressStart();
            labelToggle.PressEnd();
        }
        
        // Apply the effects directly
        HandleToggleEffects(false);
        
        // If this was the active toggle, clear the reference
        if (CurrentActiveToggle == this)
        {
            CurrentActiveToggle = null;
        }
        
        isHandlingToggle = false;
    }
    
    // Helper method to simulate a toggle press if current state doesn't match desired state
    private void SimulateToggleIfNeeded(SpatialUIToggle toggle, bool desiredState)
    {
        // Always toggle as the states are already different (this method is called
        // when we need to sync the toggles)
        toggle.PressStart();
        toggle.PressEnd();
    }

    private void UpdateTogglePosition(GameObject toggle, Vector3 offset, bool isOn, string toggleType)
    {
        if (toggle != null)
        {
            // If in baseline mode and this is the object tracking toggle, keep it inactive
            if (baselineModeController != null && baselineModeController.baselineMode && 
                toggleType == "objectTracking")
            {
                toggle.SetActive(false);
                return;
            }
            
            if (isOn)
            {
                toggle.transform.SetParent(transform);
                if (!toggle.transform.hasChanged)  toggle.transform.localScale = toggle.transform.localScale / transform.localScale.x;
                toggle.GetComponent<SpatialUI>().UpdateReferenceScale();
                toggle.transform.localPosition = offset;
                toggle.transform.localRotation = Quaternion.identity;
                
                var lazyFollow = toggle.GetComponent<LazyFollow>();
                if (lazyFollow != null) lazyFollow.enabled = false;

                // Handle specific toggle types
                if (toggleType == "recorder" && recorder != null && labelUnderSphere != null) 
                {
                    recorder.SetObjectLabel(labelUnderSphere.text, this.gameObject);
                }
                else if (toggleType == "relation")
                {
                    // Update the relation controller owner reference
                    var controller = toggle.GetComponent<RelationToggleController>();
                    if (controller != null)
                    {
                        controller.UpdateOwner(this);
                        // Ensure the toggle's active state matches the controller
                        if (controller.toggle != null && controller.toggle.m_Active)
                        {
                            controller.ToggleRelationshipLines(true);
                        }
                    }
                }
                
                // Make sure the toggle is active when toggled on
                toggle.SetActive(true);
            }
            else
            {
                toggle.transform.SetParent(null);
                toggle.GetComponent<SpatialUI>().UpdateReferenceScale();
                toggle.transform.localPosition = Vector3.zero;
                toggle.transform.localRotation = Quaternion.identity;
                
                var lazyFollow = toggle.GetComponent<LazyFollow>();
                if (lazyFollow != null) lazyFollow.enabled = true;
                
                // Handle specific toggle types
                if (toggleType == "recorder")
                {
                    SpeechToTextRecorder recorder = toggle.GetComponent<SpeechToTextRecorder>();
                    if (recorder == null && toggle.transform.parent != null) recorder = toggle.transform.parent.GetComponent<SpeechToTextRecorder>();
                    if (recorder != null)   recorder.ResetObjectLabel();
                    
                    // New logic: If in baseline mode, set the toggle inactive when toggled off
                    if (baselineModeController != null && baselineModeController.baselineMode)
                    {
                        toggle.SetActive(false);
                    }
                }
                else if (toggleType == "relation")
                {
                    // If the relation toggle is active when being detached, turn it off
                    var controller = toggle.GetComponent<RelationToggleController>();
                    if (controller != null && controller.toggle != null && controller.toggle.m_Active)
                    {
                        controller.ToggleRelationshipLines(false);
                    }
                }
            }
        }
    }

    private void UpdateRecorderToggle(bool isOn)
    {
        UpdateTogglePosition(recorderToggle, recorderToggleOffset, isOn, "recorder");
    }

    private void UpdateObjectTrackingToggle(bool isOn)
    {
        UpdateTogglePosition(objectTrackingToggle, objectTrackingToggleOffset, isOn, "objectTracking");
    }

    private void UpdateQuestionToggle(bool isOn)
    {
        UpdateTogglePosition(questionToggle, questionToggleOffset, isOn, "question");
    }

    private void UpdateRelationToggle(bool isOn)
    {
        UpdateTogglePosition(relationToggle, relationToggleOffset, isOn, "relation");
        
        // Update the controller's owner reference
        if (isOn && relationToggle != null)
        {
            RelationToggleController controller = relationToggle.GetComponent<RelationToggleController>();
            if (controller != null)
            {
                controller.UpdateOwner(this);
            }
        }
    }

    private void OnObjectInspected(bool inspected)
    {
        if (inspected)
        {
            // descriptionPanel.SetActive(true);
            string labelContent = labelUnderSphere ? labelUnderSphere.text : "unknown object";
            
            // Start continuous inspection updates
            if (inspectionRoutine != null)
            {
                StopCoroutine(inspectionRoutine);
            }
            inspectionRoutine = StartCoroutine(UpdateObjectDescriptionRoutine(labelContent));
        }
        else
        {
            // descriptionPanel.SetActive(false);
            // Stop the continuous updates
            if (inspectionRoutine != null)
            {
                StopCoroutine(inspectionRoutine);
                inspectionRoutine = null;
            }
            
            // Make sure to set pointing state to false when inspection stops
            if (currentlyPointing)
            {
                currentlyPointing = false;
                OnPointingStateChanged?.Invoke(false);
            }
        }
    }

    private IEnumerator UpdateObjectDescriptionRoutine(string labelContent)
    {
        // Keep track of the last part we pointed at to detect changes
        string lastPointedPartName = "";
        
        while (true)
        {
            // Check if hands are close enough to enable pointing detection
            bool handsInProximity = CheckHandsProximity();
            
            if (!handsInProximity)
            {
                // If hands are too far apart, disable pointing and wait
                if (currentlyPointing)
                {
                    currentlyPointing = false;
                    OnPointingStateChanged?.Invoke(false);
                    lastPointedPartName = ""; // Reset the last pointed part
                }
                yield return new WaitForSeconds(0.5f); // Check less frequently when hands are far apart
                continue;
            }
            
            // 1) Capture the current frame
            Texture2D frameTex = CaptureFrame(cameraRenderTex);
            string base64Image = ConvertTextureToBase64(frameTex);
            Destroy(frameTex);

            // 2) Build the prompt with history context
            string historyContext = descriptionHistory.Count > 0 
                ? "Previously observed information:\n" + string.Join("\n", descriptionHistory)
                : "No previous observations.";

            // Modify the prompt to request JSON format
            string prompt = $@"
                You are analyzing a {labelContent} in real-time.
                Scene context: {currentSceneContext}
                Task context: {currentTaskContext}

                Based on the current image and considering the previous observations:
                1. Describe any NEW details or changes you notice about the object
                2. Focus on aspects not mentioned before
                3. Only describe the part where the user is currently pointing at
                4. Consider the object's current state, position, and interaction with the environment
                5. If you don't see any new information, respond with: {{""part"": ""none"", ""description"": ""No new observations.""}}
                6. If the user is not pointing at the object, respond with: {{""part"": ""none"", ""description"": ""Not being pointed at.""}}
                7. The user pointing at the object is because they don't fully understand this part. So explain it in a straightforward way.
                8. Keep it concise under 25 words.

                Format your response in JSON:
                {{
                    ""part"": ""<name of the specific part being pointed at>"",
                    ""description"": ""<helpful explanation of that part in one sentence>""
                }}
            ";

            // 3) Call Gemini using the MakeGeminiRequest method from GeminiGeneral for concurrent API calls
            var request = geminiGeneral != null 
                ? geminiGeneral.MakeGeminiRequest(prompt, base64Image)
                : new GeminiGeneral.RequestStatus(geminiClient.GenerateContent(prompt, base64Image));
            
            while (!request.IsCompleted)
                yield return null;

            string rawResponse = request.Result;
            
            // First extract the JSON from the response
            string jsonStr = TryExtractJson(rawResponse);
            
            if (!string.IsNullOrEmpty(jsonStr))
            {
                try
                {
                    var pointingInfo = JsonConvert.DeserializeObject<PointingDescription>(jsonStr);
                    bool isPointingNow = pointingInfo.part != "none";
                    
                    // Update pointing state if changed
                    if (isPointingNow != currentlyPointing)
                    {
                        currentlyPointing = isPointingNow;
                        OnPointingStateChanged?.Invoke(currentlyPointing);
                    }
                    
                    if (isPointingNow)
                    {
                        // Check if the user is pointing at a different part now
                        bool partChanged = lastPointedPartName != pointingInfo.part && !string.IsNullOrEmpty(pointingInfo.part);
                        
                        // If the pointing part has changed, generate new questions for this part
                        if (partChanged && isOn)
                        {
                            Debug.Log($"Pointing part changed from '{lastPointedPartName}' to '{pointingInfo.part}' - regenerating questions");
                            StartCoroutine(GeneratePointingQuestionsRoutine(labelContent, pointingInfo.part, pointingInfo.description));
                            lastPointedPartName = pointingInfo.part;
                        }
                        
                        // Get the current fingertip position to update the pointing plane
                        var handSubsystems = new List<XRHandSubsystem>();
                        SubsystemManager.GetSubsystems(handSubsystems);
                        
                        if (handSubsystems.Count > 0 && handTracking != null)
                        {
                            var handSubsystem = handSubsystems[0];
                            GameObject holdingHand = handTracking.m_SpawnedLeftHand;
                            XRHand pointingHand = handSubsystem.rightHand;
                            
                            // If we can get the right index fingertip position, update the pointing plane
                            if (pointingHand.isTracked && 
                                pointingHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose fingerTipPose) &&
                                pointingPlane != null && 
                                holdingHand != null)
                            {
                                // First position the plane at the current fingertip position
                                pointingPlane.transform.position = fingerTipPose.position + (Vector3.up * planeUpOffset);
                                
                                // Ensure it's a child of the holding hand for tracking
                                if (!pointingPlane.transform.IsChildOf(holdingHand.transform))
                                {
                                    pointingPlane.transform.SetParent(holdingHand.transform);
                                }
                                
                                // Update the relative position for future reference
                                relativePosition = pointingPlane.transform.localPosition;
                                
                                // Get or add the DualTargetLazyFollow component
                                var dualLazyFollow = pointingPlane.GetComponent<DualTargetLazyFollow>();
                                if (dualLazyFollow == null)
                                {
                                    // Remove any existing standard LazyFollow component
                                    var oldLazyFollow = pointingPlane.GetComponent<LazyFollow>();
                                    if (oldLazyFollow != null)
                                    {
                                        Destroy(oldLazyFollow);
                                    }
                                    
                                    // Add and configure the DualTargetLazyFollow component
                                    dualLazyFollow = pointingPlane.AddComponent<DualTargetLazyFollow>();
                                    
                                    // Configure component for best visual experience
                                    dualLazyFollow.movementSpeed = 15f;
                                    dualLazyFollow.movementSpeedVariancePercentage = 0.2f;
                                    dualLazyFollow.minAngleAllowed = 3f;
                                    dualLazyFollow.maxAngleAllowed = 15f;
                                    dualLazyFollow.timeUntilThresholdReachesMaxAngle = 0.3f;
                                    dualLazyFollow.minDistanceAllowed = 0.02f;
                                    dualLazyFollow.maxDistanceAllowed = 0.05f;
                                    dualLazyFollow.timeUntilThresholdReachesMaxDistance = 0.3f;
                                }
                                
                                // Set/update the rotation target to the camera
                                dualLazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.None; // Don't follow position
                                dualLazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAt; // Look at camera
                                dualLazyFollow.rotationTarget = Camera.main.transform;
                                
                                // Make sure the component is enabled
                                dualLazyFollow.enabled = true;
                                
                                Debug.Log($"Updated pointing plane position for part: {pointingInfo.part}, at position: {fingerTipPose.position}");
                            }
                        }
                        
                        // After updating the position, make sure the visualization is active
                        if (pointingPlane != null && !pointingPlane.activeSelf)
                        {
                            pointingPlane.SetActive(true);
                        }
                        
                        // Check if this is a new part being pointed at
                        bool isNewPart = pointingInfo.part != lastRecordedPart;
                        
                        // If this is a new part and auto-recording is enabled AND no recording is currently in progress, start recording
                        if (isNewPart && enableAutoRecordOnPointing && recorderToggle != null && !isAutoRecording)
                        {
                            // Update the last recorded part
                            lastRecordedPart = pointingInfo.part;
                            
                            // Check if enough time has passed since the last auto-recording
                            float timeSinceLastRecording = Time.time - lastAutoRecordTime;
                            if (timeSinceLastRecording >= minTimeBetweenAutoRecordings)
                            {
                                // Start auto-recording
                                StartAutoRecording();
                            }
                        }
                        else if (isNewPart && enableAutoRecordOnPointing && isAutoRecording)
                        {
                            // Just update the part name without restarting recording
                            Debug.Log($"Detected new part '{pointingInfo.part}' but recording already in progress. Not restarting.");
                            lastRecordedPart = pointingInfo.part;
                        }
                        
                        // Update the recorder position after updating the pointing plane position
                        if (recorderToggle != null && isOn)
                        {
                            UpdateRecorderPositionOnPointing();
                            
                            // Pass the pointing information to the recorder
                            if (recorder != null)
                            {
                                // Try to call the UpdatePointingPartInfo method on the recorder
                                var updateMethod = recorder.GetType().GetMethod("UpdatePointingPartInfo");
                                if (updateMethod != null)
                                {
                                    // Call the method using reflection to handle potential version differences
                                    updateMethod.Invoke(recorder, new object[] { pointingInfo.part, pointingInfo.description });
                                    Debug.Log($"Updated recorder with pointing part info: {pointingInfo.part}");
                                }
                                else
                                {
                                    // Try to access the fields directly if the method doesn't exist (fallback)
                                    var partNameField = recorder.GetType().GetField("currentPointingPartName", 
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                                    var partDescField = recorder.GetType().GetField("currentPointingPartDescription", 
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                                    
                                    if (partNameField != null && partDescField != null)
                                    {
                                        partNameField.SetValue(recorder, pointingInfo.part);
                                        partDescField.SetValue(recorder, pointingInfo.description);
                                        Debug.Log($"Set recorder pointing part fields directly: {pointingInfo.part}");
                                    }
                                    else
                                    {
                                        Debug.LogWarning("Could not update recorder with pointing part info - no compatible methods or fields found");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // Reset the last recorded part when not pointing
                        lastRecordedPart = "";
                        lastPointedPartName = ""; // Reset the last pointed part
                        
                        // Hide the pointing plane when not pointing
                        if (pointingPlane != null && pointingPlane.activeSelf)
                        {
                            pointingPlane.SetActive(false);
                        }
                        
                        // Also clear pointing info in recorder if available
                        if (recorder != null)
                        {
                            var updateMethod = recorder.GetType().GetMethod("UpdatePointingPartInfo");
                            if (updateMethod != null)
                            {
                                // Call with null values to clear
                                updateMethod.Invoke(recorder, new object[] { null, null });
                            }
                        }
                    }

                    // Update UI elements
                    if (pointingPlaneText != null && isPointingNow)
                    {
                        pointingPlaneText.text = pointingInfo.part;
                    }
                    
                    if (descriptionText != null)
                    {
                        descriptionText.text = pointingInfo.description;
                    }

                    if (isPointingNow)
                    {
                        descriptionHistory.Add(pointingInfo.description);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to parse pointing description JSON: {ex}\nJSON string: {jsonStr}");
                }
            }
            else
            {
                Debug.LogError("Failed to extract JSON from Gemini response");
            }

            // Wait for the specified interval before next update
            yield return new WaitForSeconds(inspectionUpdateInterval);
        }
    }

    // Method to check if hands are in a pointing configuration
    private bool CheckHandsProximity()
    {
        if (handTracking == null) return false;
        
        var handSubsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(handSubsystems);
        
        if (handSubsystems.Count == 0) return false;
        
        var handSubsystem = handSubsystems[0];
        
        // Initialize pose variables
        Pose leftWristPose = new Pose();
        Pose rightWristPose = new Pose();
        Pose rightIndexTipPose = new Pose();
        Pose rightIndexProximalPose = new Pose();  // Base of index finger
        
        // Get tracking status and positions
        bool leftHandTracked = handSubsystem.leftHand.isTracked && 
                              handSubsystem.leftHand.GetJoint(XRHandJointID.Wrist).TryGetPose(out leftWristPose);
        
        bool rightHandTracked = handSubsystem.rightHand.isTracked && 
                               handSubsystem.rightHand.GetJoint(XRHandJointID.Wrist).TryGetPose(out rightWristPose) &&
                               handSubsystem.rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out rightIndexTipPose) &&
                               handSubsystem.rightHand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out rightIndexProximalPose);
        
        // Both hands must be tracked
        if (!leftHandTracked || !rightHandTracked) return false;
        
        // Calculate distance between hand wrists (basic proximity check)
        float wristDistance = Vector3.Distance(leftWristPose.position, rightWristPose.position);
        bool handsInProximity = wristDistance <= maxHandProximityDistance;
        
        if (!handsInProximity) return false;
        
        // Check if the right hand is in a pointing gesture
        // This is done by checking if the index finger is extended
        Vector3 indexDirection = (rightIndexTipPose.position - rightIndexProximalPose.position).normalized;
        
        // Use the wrist-to-index-tip as an alternative hand direction
        Vector3 handDirection = (rightIndexTipPose.position - rightWristPose.position).normalized;
        
        // Calculate the angle between these vectors (should be small if finger is extended in pointing position)
        float angle = Vector3.Angle(indexDirection, handDirection);
        
        // Consider it pointing if the angle is relatively small (finger is straight and extended)
        bool isRightHandPointing = angle < 25f; // Threshold value in degrees, adjust as needed
        
        // Debug information - reduced to only log when state changes
        if (isRightHandPointing != currentlyPointing)
        {
            Debug.Log($"Pointing state change - Hand distance: {wristDistance:F2}m, Index angle: {angle:F1}°, Is pointing: {isRightHandPointing}");
        }
        
        // Return true if hands are within proximity threshold AND right hand is pointing
        return handsInProximity && isRightHandPointing;
    }

    private void UpdatePointingVisualization()
    {
        // First check if hands are in proximity - early exit if not
        if (!CheckHandsProximity())
        {
            if (currentlyPointing && pointingPlane != null && pointingPlane.activeSelf)
            {
                Debug.Log("Hand proximity or pointing gesture lost, hiding pointing plane");
                pointingPlane.SetActive(false);
                currentlyPointing = false;
                
                // Don't call HandlePointingStateChanged directly to avoid potential recursion
                // Instead, just notify any listeners about the state change
                OnPointingStateChanged?.Invoke(false);
            }
            return;
        }
        
        if (handTracking != null)
        {
            var handSubsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(handSubsystems);
            
            if (handSubsystems.Count > 0)
            {
                var handSubsystem = handSubsystems[0];
                
                // Since we're using onlyAllowLeftHandGrab mode, we know the holding hand is always the left hand
                GameObject holdingHand = handTracking.m_SpawnedLeftHand;
                
                // The pointing hand is always the right hand
                XRHand pointingHand = handSubsystem.rightHand;
                
                if (pointingHand.isTracked && pointingHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose fingerTipPose))
                {
                    if (pointingPlane != null)
                    {
                        // If not currently in pointing state, trigger state change
                        if (!currentlyPointing)
                        {
                            currentlyPointing = true;
                            
                            // Don't call HandlePointingStateChanged directly to avoid potential recursion
                            // Instead, directly set up the necessary state
                            if (!pointingPlane.activeSelf)
                            {
                                pointingPlane.SetActive(true);
                            }
                            
                            // Notify any listeners about the state change
                            OnPointingStateChanged?.Invoke(true);
                        }
                        
                        // Ensure the plane is active
                        if (!pointingPlane.activeSelf)
                        {
                            pointingPlane.SetActive(true);
                        }
                        
                        // Ensure the plane has the correct name for detection in SpeechToTextRecorder
                        pointingPlane.name = "PointingPlane";

                        // We need to determine if we should initialize or update the plane position
                        bool shouldInitialize = !pointingPlane.transform.IsChildOf(holdingHand.transform);
                        bool hasLazyFollow = pointingPlane.GetComponent<DualTargetLazyFollow>() != null;
                        
                        if (shouldInitialize || !hasLazyFollow)
                        {
                            // CHANGED: First time we should position at the exact right fingertip location
                            // Before parenting to the left hand
                            
                            // 1. First position the pointing plane exactly at the fingertip position
                            pointingPlane.transform.position = fingerTipPose.position + (Vector3.up * planeUpOffset);
                            Debug.Log($"Setting pointing plane at fingertip position: {fingerTipPose.position}");
                            
                            // 2. Then make it a child of the left hand for subsequent tracking
                            pointingPlane.transform.SetParent(holdingHand.transform);
                            
                            // 3. Calculate and store the relative position after setting position and parent
                            // This is now the position relative to the holding hand that matches the fingertip
                            relativePosition = pointingPlane.transform.localPosition;
                            
                            Debug.Log($"Initialized pointing plane with relative position to left hand: {relativePosition}");
                            
                            // 4. Remove any existing LazyFollow component to prevent automatic movement
                            var oldLazyFollow = pointingPlane.GetComponent<LazyFollow>();
                            if (oldLazyFollow != null)
                            {
                                Destroy(oldLazyFollow);
                            }
                            
                            var oldDualLazyFollow = pointingPlane.GetComponent<DualTargetLazyFollow>();
                            if (oldDualLazyFollow != null)
                            {
                                Destroy(oldDualLazyFollow);
                            }
                            
                            // 5. Add and configure a new DualTargetLazyFollow for rotation only
                            var lazyFollow = pointingPlane.AddComponent<DualTargetLazyFollow>();
                            
                            // Configure component for best visual experience
                            lazyFollow.movementSpeed = 15f;
                            lazyFollow.movementSpeedVariancePercentage = 0.2f;
                            lazyFollow.minAngleAllowed = 3f;
                            lazyFollow.maxAngleAllowed = 15f;
                            lazyFollow.timeUntilThresholdReachesMaxAngle = 0.3f;
                            lazyFollow.minDistanceAllowed = 0.02f;
                            lazyFollow.maxDistanceAllowed = 0.05f;
                            lazyFollow.timeUntilThresholdReachesMaxDistance = 0.3f;
                            
                            // 6. Important: Only use LazyFollow for rotation to face the camera, not position
                            lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.None; // Don't follow position
                            lazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAt; // Look at camera
                            lazyFollow.rotationTarget = Camera.main.transform;
                            
                            // Update recorder position immediately after initializing pointing plane
                            if (recorderToggle != null && isOn)
                            {
                                UpdateRecorderPositionOnPointing();
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// (Granularity Lv1) Coroutine that captures the camera frame, sends it to Gemini,
    /// parses a JSON array of user questions, and spawns UI lines for each question.
    /// </summary>
    private IEnumerator GenerateQuestionsRoutine(string labelContent)
    {
        // 1) Capture the camera frame -> Base64
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        string base64Image = ConvertTextureToBase64(frameTex);
        Destroy(frameTex);  // free the temporary texture

        // 2) Build a simple prompt that references the label Content
        //    "Ask up to 5 questions about this item"

        // based on the current scene context and task context:
        string prompt = $@"
            Given the current scene context: {currentSceneContext},
            and the potential tasks: {currentTaskContext},
            and that the user is holding / selecting this item: {labelContent},

            Please return a JSON list of possible user questions about this product/item.
            Focus on questions that are relevant to the current scene context and tasks.
            Return only the most likely questions, up to 5 maximum.

            IMPORTANT: Each question MUST be very concise - less than 10 words total.
            Make each question as short as possible while still being clear.
            Focus on brevity and directness.

            Please predict the questions that the user truly wants to know in the current context.
            Only offer questions that are genuinely valuable to the user.

            In the format:
            json
            [
            ""Short question 1?"",
            ""Short question 2?"",
            ...
            ]
            ";

        // 3) Call Gemini using the MakeGeminiRequest method from GeminiGeneral for concurrent API calls
        var request = geminiGeneral != null 
            ? geminiGeneral.MakeGeminiRequest(prompt, base64Image)
            : new GeminiGeneral.RequestStatus(geminiClient.GenerateContent(prompt, base64Image));

        while (!request.IsCompleted)
            yield return null;

        string geminiResponse = request.Result;
        // Debug.Log("Gemini Questions Response:\n" + geminiResponse);

        // 4) Extract JSON
        string extractedJson = TryExtractJson(geminiResponse);
        Debug.Log("Gemini Questions Response - Extracted JSON:\n" + extractedJson);

        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("Could not find valid JSON block in Gemini question response.");
            
            // Log failure to generate questions for user study
            LogUserStudy($"[OBJECT] [SUGGESTED_ACTION] QUESTIONS_GENERATION_FAILED: Object=\"{labelContent}\"");
            
            yield break;
        }

        // This is our final array of question strings
        List<string> questionsList = null;
        try
        {
            questionsList = JsonConvert.DeserializeObject<List<string>>(extractedJson);
            
            // Log successful question generation for user study
            if (questionsList != null && questionsList.Count > 0)
            {
                LogUserStudy($"[OBJECT] [SUGGESTED_ACTION] QUESTIONS_GENERATED: Object=\"{labelContent}\", Count={questionsList.Count}, Questions=\"{string.Join(" | ", questionsList)}\"");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to parse question array: " + e);
            yield break;
        }

        // 5) Instantiate UI elements for each question
        ClearPreviousQuestions();

        if (questionsList != null && questionsList.Count > 0)
        {
            float currentY = -60f;  // Start at the top
            float questionHeight = 54f;  // Height of each question block, adjust as needed
            float spacing = 0f;  // Space between questions (reduced by 0.5x from 5f)

            foreach (var q in questionsList)
            {
                // Instantiate your question prefab 
                var go = Instantiate(questionPrefab, questionsParent);
                go.name = "GeminiQuestion";

                // Position 
                Transform t = go.transform;
                if (t != null)
                {
                    t.localPosition = new Vector3(0f, -currentY, 0f);
                    currentY += questionHeight + spacing;
                }

                // Set the text inside
                TextMeshPro txt = go.GetComponentInChildren<TextMeshPro>();
                if (txt != null) txt.text = q;

                // Add button press handling
                var button = go.GetComponent<SpatialUIButton>();
                if (button != null)
                {
                    string questionText = q; // closure
                    button.WasPressed += (buttonText, renderer, index) =>
                    {
                        // clear the previous answer in the answer panel: set the text to "Generating..."
                        answerPanel.GetComponentInChildren<TextMeshPro>().text = "Generating...";

                        if (questionAnswerer != null)
                        {
                            questionAnswerer.RequestAnswer(questionText);
                            answerPanel.SetActive(true);
                        }
                        else
                        {
                            Debug.LogWarning("No QuestionAnswerer reference set.");
                        }
                    };
                }
                else
                {
                    Debug.LogWarning("Question prefab is missing SpatialUIButton component.");
                }
            }
        }
    }

    /// <summary>
    /// (Granularity Lv2) Coroutine that calls Gemini to find relationships among scene items,
    /// draws lines from the toggled object to each related item.
    /// </summary>
    private IEnumerator GenerateRelationshipsRoutine(string inHandLabel)
    {
        // 1) Gather all recognized anchors from sceneObjManager
        var anchors = sceneObjManager.GetAllAnchors();
        List<string> itemLabels = new List<string>();
        foreach (var a in anchors)
        {
            itemLabels.Add(a.label);
        }
        // remove the "in-hand" label so it doesn't appear in the "others"
        itemLabels.Remove(inHandLabel);

        // future possible feature:
        // categorize the current user intent based on the scene context and task context -> "Compare", "Find similar", "Find task-related objects", etc. then use it as a part of the context to guide the relationship generation.
        
        string prompt = $@"
        Given this scene context: {currentSceneContext},
        the potential tasks: {currentTaskContext},
        and that the user is holding / selecting this item: {inHandLabel},

        Find objects that are most related to this {inHandLabel} in the current scene, considering:
        1. The overall scene context and task
        2. Spatial relationships
        3. Functional relationships in the context of the task
        4. Common usage patterns

        Reminder: Don't include unrelated items in the output which are not related to the current task. It should be functionally related to the {inHandLabel}.

        Choose only from these detected items: {string.Join(", ", itemLabels)}.

        Output a JSON object where each key is a related object and its value is a brief relationship description (max 5 words).
        Example format:
        {{
          ""object1"": ""used together for cooking"",
          ""object2"": ""located next to item"",
          ""object3"": ""complements main task""
        }}

        if you don't find any meaningful relationships between the {inHandLabel} and other items in the current scene, return an empty JSON object:
        {{}}
        ";

        Debug.Log("Relationships prompt:\n" + prompt);

        // 3) Call Gemini using the MakeGeminiRequest method from GeminiGeneral for concurrent API calls
        var request = geminiGeneral != null 
            ? geminiGeneral.MakeGeminiRequest(prompt, null)
            : new GeminiGeneral.RequestStatus(geminiClient.GenerateContent(prompt, null));
        
        while (!request.IsCompleted)
            yield return null;

        string rawResponse = request.Result;
        // Debug.Log($"Relationships raw response:\n{rawResponse}");

        // 4) Extract JSON portion
        string extractedJson = TryExtractJson(rawResponse);
        Debug.Log("Relationships - Extracted JSON:\n" + extractedJson);

        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("No valid JSON found in relationships response.");
            
            // Log failure to find relationships for user study
            LogUserStudy($"[ENV] [OBJECT_RELATIONSHIPS] RELATIONSHIPS_GENERATION_FAILED: Object=\"{inHandLabel}\"");
            
            yield break;
        }

        // 5) Parse to dictionary
        Dictionary<string, string> relationshipsDict = null;
        try
        {
            relationshipsDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(extractedJson);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Failed to parse relationships JSON: " + e);
        }

        // Handle empty or null relationships
        if (relationshipsDict == null || relationshipsDict.Count == 0)
        {
            Debug.Log($"No meaningful relationships found for '{inHandLabel}' in the current context.");
            
            // Log no relationships found for user study
            LogUserStudy($"[ENV] [OBJECT_RELATIONSHIPS] NO_RELATIONSHIPS_FOUND: Object=\"{inHandLabel}\"");
            
            // Clear any existing relationship lines since there are no relationships
            if (relationLineManager != null)
            {
                relationLineManager.ClearAllLines();
            }

            yield break;
        }

        // Log relationships found for user study
        StringBuilder relationshipSb = new StringBuilder();
        foreach (var kvp in relationshipsDict)
        {
            relationshipSb.Append($"{kvp.Key}=\"{kvp.Value}\" | ");
        }
        LogUserStudy($"[ENV] [OBJECT_RELATIONSHIPS] RELATIONSHIPS_FOUND: Object=\"{inHandLabel}\", Count={relationshipsDict.Count}, Relationships=\"{relationshipSb.ToString().TrimEnd(' ', '|')}\"");
        
        // 6) Show lines from this specific sphere to each related anchor
        // Instead of using GetAnchorByLabel, we'll find the anchor that matches our specific GameObject
        var myAnchor = sceneObjManager.GetAnchorByGameObject(this.gameObject);
        if (myAnchor == null)
        {
            Debug.LogWarning($"No anchor found for this sphere GameObject!");
            yield break;
        }
        
        // When relationships are generated from manually toggling a sphere, 
        // we set enableTimeout to false to prevent auto-clearing
        relationLineManager.ShowRelationships(myAnchor, relationshipsDict, anchors, false);
    }

    /// <summary>
    /// Example helper to extract the JSON portion from the Gemini response 
    /// which might contain ```json ...```.
    /// Adjust to match your actual response format.
    /// </summary>
    private string TryExtractJson(string fullResponse)
    {
        try
        {
            var root = JsonConvert.DeserializeObject<GeminiRoot>(fullResponse);
            if (root?.candidates == null || root.candidates.Count == 0)
                return null;

            string rawText = root.candidates[0].content.parts[0].text;
            if (string.IsNullOrEmpty(rawText)) 
                return null;

            if (rawText.Contains("```json"))
            {
                var splitted = rawText.Split(new[] { "```json" }, StringSplitOptions.None);
                if (splitted.Length > 1)
                {
                    var splitted2 = splitted[1].Split(new[] { "```" }, StringSplitOptions.None);
                    rawText = splitted2[0].Trim();
                }
            }
            return rawText;
        }
        catch
        {
            // fallback: raw entire text as-is
            return fullResponse;
        }
    }

    [Serializable]
    public class GeminiRoot
    {
        public List<Candidate> candidates;
    }

    [Serializable]
    public class Candidate
    {
        public Content content;
    }

    [Serializable]
    public class Content
    {
        public List<Part> parts;
    }

    [Serializable]
    public class Part
    {
        public string text;
    }

    private void ClearPreviousQuestions()
    {
        if (questionsParent == null) return;

        foreach (Transform child in questionsParent)
        {
            if (child.name == "GeminiQuestion")
            {
                Destroy(child.gameObject);
            }
        }
    }

    // -------------------------------------------------------
    // Methods to capture the camera feed & convert to Base64
    // -------------------------------------------------------
    private Texture2D CaptureFrame(RenderTexture rt)
    {
        if (rt == null)
        {
            Debug.LogWarning("No cameraRenderTex assigned.");
            return null;
        }
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        return tex;
    }

    private string ConvertTextureToBase64(Texture2D tex)
    {
        if (tex == null) return null;
        var bytes = tex.EncodeToPNG();
        return Convert.ToBase64String(bytes);
    }

    // Add new methods to handle toggle event subscription
    private void SubscribeToToggleEvents()
    {
        if (spatialUIToggle != null)
        {
            spatialUIToggle.m_ToggleChanged.AddListener(OnSphereToggled);
        }
        
        if (labelToggle != null)
        {
            labelToggle.m_ToggleChanged.AddListener(OnLabelToggled);
        }
    }

    private void UnsubscribeFromToggleEvents()
    {
        if (spatialUIToggle != null)
        {
            spatialUIToggle.m_ToggleChanged.RemoveListener(OnSphereToggled);
        }
        
        if (labelToggle != null)
        {
            labelToggle.m_ToggleChanged.RemoveListener(OnLabelToggled);
        }
    }

    // Helper method to setup DualTargetLazyFollow on a GameObject
    private void SetupDualTargetLazyFollow(GameObject target, Vector3 offset)
    {
        // Disable existing LazyFollow if any
        var existingLazyFollow = target.GetComponent<LazyFollow>();
        if (existingLazyFollow != null)
        {
            existingLazyFollow.enabled = false;
        }

        // Add or get DualTargetLazyFollow component
        var dualTargetLazyFollow = target.GetComponent<DualTargetLazyFollow>();
        if (dualTargetLazyFollow == null)
        {
            dualTargetLazyFollow = target.AddComponent<DualTargetLazyFollow>();
            
            // Configure component
            dualTargetLazyFollow.movementSpeed = 15f;
            dualTargetLazyFollow.movementSpeedVariancePercentage = 0.2f;
            dualTargetLazyFollow.minAngleAllowed = 3f;
            dualTargetLazyFollow.maxAngleAllowed = 15f;
            dualTargetLazyFollow.timeUntilThresholdReachesMaxAngle = 0.3f;
            dualTargetLazyFollow.minDistanceAllowed = 0.02f;
            dualTargetLazyFollow.maxDistanceAllowed = 0.05f;
            dualTargetLazyFollow.timeUntilThresholdReachesMaxDistance = 0.3f;
        }

        // Set the targets
        dualTargetLazyFollow.positionTarget = transform; // follow the sphere
        dualTargetLazyFollow.rotationTarget = Camera.main.transform; // look at the camera
        dualTargetLazyFollow.targetOffset = offset; // apply the offset
        dualTargetLazyFollow.enabled = true;
    }

    // Helper method to cleanup DualTargetLazyFollow on a GameObject
    private void CleanupDualTargetLazyFollow(GameObject target)
    {
        var existingLazyFollow = target.GetComponent<LazyFollow>();
        if (existingLazyFollow != null) existingLazyFollow.enabled = true;

        var dualTargetLazyFollow = target.GetComponent<DualTargetLazyFollow>();
        if (dualTargetLazyFollow != null) Destroy(dualTargetLazyFollow);
    }

    private void HandleAnchorGrabbed(SceneObjectAnchor anchor)
    {
        // Check if this is our anchor
        if (anchor.sphereObj == this.gameObject)
        {
            // Log grab action for user study
            if (labelUnderSphere != null)
            {
                LogUserStudy($"[OBJECT] OBJECT_GRABBED: Object=\"{labelUnderSphere.text}\"");
            }
            
            // Find the Menu canvas parent of InfoPanel
            Transform menuCanvas = InfoPanel.transform.parent;
            if (menuCanvas != null && menuCanvas.name == "Menu")
            {
                // // Setup DualTargetLazyFollow for menuCanvas
                // SetupDualTargetLazyFollow(menuCanvas.gameObject, menuOffset);

                // // Setup DualTargetLazyFollow for recorderToggle
                // if (recorderToggle != null) SetupDualTargetLazyFollow(recorderToggle, recorderToggleOffset);

                // // Setup DualTargetLazyFollow for objectTrackingToggle
                // if (objectTrackingToggle != null) SetupDualTargetLazyFollow(objectTrackingToggle, objectTrackingToggleOffset);

                // Unsubscribe from toggle events instead of disabling the component
                UnsubscribeFromToggleEvents();
                spatialUIToggle.enableInteraction = false;
                if (labelToggle != null) 
                {
                    labelToggle.enableInteraction = false;
                    labelToggle.gameObject.SetActive(false);
                }

                // Deactivate first two children
                if (menuCanvas.childCount >= 3)
                {
                    menuCanvas.GetChild(0).gameObject.SetActive(false);
                    menuCanvas.GetChild(1).gameObject.SetActive(false);
                    menuCanvas.GetChild(2).gameObject.SetActive(false);
                }

                // Before activating this toggle, turn off any other active ones
                DeactivateAllOtherToggles();
                
                // Update state and trigger effects
                isOn = true;
                CurrentActiveToggle = this;
                HandleToggleEffects(true);

                // Start object inspection
                OnObjectInspected(true);
            }
        }
    }

    private void HandleAnchorReleased(SceneObjectAnchor anchor)
    {
        // Check if this is our anchor
        if (anchor.sphereObj == this.gameObject)
        {
            // // Log release action for user study
            // if (labelUnderSphere != null)
            // {
            //     LogUserStudy($"OBJECT_RELEASED: Object=\"{labelUnderSphere.text}\"");
            // }
            
            // Find the Menu canvas parent of InfoPanel
            Transform menuCanvas = InfoPanel.transform.parent;
            if (menuCanvas != null && menuCanvas.name == "Menu")
            {
                // // Cleanup DualTargetLazyFollow for menuCanvas
                // CleanupDualTargetLazyFollow(menuCanvas.gameObject);

                // // Cleanup DualTargetLazyFollow for recorderToggle
                // if (recorderToggle != null) CleanupDualTargetLazyFollow(recorderToggle);

                // // Cleanup DualTargetLazyFollow for objectTrackingToggle
                // if (objectTrackingToggle != null) CleanupDualTargetLazyFollow(objectTrackingToggle);

                // Resubscribe to toggle events
                SubscribeToToggleEvents();
                spatialUIToggle.enableInteraction = true;
                if (labelToggle != null) 
                {
                    labelToggle.enableInteraction = true;
                    labelToggle.gameObject.SetActive(true);
                }

                // Reactivate first two children
                if (menuCanvas.childCount >= 3)
                {
                    menuCanvas.GetChild(0).gameObject.SetActive(true);
                    menuCanvas.GetChild(1).gameObject.SetActive(true);
                    menuCanvas.GetChild(2).gameObject.SetActive(true);
                }

                // Update state and trigger effects
                isOn = false;
                // If this was the current active toggle, clear the reference
                if (CurrentActiveToggle == this)
                {
                    CurrentActiveToggle = null;
                }
                HandleToggleEffects(false);

                // Stop object inspection
                OnObjectInspected(false);
                
                // Stop any active auto-recording
                StopAutoRecordingIfActive();
            }
        }
    }

    // Add this class to parse the JSON response
    [Serializable]
    private class PointingDescription
    {
        public string part;
        public string description;
    }
    
    [ContextMenu("ToggleInfoPanel")]
    public void ToggleInfoPanel()
    {
        SetInfoPanelVisibility(!InfoPanel.activeSelf);
    }
    
    public void SetInfoPanelVisibility(bool isVisible)
    {
        if (InfoPanel != null)
        {
            bool stateActuallyChanged = InfoPanel.activeSelf != isVisible;
            InfoPanel.SetActive(isVisible);
            
            // If hiding the panel, also hide the answer panel
            if (!isVisible && answerPanel != null)
            {
                answerPanel.SetActive(false);
            }

            if (isVisible && stateActuallyChanged) // Panel is being turned ON
            {
                if (isHandlingFunctionToggleExclusivity) return;
                isHandlingFunctionToggleExclusivity = true;

                LogUserStudy($"[OBJECT] INFO_PANEL_VISIBILITY: Object=\"{labelUnderSphere.text}\", Visible={isVisible}");

                // Deactivate Relation Toggle if it's active
                if (relationToggle != null)
                {
                    SpatialUIToggle rt = relationToggle.GetComponent<SpatialUIToggle>();
                    RelationToggleController controller = relationToggle.GetComponent<RelationToggleController>();
                    
                    if (rt != null && IsRelationToggleActive())
                    {
                        rt.PressStart(); // This will trigger controller's ToggleRelationshipLines(false)
                        rt.PressEnd();
                    }
                }

                // Deactivate Recorder Toggle if it's active
                if (IsRecorderOn() && recorderToggle != null)
                {
                    SpatialUIToggle recT = recorderToggle.GetComponent<SpatialUIToggle>();
                    if (recT != null)
                    {
                        recT.PressStart(); // This will trigger OnRecorderFunctionToggleChanged(false)
                        recT.PressEnd();
                    }
                }
                isHandlingFunctionToggleExclusivity = false;
            }
            else if (!isVisible && stateActuallyChanged) // Panel is being turned OFF
            {
                 LogUserStudy($"[OBJECT] INFO_PANEL_VISIBILITY: Object=\"{labelUnderSphere.text}\", Visible={isVisible}");
            }
        }
    }

    // Shared method to handle toggle effects for both toggles
    private void HandleToggleEffects(bool isActive)
    {
        if (isActive)
        {
            // if (!baselineModeController.baselineMode) 
            // {
            //     SetInfoPanelVisibility(true);
            // }

            UpdateRecorderToggle(true);
            UpdateQuestionToggle(true);
            UpdateRelationToggle(true);
            // Only update object tracking toggle if not in baseline mode
            if (!baselineModeController.baselineMode) {
                UpdateObjectTrackingToggle(true);
            }

            LogUserStudy($"[OBJECT] TOGGLE_ON: Object=\"{labelUnderSphere.text}\"");

            // Set the current active anchor for the HandGrabTrigger system
            SceneObjectAnchor thisAnchor = SceneObjectManager.Instance.GetAnchorByGameObject(this.gameObject);
            if (thisAnchor != null)
            {
                HandGrabTrigger.SetCurrentActiveAnchor(thisAnchor);
            }

            if (!baselineModeController.baselineMode) {

                Transform menuCanvas = InfoPanel.transform.parent;
                if (menuCanvas != null && menuCanvas.name == "Menu")
                {
                    LazyFollow lazyFollow = menuCanvas.GetComponent<LazyFollow>();
                    if (lazyFollow != null)   lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.None;

                    if (menuCanvas.childCount >= 3)
                    {
                        menuCanvas.GetChild(0).gameObject.SetActive(false);
                        menuCanvas.GetChild(1).gameObject.SetActive(false);
                        menuCanvas.GetChild(2).gameObject.SetActive(false);
                    }

                    menuCanvas.SetParent(transform);
                    menuCanvas.localPosition = menuOffset;
                    
                    // Store the original anchoredPosition of the RectTransform (not localPosition)
                    RectTransform infoPanelRect = InfoPanel.GetComponent<RectTransform>();
                    originalInfoPanelPosition = infoPanelRect.anchoredPosition;
                    
                    // Check for AnimateWindow component and modify its End Position
                    AnimateWindow animateWindow = InfoPanel.GetComponent<AnimateWindow>();
                    if (animateWindow != null)
                    {
                        // Store current values
                        Debug.Log($"AnimateWindow found. Storing original and setting new positions");
                        originalStartPosition = animateWindow.m_StartPosition;
                        originalEndPosition = animateWindow.m_EndPosition;
                        
                        // Calculate the delta vector between original start and end positions
                        Vector3 originalDelta = originalEndPosition - originalStartPosition;
                        
                        // Calculate a new start position by applying the same delta from our target position
                        Vector3 newStartPosition = menuOffsetForStatic - originalDelta;
                        
                        // Set both start and end positions for the animation
                        animateWindow.m_StartPosition = newStartPosition;
                        animateWindow.m_EndPosition = menuOffsetForStatic;
                        
                        Debug.Log($"Setting animation positions: Start={newStartPosition}, End={menuOffsetForStatic}");
                        Debug.Log($"Original delta was {originalDelta}, preserving animation movement style");
                        
                        // Force refresh the animation
                        animateWindow.enabled = false;
                        animateWindow.enabled = true;
                    }
                    else
                    {
                        // Fallback to setting anchoredPosition directly if no animation component
                        infoPanelRect.anchoredPosition = menuOffsetForStatic;
                        Debug.Log($"No AnimateWindow found. Directly setting anchoredPosition to {menuOffsetForStatic}");
                    }
                    
                    Debug.Log($"Original position was {originalInfoPanelPosition}");
                }
            }

            // Update context before generating questions and relationships
            UpdateSceneContext();

            // We just toggled ON this sphere: tell the menu to update the title
            if (labelUnderSphere != null)
            {
                string labelContent = labelUnderSphere.text;
                menuScript.SetMenuTitle(labelContent);

                // 1) Generate possible user questions for this object (Granularity Lv1-style)
                // Stop any existing question generation coroutine
                if (activeQuestionGenerationCoroutine != null)
                {
                    StopCoroutine(activeQuestionGenerationCoroutine);
                }
                // Clear any existing questions first to prevent overlap
                ClearPreviousQuestions();
                // Start a new coroutine and track it
                activeQuestionGenerationCoroutine = StartCoroutine(GenerateQuestionsRoutine(labelContent));

                // 2) Also generate relationships with other items (Granularity Lv2)
                // This is now controlled by the relationToggle instead of being automatic
                // if (!baselineModeController.baselineMode)
                // {
                //     StartCoroutine(GenerateRelationshipsRoutine(labelContent));
                // }

                // Check if the relation toggle is on, and if so, generate relationships
                if (relationToggle != null)
                {
                    SpatialUIToggle toggle = relationToggle.GetComponent<SpatialUIToggle>();
                    RelationToggleController controller = relationToggle.GetComponent<RelationToggleController>();
                    
                    if (toggle != null && toggle.m_Active && controller != null)
                    {
                        // Ensure owner reference is updated
                        controller.UpdateOwner(this);
                        // Use the controller to toggle relationship lines
                        controller.ToggleRelationshipLines(true);
                    }
                }
            }

            var childLazyFollow = this.GetComponentInChildren<LazyFollow>();
            if (childLazyFollow != null)
            {
                // enable lazyFollow
                childLazyFollow.enabled = true;
            }
        }
        else
        {
            // // Turn OFF the info panel
            // if (!baselineModeController.baselineMode) 
            // {
            //     SetInfoPanelVisibility(false);
            // }
            
            UpdateRecorderToggle(false);
            UpdateQuestionToggle(false);
            UpdateRelationToggle(false);
            UpdateObjectTrackingToggle(false);

            LogUserStudy($"[OBJECT] TOGGLE_OFF: Object=\"{labelUnderSphere.text}\"");
            
            // Stop any active coroutines
            if (activeQuestionGenerationCoroutine != null)
            {
                StopCoroutine(activeQuestionGenerationCoroutine);
                activeQuestionGenerationCoroutine = null;
            }
            
            if (activeRelationshipQuestionCoroutine != null)
            {
                StopCoroutine(activeRelationshipQuestionCoroutine);
                activeRelationshipQuestionCoroutine = null;
            }

            // Clear the current active anchor when toggle is turned off
            HandGrabTrigger.SetCurrentActiveAnchor(null);

            if (!baselineModeController.baselineMode) {

                Transform menuCanvas = InfoPanel.transform.parent;
                if (menuCanvas != null && menuCanvas.name == "Menu")
                {
                    menuCanvas.SetParent(null);

                    LazyFollow lazyFollow = menuCanvas.GetComponent<LazyFollow>();
                    if (lazyFollow != null) 
                    {
                        // IMPROVED FIX: Force a reset by disabling and re-enabling the component
                        // This will trigger the initialization behavior similar to what happens with the recorder toggle
                        lazyFollow.enabled = false;
                        
                        // Configure optimal parameters before re-enabling
                        lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;
                        lazyFollow.minDistanceAllowed = 0.01f;  // Smaller value to detect small movements
                        lazyFollow.maxDistanceAllowed = 0.1f;   // Max distance as shown in screenshot
                        lazyFollow.timeUntilThresholdReachesMaxDistance = 0.5f;  // Time threshold as shown
                        lazyFollow.movementSpeed = 8f;  // Movement speed as shown in screenshot
                        lazyFollow.movementSpeedVariancePercentage = 0.5f; // Variance as shown
                        
                        // Set rotation parameters exactly as shown in screenshot
                        lazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAt;
                        lazyFollow.minAngleAllowed = 0.01f;
                        lazyFollow.maxAngleAllowed = 1f;
                        lazyFollow.timeUntilThresholdReachesMaxAngle = 0.5f;
                        
                        // Wait a frame to ensure the position change is registered
                        lazyFollow.enabled = true;
                        
                        // Force the target to be the camera explicitly
                        if (Camera.main != null)
                        {
                            lazyFollow.target = Camera.main.transform;
                            // Force a snap to target position
                            lazyFollow.snapOnEnable = true;
                        }
                        
                        Debug.Log("Reset LazyFollow on menuCanvas to force immediate following");
                    }

                    if (menuCanvas.childCount >= 3)
                    {
                        menuCanvas.GetChild(0).gameObject.SetActive(true);
                        menuCanvas.GetChild(1).gameObject.SetActive(true);
                        menuCanvas.GetChild(2).gameObject.SetActive(true);
                    }

                    // Restore the AnimateWindow end position if it exists
                    AnimateWindow animateWindow = InfoPanel.GetComponent<AnimateWindow>();
                    if (animateWindow != null)
                    {
                        Debug.Log($"Restoring original AnimateWindow positions: Start={originalStartPosition}, End={originalEndPosition}");
                        
                        // Restore the original start and end positions
                        animateWindow.m_StartPosition = originalStartPosition;
                        animateWindow.m_EndPosition = originalEndPosition;
                        
                        // Force refresh the animation
                        animateWindow.enabled = false;
                        animateWindow.enabled = true;
                    }
                    else
                    {
                        // Fallback to setting anchoredPosition directly
                        RectTransform infoPanelRect = InfoPanel.GetComponent<RectTransform>();
                        infoPanelRect.anchoredPosition = originalInfoPanelPosition;
                        Debug.Log($"Restoring InfoPanel anchoredPosition to {originalInfoPanelPosition}");
                    }
                }
            }

            // Clear any existing relationship lines
            if (relationLineManager != null)
            {
                relationLineManager.ClearAllLines();
            }

            // Ensure the anchor's lazy follow is enabled when tracking is turned off
            var anchorLazyFollow = this.GetComponent<LazyFollow>();
            if (anchorLazyFollow != null)
            {
                anchorLazyFollow.enabled = true;
            }

            var childLazyFollow = this.GetComponentInChildren<LazyFollow>(); // the lazyFollow on the label object
            if (childLazyFollow != null)
            {
                // disable lazyFollow
                childLazyFollow.enabled = false;
            }
        }
    }

    /// <summary>
    /// Generates relationship between this object and another nearby object.
    /// Called when two objects come within proximity threshold of each other.
    /// </summary>
    public void GenerateProximityRelationship(string nearbyObjectLabel)
    {
        if (string.IsNullOrEmpty(nearbyObjectLabel) || labelUnderSphere == null)
        {
            Debug.LogWarning("Cannot generate proximity relationship: Missing label information");
            return;
        }

        // Don't generate relationships in baseline mode
        if (baselineModeController.baselineMode)
        {
            return;
        }

        string thisObjectLabel = labelUnderSphere.text;
        
        Debug.Log($"Generating proximity relationship between '{thisObjectLabel}' and '{nearbyObjectLabel}'");
        
        // Log proximity relationship request for user study
        LogUserStudy($"[ENV] [PROXIMITY] PROXIMITY_RELATIONSHIP: Source=\"{thisObjectLabel}\", Target=\"{nearbyObjectLabel}\"");
        
        // Start the coroutine to generate relationship specifically between these two objects
        StartCoroutine(GenerateSpecificRelationshipRoutine(thisObjectLabel, nearbyObjectLabel));
    }

    /// <summary>
    /// Coroutine that calls Gemini to find the relationship between two specific objects,
    /// and draws a line between them with the relationship description.
    /// </summary>
    private IEnumerator GenerateSpecificRelationshipRoutine(string objectA, string objectB)
    {
        // Stop any existing coroutines to prevent multiple question generation
        if (activeQuestionGenerationCoroutine != null)
        {
            StopCoroutine(activeQuestionGenerationCoroutine);
            activeQuestionGenerationCoroutine = null;
        }
        
        if (activeRelationshipQuestionCoroutine != null)
        {
            StopCoroutine(activeRelationshipQuestionCoroutine);
            activeRelationshipQuestionCoroutine = null;
        }
        
        // Update scene context to ensure we have the latest context
        UpdateSceneContext();
        
        // Find the anchor for this object
        var myAnchor = sceneObjManager.GetAnchorByGameObject(this.gameObject);
        if (myAnchor == null)
        {
            Debug.LogWarning($"No anchor found for this sphere GameObject!");
            yield break;
        }

        // Find the anchor for the nearby object
        var otherAnchor = sceneObjManager.GetAnchorByLabel(objectB);
        if (otherAnchor == null)
        {
            Debug.LogWarning($"No anchor found for nearby object '{objectB}'!");
            yield break;
        }

        // Check if a relationship already exists
        string previousRelationship = null;
        bool isUpdatingExistingRelationship = false;
        
        if (relationLineManager != null)
        {
            previousRelationship = relationLineManager.GetExistingRelationship(myAnchor, otherAnchor);
            isUpdatingExistingRelationship = (previousRelationship != null);
            
            if (isUpdatingExistingRelationship)
            {
                Debug.Log($"Updating existing relationship '{previousRelationship}' between '{objectA}' and '{objectB}'");
            }
        }

        // Clear any existing relationship lines to focus on this new relationship
        if (relationLineManager != null)
        {
            relationLineManager.ClearAllLines();
        }

        // Create a temporary relationship dictionary with "Loading..." text
        Dictionary<string, string> loadingDict = new Dictionary<string, string>
        {
            { objectB, "..." } // or "Loading..."
        };
        
        // Show initial connection line with loading text
        // Explicitly set enableTimeout to false for proximity relationships (user-initiated)
        var allAnchors = sceneObjManager.GetAllAnchors();
        relationLineManager.ShowRelationships(myAnchor, loadingDict, allAnchors, false);
        
        // Build prompt specifically for these two objects
        string prompt = $@"
        Given this scene context: {currentSceneContext},
        the potential tasks: {currentTaskContext},
        
        Find the specific relationship between these two objects that have been brought close together:
        1. {objectA}
        2. {objectB}

        Consider:
        1. How these objects might be used together in the current context
        2. Functional relationships between them
        3. Causal relationships (one affects the other)
        4. Hierarchical relationships (one is part of the other)
        5. Common usage patterns
        6. if {objectA} and {objectB} are medicine or supplements, can they be used together? (e.g. like medicine or supplements, for the safety of the user can they be used together?) 

        Output a single JSON object with ONE key-value pair where:
        - Key is ""{objectB}"" (the exact string)
        - Value is a brief relationship description (5-7 words maximum)

        Example format:
        {{
          ""{objectB}"": ""can be used to clean {objectA}""
        }}

        If there is no meaningful relationship, use this format:
        {{
          ""{objectB}"": ""no clear relationship found""
        }}
        ";

        // Call Gemini using the MakeGeminiRequest method from GeminiGeneral for concurrent API calls
        var request = geminiGeneral != null 
            ? geminiGeneral.MakeGeminiRequest(prompt, null)
            : new GeminiGeneral.RequestStatus(geminiClient.GenerateContent(prompt, null));
        
        while (!request.IsCompleted)
            yield return null;

        string rawResponse = request.Result;
        
        // Extract JSON portion
        string extractedJson = TryExtractJson(rawResponse);
        Debug.Log("Proximity Relationship - Extracted JSON:\n" + extractedJson);

        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("No valid JSON found in proximity relationship response.");
            
            // Update with a fallback message
            Dictionary<string, string> fallbackDict = new Dictionary<string, string>
            {
                { objectB, "Relationship undefined" }
            };
            relationLineManager.ShowRelationships(myAnchor, fallbackDict, allAnchors, false); // Disable timeout
            yield break;
        }

        // Parse to dictionary
        Dictionary<string, string> relationshipDict = null;
        try
        {
            relationshipDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(extractedJson);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Failed to parse relationship JSON: " + e);
            
            // Update with error message
            Dictionary<string, string> errorDict = new Dictionary<string, string>
            {
                { objectB, "Error identifying relationship" }
            };
            relationLineManager.ShowRelationships(myAnchor, errorDict, allAnchors, false); // Disable timeout
            yield break;
        }

        // Handle empty or null relationships
        if (relationshipDict == null || relationshipDict.Count == 0)
        {
            Debug.Log($"No relationship found between '{objectA}' and '{objectB}'");
            
            // Update with no relationship message
            Dictionary<string, string> noRelationDict = new Dictionary<string, string>
            {
                { objectB, "No relationship found" }
            };
            relationLineManager.ShowRelationships(myAnchor, noRelationDict, allAnchors, false); // Disable timeout
            yield break;
        }

        // Get the current relationship
        string currentRelationship = relationshipDict[objectB];
        
        // Show the final relationship with proper text - explicitly disable timeout
        relationLineManager.ShowRelationships(myAnchor, relationshipDict, allAnchors, false);

        // Check if we should generate new questions based on the relationship
        // Only generate new questions if:
        // 1. This is a new relationship (not updating an existing one)
        // 2. Or the relationship text has changed
        if (!isUpdatingExistingRelationship || 
            (previousRelationship != null && currentRelationship != previousRelationship))
        {
            // After showing relationship line, generate questions about the relationship
            Vector3 midpoint = (myAnchor.sphereObj.transform.position + otherAnchor.sphereObj.transform.position) * 0.5f;
            GenerateRelationshipQuestionsAndPositionMenu(objectA, objectB, currentRelationship, midpoint);
        }
        else
        {
            Debug.Log($"Relationship unchanged, reusing existing questions for '{objectA}' and '{objectB}'");
        }
    }

    /// <summary>
    /// Clears relationship lines specifically with the object of the given label.
    /// Called when objects move far apart and the relationship should be removed.
    /// </summary>
    public void ClearSpecificRelationship(string targetObjectLabel)
    {
        if (relationLineManager == null)
        {
            Debug.LogWarning("Cannot clear relationship: No relationLineManager reference");
            return;
        }
        
        // Get anchor for this sphere
        var myAnchor = sceneObjManager.GetAnchorByGameObject(this.gameObject);
        if (myAnchor == null)
        {
            Debug.LogWarning($"Cannot clear relationship: No anchor found for this sphere");
            return;
        }
        
        // Get anchor for the target object
        var targetAnchor = sceneObjManager.GetAnchorByLabel(targetObjectLabel);
        if (targetAnchor == null)
        {
            Debug.LogWarning($"Cannot clear relationship: No anchor found for '{targetObjectLabel}'");
            return;
        }
        
        // Clear just the line between these two anchors
        relationLineManager.ClearSpecificLine(myAnchor, targetAnchor);
        
        // Stop any active relationship question coroutine
        if (activeRelationshipQuestionCoroutine != null)
        {
            StopCoroutine(activeRelationshipQuestionCoroutine);
            activeRelationshipQuestionCoroutine = null;
        }
        
        // Reset the menu canvas to original position and hide the questions
        if (menuScript != null && menuScript.transform.parent != null && questionsParent != null)
        {
            // Hide the questions panel
            questionsParent.gameObject.SetActive(false);
            
            // Hide answer panel if it exists
            if (answerPanel != null)
            {
                answerPanel.SetActive(false);
            }
            
            // Clear any questions
            ClearPreviousQuestions();
        }
        
        Debug.Log($"Cleared relationship line between '{labelUnderSphere.text}' and '{targetObjectLabel}'");
    }

    // New method to start automatic recording
    private void StartAutoRecording()
    {
        // If already auto-recording, don't start a new recording
        if (isAutoRecording)
        {
            Debug.Log("Auto-recording already in progress. Not starting a new one.");
            return;
        }
        
        // Get the recorder toggle component
        if (recorderToggle != null)
        {
            // Check if the recorder is already in recording state (may have been manually started)
            SpatialUIToggle toggle = recorderToggle.GetComponent<SpatialUIToggle>();
            SpeechToTextRecorder recorderComponent = recorderToggle.GetComponent<SpeechToTextRecorder>();
            if (recorderComponent == null && recorderToggle.transform.parent != null)
            {
                recorderComponent = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
            }
            
            bool alreadyRecording = false;
            if (recorderComponent != null)
            {
                // Try to determine if it's recording using reflection
                var isRecordingField = recorderComponent.GetType().GetField("isRecording", 
                    System.Reflection.BindingFlags.Instance | 
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                    
                if (isRecordingField != null)
                {
                    alreadyRecording = (bool)isRecordingField.GetValue(recorderComponent);
                }
            }
            
            // Only toggle if not already recording
            if (!alreadyRecording && toggle != null)
            {
                // Update tracking variables
                isAutoRecording = true;
                lastAutoRecordTime = Time.time;
                
                // Log auto-recording start for user study
                if (lastRecordedPart != null && labelUnderSphere != null)
                {
                    LogUserStudy($"[DETAIL] [POINTING] AUTO_RECORDING_STARTED: Object=\"{labelUnderSphere.text}\", Part=\"{lastRecordedPart}\"");
                }
                
                // Simulate pressing the recorder toggle button
                toggle.PressStart();
                toggle.PressEnd();
                
                Debug.Log($"Auto-recording started for part: {lastRecordedPart}");
                
                // Start coroutine to automatically stop recording after the specified duration
                if (autoRecordingStopCoroutine != null)
                {
                    StopCoroutine(autoRecordingStopCoroutine);
                }
                autoRecordingStopCoroutine = StartCoroutine(StopAutoRecordingAfterDelay(autoRecordDuration));
                
                // Highlight the recorder toggle to show it's active
                HighlightRecorderToggle(true);
            }
            else if (alreadyRecording)
            {
                // If already recording, just update the state without toggling
                Debug.Log("Recording already active. Just updating state tracking.");
                isAutoRecording = true;
                lastAutoRecordTime = Time.time;
                
                // Check if we need to restart the auto-stop coroutine
                if (autoRecordingStopCoroutine == null)
                {
                    autoRecordingStopCoroutine = StartCoroutine(StopAutoRecordingAfterDelay(autoRecordDuration));
                }
                
                // Make sure highlight is active
                HighlightRecorderToggle(true);
            }
        }
    }
    
    // Coroutine to stop auto-recording after a delay
    private IEnumerator StopAutoRecordingAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        // Simulate pressing the recorder toggle button to stop recording
        if (recorderToggle != null)
        {
            SpatialUIToggle toggle = recorderToggle.GetComponent<SpatialUIToggle>();
            SpeechToTextRecorder recorderComponent = recorderToggle.GetComponent<SpeechToTextRecorder>();
            if (recorderComponent == null && recorderToggle.transform.parent != null)
            {
                recorderComponent = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
            }
            
            bool currentlyRecording = false;
            if (recorderComponent != null)
            {
                // Try to determine if it's recording using reflection
                var isRecordingField = recorderComponent.GetType().GetField("isRecording", 
                    System.Reflection.BindingFlags.Instance | 
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                    
                if (isRecordingField != null)
                {
                    currentlyRecording = (bool)isRecordingField.GetValue(recorderComponent);
                }
            }
            
            // Only toggle if actually recording
            if (currentlyRecording && toggle != null)
            {
                // // Log auto-recording stop for user study
                // if (labelUnderSphere != null)
                // {
                //     LogUserStudy($"AUTO_RECORDING_STOPPED: Object=\"{labelUnderSphere.text}\", Duration={delay}s");
                // }
                
                // Simulate button press sequence
                toggle.PressStart();
                toggle.PressEnd();
                
                Debug.Log($"Auto-recording stopped after {delay} seconds");
            }
        }
        
        // Reset auto-recording flag
        isAutoRecording = false;
        autoRecordingStopCoroutine = null;
        
        // Remove highlight from recorder toggle
        HighlightRecorderToggle(false);
    }
    
    // Helper method to visually highlight the recorder toggle when auto-recording
    private void HighlightRecorderToggle(bool highlight)
    {
        if (recorderToggle == null)
            return;
            
        // Get the toggle component
        SpatialUIToggle toggle = recorderToggle.GetComponent<SpatialUIToggle>();
        if (toggle != null)
        {
            // Save the original material when first highlighting
            if (highlight && originalRecorderMaterial == null)
            {
                // Try to get the renderer
                MeshRenderer renderer = recorderToggle.GetComponent<MeshRenderer>();
                if (renderer == null)
                {
                    renderer = recorderToggle.GetComponentInChildren<MeshRenderer>();
                }
                
                if (renderer != null && renderer.material != null)
                {
                    originalRecorderMaterial = renderer.material;
                    
                    // Create a new material based on the original but with the highlight color
                    Material highlightMaterial = new Material(originalRecorderMaterial);
                    highlightMaterial.color = autoRecordingActiveColor;
                    renderer.material = highlightMaterial;
                }
            }
            // Restore the original material when un-highlighting
            else if (!highlight && originalRecorderMaterial != null)
            {
                MeshRenderer renderer = recorderToggle.GetComponent<MeshRenderer>();
                if (renderer == null)
                {
                    renderer = recorderToggle.GetComponentInChildren<MeshRenderer>();
                }
                
                if (renderer != null)
                {
                    renderer.material = originalRecorderMaterial;
                    originalRecorderMaterial = null;
                }
            }
            
            // Also use the toggle's built-in visual state
            if (highlight)
            {
                toggle.PassiveToggleWithoutInvokeOn();
            }
            else
            {
                toggle.PassiveToggleWithoutInvokeOff();
            }
        }
    }
    
    // Method to update recorder toggle position when pointing is active
    private void UpdateRecorderPositionOnPointing()
    {
        if (recorderToggle == null || pointingPlane == null || !currentlyPointing)
            return;
            
        recorderToggle.transform.SetParent(pointingPlane.transform);
        // recorderToggle.transform.localScale = recorderToggle.transform.localScale / pointingPlane.transform.localScale.x;
        recorderToggle.GetComponent<SpatialUI>().UpdateReferenceScale();
        
        // Update reference scale
        var spatialUI = recorderToggle.GetComponent<SpatialUI>();
        if (spatialUI != null)
        {
            spatialUI.UpdateReferenceScale();
        }
        
        // Position the recorder relative to the pointing plane
        // Use the value directly, positive = above the plane, negative = below the plane
        recorderToggle.transform.localPosition = new Vector3(0f, recorderOffsetFromPointingPlane, 0f);
        recorderToggle.transform.localRotation = Quaternion.identity;
        
        // Disable LazyFollow on the recorder to prevent conflicts
        var lazyFollow = recorderToggle.GetComponent<LazyFollow>();
        if (lazyFollow != null)
        {
            lazyFollow.enabled = false;
        }
        
        // Since the pointing plane is now a child of the holding hand and has fixed position,
        // we should ensure the recorder looks toward the camera
        var dualLazyFollow = recorderToggle.GetComponent<DualTargetLazyFollow>();
        if (dualLazyFollow == null)
        {
            dualLazyFollow = recorderToggle.AddComponent<DualTargetLazyFollow>();
            
            // Configure the dual target lazy follow component
            dualLazyFollow.movementSpeed = 15f;
            dualLazyFollow.movementSpeedVariancePercentage = 0.2f;
            dualLazyFollow.minAngleAllowed = 3f;
            dualLazyFollow.maxAngleAllowed = 15f;
            dualLazyFollow.timeUntilThresholdReachesMaxAngle = 0.3f;
            
            // Set to only use rotation follow mode, not position
            dualLazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.None;
            dualLazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAt;
            
            // Set the rotation target to look at the camera
            dualLazyFollow.rotationTarget = Camera.main.transform;
        }
        else
        {
            // Make sure it's enabled and properly configured
            dualLazyFollow.enabled = true;
            dualLazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.None;
            dualLazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAt;
            dualLazyFollow.rotationTarget = Camera.main.transform;
        }
        
        // Ensure the recorder still has the correct object label association
        SpeechToTextRecorder recorderComponent = recorderToggle.GetComponent<SpeechToTextRecorder>();
        if (recorderComponent == null && recorderToggle.transform.parent != null)
        {
            recorderComponent = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
        }
        
        if (recorderComponent != null && labelUnderSphere != null)
        {
            // This will update the object label without changing the parent again
            recorderComponent.SetObjectLabel(labelUnderSphere.text, this.gameObject);
        }
        
        // Log the actual position used
        Debug.Log($"Repositioned recorder toggle with offset {recorderOffsetFromPointingPlane} relative to pointing plane");
    }

    // Helper method to stop auto-recording if it's currently active
    private void StopAutoRecordingIfActive()
    {
        if (isAutoRecording)
        {
            // Stop the auto-recording coroutine if it's running
            if (autoRecordingStopCoroutine != null)
            {
                StopCoroutine(autoRecordingStopCoroutine);
                autoRecordingStopCoroutine = null;
            }
            
            // Simulate pressing the recorder toggle button to stop recording
            if (recorderToggle != null)
            {
                SpeechToTextRecorder recorderComponent = recorderToggle.GetComponent<SpeechToTextRecorder>();
                if (recorderComponent == null && recorderToggle.transform.parent != null)
                {
                    recorderComponent = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
                }
                
                bool currentlyRecording = false;
                if (recorderComponent != null)
                {
                    // Try to determine if it's recording using reflection
                    var isRecordingField = recorderComponent.GetType().GetField("isRecording", 
                        System.Reflection.BindingFlags.Instance | 
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public);
                        
                    if (isRecordingField != null)
                    {
                        currentlyRecording = (bool)isRecordingField.GetValue(recorderComponent);
                    }
                }
                
                // Only stop if actually recording
                if (currentlyRecording)
                {
                    SpatialUIToggle toggle = recorderToggle.GetComponent<SpatialUIToggle>();
                    if (toggle != null)
                    {
                        // Simulate button press sequence
                        toggle.PressStart();
                        toggle.PressEnd();
                        
                        Debug.Log("Auto-recording stopped due to object being disabled or released");
                    }
                }
            }
            
            isAutoRecording = false;
            
            // Reset toggle visual state
            HighlightRecorderToggle(false);
        }
    }

    // Make sure to handle cleanup if object is disabled while auto-recording
    private void OnDisable()
    {
        if (sceneContextManager != null)
        {
            sceneContextManager.OnSceneContextComplete -= HandleSceneAnalysis;
        }

        // Unsubscribe from all events
        HandGrabTrigger.OnAnchorGrabbed -= HandleAnchorGrabbed;
        HandGrabTrigger.OnAnchorReleased -= HandleAnchorReleased;
        UnsubscribeFromToggleEvents();
        OnPointingStateChanged -= OnPointingStateHandler;

        // Make sure to set pointing state to false when disabled
        if (currentlyPointing)
        {
            currentlyPointing = false;
            OnPointingStateChanged?.Invoke(false);
        }

        if (pointingPlane != null)
        {
            pointingPlane.SetActive(false);
        }
        
        // Clean up auto-recording if active
        StopAutoRecordingIfActive();
    }

    /// <summary>
    /// Specialized version of GenerateQuestionsRoutine that focuses on a specific part of an object
    /// being pointed at by the user.
    /// </summary>
    private IEnumerator GeneratePointingQuestionsRoutine(string objectLabel, string partName, string partDescription)
    {
        // 1) Capture the camera frame -> Base64
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        string base64Image = ConvertTextureToBase64(frameTex);
        Destroy(frameTex);  // free the temporary texture

        // 2) Build a specialized prompt that references the specific part being pointed at
        string prompt = $@"
            Given the current scene context: {currentSceneContext},
            and the potential tasks: {currentTaskContext},
            and that the user is holding / selecting a '{objectLabel}',
            and is SPECIFICALLY POINTING at the '{partName}' part of this object,
            which is described as: '{partDescription}',

            Please return a JSON list of possible user questions about this SPECIFIC PART of the object.
            Focus on questions that are relevant to:
            1. The specific functionality or purpose of this particular part
            2. How this part relates to the overall object
            3. How to use or interact with this specific part
            4. Any issues or considerations specific to this part
            5. How this part might be relevant to the current task context

            IMPORTANT: Each question MUST be very concise - less than 10 words total.
            Make each question as short as possible while still being clear.
            Focus on brevity and directness.

            Return only the most likely questions, up to 5 maximum.
            Focus on questions that users would genuinely want answers to about this specific part.

            In the format:
            json
            [
            ""Short question about {partName}?"",
            ""Another brief question?"",
            ...
            ]
            ";

        // 3) Call Gemini using the MakeGeminiRequest method from GeminiGeneral for concurrent API calls
        Debug.Log($"Generating specialized questions for '{partName}' part of '{objectLabel}'");
        var request = geminiGeneral != null 
            ? geminiGeneral.MakeGeminiRequest(prompt, base64Image)
            : new GeminiGeneral.RequestStatus(geminiClient.GenerateContent(prompt, base64Image));

        while (!request.IsCompleted)
            yield return null;

        string geminiResponse = request.Result;

        // 4) Extract JSON
        string extractedJson = TryExtractJson(geminiResponse);
        Debug.Log($"Pointing-specific Questions Response - Extracted JSON:\n{extractedJson}");

        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("Could not find valid JSON block in Gemini question response for pointing.");
            
            // Log failure for user study
            LogUserStudy($"[DETAIL] [POINTING] PART_QUESTIONS_GENERATION_FAILED: Object=\"{objectLabel}\", Part=\"{partName}\"");
            
            yield break;
        }

        // This is our final array of question strings
        List<string> questionsList = null;
        try
        {
            questionsList = JsonConvert.DeserializeObject<List<string>>(extractedJson);
            
            // Log success for user study
            if (questionsList != null && questionsList.Count > 0)
            {
                LogUserStudy($"[DETAIL] [POINTING] PART_QUESTIONS_GENERATED: Object=\"{objectLabel}\", Part=\"{partName}\", Count={questionsList.Count}, Questions=\"{string.Join(" | ", questionsList)}\"");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse pointing-specific question array: {e}");
            yield break;
        }

        // 5) Instantiate UI elements for each question
        ClearPreviousQuestions();

        if (questionsList != null && questionsList.Count > 0)
        {
            float currentY = -60f;  // Start at the top
            float questionHeight = 54f;  // Height of each question block, adjust as needed
            float spacing = 0f;  // Space between questions (reduced by 0.5x from 5f)

            foreach (var q in questionsList)
            {
                // Instantiate your question prefab 
                var go = Instantiate(questionPrefab, questionsParent);
                go.name = "GeminiQuestion";

                // Position 
                Transform t = go.transform;
                if (t != null)
                {
                    t.localPosition = new Vector3(0f, -currentY, 0f);
                    currentY += questionHeight + spacing;
                }

                // Set the text inside
                TextMeshPro txt = go.GetComponentInChildren<TextMeshPro>();
                if (txt != null) txt.text = q;

                // Add button press handling
                var button = go.GetComponent<SpatialUIButton>();
                if (button != null)
                {
                    string questionText = q; // closure
                    button.WasPressed += (buttonText, renderer, index) =>
                    {
                        // clear the previous answer in the answer panel: set the text to "Generating..."
                        answerPanel.GetComponentInChildren<TextMeshPro>().text = "Generating...";

                        if (questionAnswerer != null)
                        {
                            questionAnswerer.RequestAnswer(questionText);
                            answerPanel.SetActive(true);
                        }
                        else
                        {
                            Debug.LogWarning("No QuestionAnswerer reference set.");
                        }
                    };
                }
                else
                {
                    Debug.LogWarning("Question prefab is missing SpatialUIButton component.");
                }
            }
        }
    }

    /// <summary>
    /// Generates questions based on the relationship between two objects and positions the menu
    /// at the side of the relationship line's midpoint.
    /// </summary>
    private void GenerateRelationshipQuestionsAndPositionMenu(string objectA, string objectB, string relationshipDesc, Vector3 midpoint)
    {
        // Make sure we have a menu canvas and questions parent
        if (menuScript == null || menuScript.transform.parent == null || questionsParent == null)
        {
            Debug.LogWarning("Cannot generate relationship questions: menuScript or questionsParent is null");
            return;
        }

        // Get menu canvas transform
        Transform menuCanvas = menuScript.transform.parent;
        
        // Position menu canvas near the midpoint of the relationship line, but offset to the side
        Vector3 cameraPos = Camera.main.transform.position;
        Vector3 directionToCamera = (cameraPos - midpoint).normalized;
        
        // Create a perpendicular vector (side offset direction)
        Vector3 perpendicularDir = Vector3.Cross(Vector3.up, directionToCamera).normalized;
        
        // Position the menu to the side of the relationship line
        Vector3 menuPosition = midpoint + perpendicularDir * 0.1f + Vector3.up * 0.05f;
        
        // Set menu canvas position and make sure it's not parented to anything
        menuCanvas.SetParent(null);
        menuCanvas.position = menuPosition;
        
        // Look at camera
        menuCanvas.LookAt(cameraPos);
        
        // Activate the menu canvas and questions parent
        menuCanvas.gameObject.SetActive(true);
        questionsParent.gameObject.SetActive(true);
        
        // Clear previous questions using the existing method
        ClearPreviousQuestions();
        
        // Stop any existing relationship question coroutine
        if (activeRelationshipQuestionCoroutine != null)
        {
            StopCoroutine(activeRelationshipQuestionCoroutine);
        }
        
        // Start generating questions about the relationship and track the coroutine
        activeRelationshipQuestionCoroutine = StartCoroutine(GenerateRelationshipQuestionsRoutine(objectA, objectB, relationshipDesc));
    }

    /// <summary>
    /// Coroutine to generate questions about the relationship between two objects
    /// </summary>
    private IEnumerator GenerateRelationshipQuestionsRoutine(string objectA, string objectB, string relationshipDesc)
    {
        Debug.Log($"Generating questions about relationship between '{objectA}' and '{objectB}': '{relationshipDesc}'");
        
        // Build a prompt for Gemini to generate questions about the relationship
        string prompt = $@"
            Given this scene context: {currentSceneContext},
            the potential tasks: {currentTaskContext},
            
            The user has brought two objects close together:
            1. {objectA}
            2. {objectB}
            
            Their relationship is: ""{relationshipDesc}""
            
            Please return a JSON list of possible user questions about this relationship.
            Focus on questions that are relevant to:
            
            1. How these objects are used together
            2. Why this relationship is important in the current context
            3. What the user might want to know about this specific interaction
            4. Safety or compatibility considerations
            5. Practical advice about using these items together
            
            IMPORTANT: Each question MUST be very concise - less than 10 words total.
            Make each question as short as possible while still being clear.
            Focus on brevity and directness.
            
            Return only the most likely questions, up to 5 maximum.
            Focus on questions that users would genuinely want answers to.
            
            In the format:
            json
            [
            ""Short question 1?"",
            ""Short question 2?"",
            ...
            ]
        ";
        
        // Call Gemini using the appropriate method for this class
        var request = geminiGeneral != null 
            ? geminiGeneral.MakeGeminiRequest(prompt, null)
            : new GeminiGeneral.RequestStatus(geminiClient.GenerateContent(prompt, null));
        
        // Wait for completion
        while (!request.IsCompleted)
            yield return null;
        
        string geminiResponse = request.Result;
        Debug.Log($"Received response from Gemini for relationship questions: {geminiResponse}");
        
        // Extract JSON
        string extractedJson = TryExtractJson(geminiResponse);
        Debug.Log($"Extracted JSON for relationship questions: {extractedJson}");
        
        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("Could not find valid JSON block in Gemini relationship questions response.");
            yield break;
        }
        
        // Parse the JSON into a list of questions
        List<string> questionsList = null;
        try
        {
            questionsList = JsonConvert.DeserializeObject<List<string>>(extractedJson);
            Debug.Log($"Successfully parsed {questionsList.Count} relationship questions from JSON");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse relationship question array: {e}");
            yield break;
        }
        
        // Create UI elements for each question
        if (questionsList != null && questionsList.Count > 0 && questionsParent != null)
        {
            float currentY = -60f;  // Start at the top
            float questionHeight = 54f;  // Height of each question block
            float spacing = 0f;  // Space between questions
            
            foreach (var q in questionsList)
            {
                // Skip empty questions
                if (string.IsNullOrWhiteSpace(q)) continue;
                
                // Instantiate the question prefab
                GameObject go = Instantiate(questionPrefab, questionsParent);
                go.name = "GeminiQuestion";
                
                // Position it correctly using the transform
                Transform t = go.transform;
                if (t != null)
                {
                    t.localPosition = new Vector3(0f, -currentY, 0f);
                    currentY += questionHeight + spacing;
                }
                
                // Set the text
                TextMeshPro txt = go.GetComponentInChildren<TextMeshPro>();
                if (txt != null) txt.text = q;
                
                // Add button functionality
                var button = go.GetComponent<SpatialUIButton>();
                if (button != null)
                {
                    string questionText = q;  // Capture for closure
                    button.WasPressed += (buttonText, renderer, index) =>
                    {
                        // Clear previous answer and set "Generating..."
                        if (answerPanel != null && answerPanel.GetComponentInChildren<TextMeshPro>() != null)
                        {
                            answerPanel.GetComponentInChildren<TextMeshPro>().text = "Generating...";
                        }
                        
                        // Request answer if we have a question answerer
                        if (questionAnswerer != null)
                        {
                            questionAnswerer.RequestAnswer(questionText);
                            if (answerPanel != null) answerPanel.SetActive(true);
                        }
                        else
                        {
                            Debug.LogWarning("No GeminiQuestionAnswerer reference set.");
                        }
                    };
                }
                else
                {
                    Debug.LogWarning("Question prefab is missing SpatialUIButton component.");
                }
            }
            
            Debug.Log($"Created {questionsList.Count} question UI elements for relationship");
        }
    }

    // Helper method for creating timestamped user study logs
    private void LogUserStudy(string message)
    {
        if (!enableUserStudyLogging) return;
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
    }

    // New listener for RecorderToggle state changes for exclusivity
    private void OnRecorderFunctionToggleChanged(bool isRecorderNowOn)
    {
        if (isHandlingFunctionToggleExclusivity) return;

        // We need to determine if the state *actually* changed to ON.
        // The IsRecorderOn() reflects the state *after* the toggle press.
        // So if isRecorderNowOn is true, it means it just turned on.
        if (isRecorderNowOn)
        {
            isHandlingFunctionToggleExclusivity = true;
            Debug.Log("RecorderToggle turned ON by user, ensuring other function toggles are OFF.");

            // Deactivate Question Toggle if it's active
            if (InfoPanel != null && InfoPanel.activeSelf && questionToggle != null)
            {
                SpatialUIToggle qt = questionToggle.GetComponent<SpatialUIToggle>();
                if (qt != null)
                {
                    qt.PressStart(); // Triggers SetInfoPanelVisibility(false)
                    qt.PressEnd();
                }
            }

            // Deactivate Relation Toggle if it's active
            if (relationToggle != null)
            {
                SpatialUIToggle rt = relationToggle.GetComponent<SpatialUIToggle>();
                if (rt != null && IsRelationToggleActive())
                {
                    rt.PressStart(); // This will trigger the controller's ToggleRelationshipLines(false)
                    rt.PressEnd();
                }
            }
            isHandlingFunctionToggleExclusivity = false;
        }
        // If isRecorderNowOn is false, it means it was turned off, no need to deactivate others.
    }

    // Helper method for IsRecorderOn
    private bool IsRecorderOn()
    {
        if (recorderToggle == null) return false;
        SpeechToTextRecorder recorderComponent = recorderToggle.GetComponent<SpeechToTextRecorder>();
        if (recorderComponent == null && recorderToggle.transform.parent != null)
        {
            recorderComponent = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
        }
        if (recorderComponent != null)
        {
            var isRecordingField = recorderComponent.GetType().GetField("isRecording",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            if (isRecordingField != null)
            {
                try { // Add try-catch for safety if field access fails
                    return (bool)isRecordingField.GetValue(recorderComponent);
                } catch (Exception ex) {
                    Debug.LogError($"Error accessing isRecording field: {ex.Message}");
                    return false;
                }
            }
        }
        return false; // Default if cannot determine
    }

    // Helper method to check if relation toggle is active
    private bool IsRelationToggleActive()
    {
        if (relationToggle == null) return false;
        SpatialUIToggle toggle = relationToggle.GetComponent<SpatialUIToggle>();
        if (toggle != null)
        {
            return toggle.m_Active;
        }
        return false;
    }
}
