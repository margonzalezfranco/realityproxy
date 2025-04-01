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

/// <summary>
/// Script attached to each sphere toggled in the scene. 
/// It calls Gemini to (A) generate questions about the object, and (B) show relationships with other items.
/// </summary>
public class SphereToggleScript : MonoBehaviour
{
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

    private bool isOn = false;

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
    public float recorderOffsetFromPointingPlane = -0.05f;

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

        if (recorder == null)
        {
            recorder = FindFirstObjectByType<SpeechToTextRecorder>();
        }

        if (baselineModeController == null)
        {
            baselineModeController = FindFirstObjectByType<BaselineModeController>();
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

        // Subscribe to the toggle's onValueChanged event
        SubscribeToToggleEvents();

        if (sceneContextManager != null)
        {
            sceneContextManager.OnSceneContextComplete += HandleSceneAnalysis;
        }

        // Subscribe to anchor grab/release events
        HandGrabTrigger.OnAnchorGrabbed += HandleAnchorGrabbed;
        HandGrabTrigger.OnAnchorReleased += HandleAnchorReleased;

        // Subscribe to our own pointing state event
        OnPointingStateChanged += HandlePointingStateChanged;
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
        OnPointingStateChanged -= HandlePointingStateChanged;

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

    private void OnSphereToggled(bool toggledOn)
    {
        // If we're already handling a toggle event, ignore this one to prevent loops
        if (isHandlingToggle) return;
        
        isHandlingToggle = true;
        
        // Update our internal state first
        isOn = toggledOn;
        
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

        // Update our internal state first
        isOn = toggledOn;
        
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
            if (isOn)
            {
                toggle.transform.SetParent(transform);
                if (!toggle.transform.hasChanged)  toggle.transform.localScale = toggle.transform.localScale / transform.localScale.x;
                toggle.GetComponent<SpatialUI>().UpdateReferenceScale();
                toggle.transform.localPosition = offset;
                toggle.transform.localRotation = Quaternion.identity;
                
                var lazyFollow = toggle.GetComponent<LazyFollow>();
                if (lazyFollow != null) lazyFollow.enabled = false;

                if (toggleType == "recorder" && recorder != null && labelUnderSphere != null) recorder.SetObjectLabel(labelUnderSphere.text, this.gameObject);
            }
            else
            {
                toggle.transform.SetParent(null);
                toggle.GetComponent<SpatialUI>().UpdateReferenceScale();
                toggle.transform.localPosition = Vector3.zero;
                toggle.transform.localRotation = Quaternion.identity;
                
                var lazyFollow = toggle.GetComponent<LazyFollow>();
                if (lazyFollow != null) lazyFollow.enabled = true;
                
                if (toggleType == "recorder")
                {
                    SpeechToTextRecorder recorder = toggle.GetComponent<SpeechToTextRecorder>();
                    if (recorder == null && toggle.transform.parent != null) recorder = toggle.transform.parent.GetComponent<SpeechToTextRecorder>();
                    if (recorder != null)   recorder.ResetObjectLabel();
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
                        UpdatePointingVisualization();
                        
                        // Check if this is a new part being pointed at
                        bool isNewPart = pointingInfo.part != lastRecordedPart;
                        
                        // If this is a new part and auto-recording is enabled, start recording
                        if (isNewPart && enableAutoRecordOnPointing && recorderToggle != null)
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
                    }
                    else
                    {
                        // Reset the last recorded part when not pointing
                        lastRecordedPart = "";
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

    // New method to check if hands are within proximity threshold
    private bool CheckHandsProximity()
    {
        if (handTracking == null) return false;
        
        var handSubsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(handSubsystems);
        
        if (handSubsystems.Count == 0) return false;
        
        var handSubsystem = handSubsystems[0];
        
        // Initialize pose variables
        Pose leftPose = new Pose();
        Pose rightPose = new Pose();
        
        // Get positions of both hands
        bool leftHandTracked = handSubsystem.leftHand.isTracked && 
                               handSubsystem.leftHand.GetJoint(XRHandJointID.Wrist).TryGetPose(out leftPose);
        
        bool rightHandTracked = handSubsystem.rightHand.isTracked && 
                                handSubsystem.rightHand.GetJoint(XRHandJointID.Wrist).TryGetPose(out rightPose);
        
        // Both hands must be tracked
        if (!leftHandTracked || !rightHandTracked) return false;
        
        // Calculate distance between hand wrists
        float distance = Vector3.Distance(leftPose.position, rightPose.position);
        
        // Debug.Log($"Hand distance: {distance}m, threshold: {maxHandProximityDistance}m");
        
        // Return true if hands are within the proximity threshold
        return distance <= maxHandProximityDistance;
    }

    private void UpdatePointingVisualization()
    {
        // First check if hands are in proximity - early exit if not
        if (!CheckHandsProximity())
        {
            if (pointingPlane != null && pointingPlane.activeSelf)
            {
                pointingPlane.SetActive(false);
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
                        // Ensure the plane is active
                        pointingPlane.SetActive(true);
                        
                        // Ensure the plane has the correct name for detection in SpeechToTextRecorder
                        pointingPlane.name = "PointingPlane";

                        // We need to determine if we should initialize or update the plane position
                        bool shouldInitialize = !pointingPlane.transform.IsChildOf(holdingHand.transform);
                        bool hasLazyFollow = pointingPlane.GetComponent<DualTargetLazyFollow>() != null;
                        
                        if (shouldInitialize || !hasLazyFollow)
                        {
                            // First time setup or reattaching:
                            // 1. Make pointing plane a child of the holding hand
                            pointingPlane.transform.SetParent(holdingHand.transform);
                            
                            // 2. Calculate position relative to the holding hand based on the pointing finger
                            // This gets the position of the right index finger tip relative to the left hand
                            Vector3 relativeFingerPos = holdingHand.transform.InverseTransformPoint(fingerTipPose.position);
                            relativePosition = relativeFingerPos;
                            
                            // 3. Set this position with a vertical offset
                            Vector3 fixedOffset = relativePosition + (Vector3.up * planeUpOffset);
                            pointingPlane.transform.localPosition = fixedOffset;
                            
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
                            
                            // Configure component
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
                            
                            Debug.Log("Initialized pointing plane at fixed position relative to holding hand");
                        }
                        
                        // Update recorder position after updating pointing plane
                        if (recorderToggle != null && isOn)
                        {
                            UpdateRecorderPositionOnPointing();
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

            Please predict the questions that the user truly wants to know in the current context, rather than providing irrelevant questions. Only offer questions that are genuinely valuable to the user, which means questions that they might actually want to understand.

            In the format:
            json
            [
            ""Question 1"",
            ""Question 2"",
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
            yield break;
        }

        // This is our final array of question strings
        List<string> questionsList = null;
        try
        {
            questionsList = JsonConvert.DeserializeObject<List<string>>(extractedJson);
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
            
            // Clear any existing relationship lines since there are no relationships
            if (relationLineManager != null)
            {
                relationLineManager.ClearAllLines();
            }

            yield break;
        }

        // 6) Show lines from this specific sphere to each related anchor
        // Instead of using GetAnchorByLabel, we'll find the anchor that matches our specific GameObject
        var myAnchor = sceneObjManager.GetAnchorByGameObject(this.gameObject);
        if (myAnchor == null)
        {
            Debug.LogWarning($"No anchor found for this sphere GameObject!");
            yield break;
        }
        relationLineManager.ShowRelationships(myAnchor, relationshipsDict, anchors);
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

                // Update state and trigger effects
                isOn = true;
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
                HandleToggleEffects(false);

                // Stop object inspection
                OnObjectInspected(false);
                
                // Stop any active auto-recording
                StopAutoRecordingIfActive();
            }
        }
    }

    private void HandlePointingStateChanged(bool isPointing)
    {
        if (!isPointing)
        {
            if (pointingPlane != null)
            {
                // Don't unparent the pointing plane from the holding hand
                // We want to keep the parent relationship with the holding hand at all times
                
                // Just hide the plane when not pointing
                pointingPlane.SetActive(false);
                
                // Reset relative position to force recalculation next time
                relativePosition = Vector3.zero;
            }
            
            // Reset recorder toggle position when pointing stops
            if (recorderToggle != null && isOn)
            {
                UpdateRecorderToggle(true);
            }
            
            // Stop auto-recording if active when pointing ends
            StopAutoRecordingIfActive();
            
            // Reset the last recorded part
            lastRecordedPart = "";
        }
        else
        {
            if (pointingPlane != null)
            {
                pointingPlane.SetActive(true);
                // Update the position based on the current pointing finger position
                UpdatePointingVisualization();
                
                // Update recorder toggle position when pointing starts
                if (recorderToggle != null && isOn)
                {
                    UpdateRecorderPositionOnPointing();
                }
            }
        }
    }

    // Method to update recorder toggle position when pointing is active
    private void UpdateRecorderPositionOnPointing()
    {
        if (recorderToggle == null || pointingPlane == null || !currentlyPointing)
            return;
            
        // Make the recorder toggle a child of the pointing plane
        recorderToggle.transform.SetParent(pointingPlane.transform);
        
        // Reset scale if it got changed (adaptation from UpdateTogglePosition method)
        if (!recorderToggle.transform.hasChanged)
        {
            recorderToggle.transform.localScale = recorderToggle.transform.localScale / pointingPlane.transform.localScale.x;
        }
        
        // Update reference scale
        var spatialUI = recorderToggle.GetComponent<SpatialUI>();
        if (spatialUI != null)
        {
            spatialUI.UpdateReferenceScale();
        }
        
        // Position the recorder relative to the pointing plane
        // A negative Y value positions it below the plane, positive would place it above
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
        
        Debug.Log("Repositioned recorder toggle below pointing plane");
    }

    // Add this class to parse the JSON response
    [Serializable]
    private class PointingDescription
    {
        public string part;
        public string description;
    }

    // Shared method to handle toggle effects for both toggles
    private void HandleToggleEffects(bool isActive)
    {
        if (isActive)
        {
            if (!baselineModeController.baselineMode) InfoPanel.SetActive(true);

            UpdateRecorderToggle(true);
            UpdateObjectTrackingToggle(true);

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
                StartCoroutine(GenerateQuestionsRoutine(labelContent));

                // 2) Also generate relationships with other items (Granularity Lv2)
                if (!baselineModeController.baselineMode)
                {
                    StartCoroutine(GenerateRelationshipsRoutine(labelContent));
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
            // Turn OFF
            if (!baselineModeController.baselineMode) InfoPanel.SetActive(false);
            answerPanel.SetActive(false);
            UpdateRecorderToggle(false);
            UpdateObjectTrackingToggle(false);

            // Clear the current active anchor when toggle is turned off
            HandGrabTrigger.SetCurrentActiveAnchor(null);

            if (!baselineModeController.baselineMode) {

                Transform menuCanvas = InfoPanel.transform.parent;
                if (menuCanvas != null && menuCanvas.name == "Menu")
                {
                    menuCanvas.SetParent(null);

                    LazyFollow lazyFollow = menuCanvas.GetComponent<LazyFollow>();
                    if (lazyFollow != null) lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;

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
        
        // Start the coroutine to generate relationship specifically between these two objects
        StartCoroutine(GenerateSpecificRelationshipRoutine(thisObjectLabel, nearbyObjectLabel));
    }

    /// <summary>
    /// Coroutine that calls Gemini to find the relationship between two specific objects,
    /// and draws a line between them with the relationship description.
    /// </summary>
    private IEnumerator GenerateSpecificRelationshipRoutine(string objectA, string objectB)
    {
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
        var allAnchors = sceneObjManager.GetAllAnchors();
        relationLineManager.ShowRelationships(myAnchor, loadingDict, allAnchors);
        
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
            relationLineManager.ShowRelationships(myAnchor, fallbackDict, allAnchors);
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
            relationLineManager.ShowRelationships(myAnchor, errorDict, allAnchors);
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
            relationLineManager.ShowRelationships(myAnchor, noRelationDict, allAnchors);
            yield break;
        }

        // Show the final relationship with proper text
        relationLineManager.ShowRelationships(myAnchor, relationshipDict, allAnchors);
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
        
        Debug.Log($"Cleared relationship line between '{labelUnderSphere.text}' and '{targetObjectLabel}'");
    }

    // New method to start automatic recording
    private void StartAutoRecording()
    {
        // If already auto-recording, stop the previous one first
        if (isAutoRecording && autoRecordingStopCoroutine != null)
        {
            StopCoroutine(autoRecordingStopCoroutine);
        }
        
        // Get the recorder toggle component
        if (recorderToggle != null)
        {
            // Update tracking variables
            isAutoRecording = true;
            lastAutoRecordTime = Time.time;
            
            // Simulate pressing the recorder toggle button
            SpatialUIToggle toggle = recorderToggle.GetComponent<SpatialUIToggle>();
            if (toggle != null)
            {
                // Simulate button press sequence
                toggle.PressStart();
                toggle.PressEnd();
                
                Debug.Log($"Auto-recording started for part: {lastRecordedPart}");
            }
            
            // Start coroutine to automatically stop recording after the specified duration
            autoRecordingStopCoroutine = StartCoroutine(StopAutoRecordingAfterDelay(autoRecordDuration));
            
            // Highlight the recorder toggle to show it's active
            HighlightRecorderToggle(true);
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
            if (toggle != null)
            {
                // Simulate button press sequence
                toggle.PressStart();
                toggle.PressEnd();
                
                Debug.Log($"Auto-recording stopped after {delay} seconds");
            }
        }
        
        // Reset auto-recording flag
        isAutoRecording = false;
        
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
    
    // Make sure to handle cleanup if object is destroyed while auto-recording
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
        OnPointingStateChanged -= HandlePointingStateChanged;

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
    }

    // Helper method to stop auto-recording if it's currently active
    private void StopAutoRecordingIfActive()
    {
        if (isAutoRecording)
        {
            // Simulate pressing the recorder toggle button to stop recording
            if (recorderToggle != null)
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
            
            isAutoRecording = false;
            
            if (autoRecordingStopCoroutine != null)
            {
                StopCoroutine(autoRecordingStopCoroutine);
                autoRecordingStopCoroutine = null;
            }
            
            // Reset toggle visual state
            HighlightRecorderToggle(false);
        }
    }
}
