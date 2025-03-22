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

    [Tooltip("TextMeshPro label that holds the sphere's 'name' or 'content'.")]
    public TextMeshPro labelUnderSphere;

    [Tooltip("Reference to the scene's menu. We call SetMenuTitle(...) on it.")]
    public MenuScript menuScript;

    public GameObject InfoPanel;

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
    public Vector3 menuOffset = new Vector3(-6f, 1.2f, -2.5f); // Default slightly above the anchor

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

    private Vector3 relativePosition; // Store relative position to holding hand

    public GameObject recorderToggle;

    private void Start()
    {
        if (geminiClient == null)
        {
            geminiClient = new GeminiAPI(modelName, geminiApiKey);
        }

        if (recorderToggle == null)
        {
            recorderToggle = GameObject.Find("recorderToggle");
        }

        if (recorder == null)
        {
            recorder = FindFirstObjectByType<SpeechToTextRecorder>();
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
        isOn = toggledOn;

        if (isOn)
        {
            InfoPanel.SetActive(true);

            UpdateRecorderToggle(true);

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
                StartCoroutine(GenerateRelationshipsRoutine(labelContent));
            }

            var lazyFollow = this.GetComponentInChildren<LazyFollow>();
            if (lazyFollow != null)
            {
                // enable lazyFollow
                lazyFollow.enabled = true;
            }
        }
        else
        {
            // Turn OFF
            InfoPanel.SetActive(false);
            answerPanel.SetActive(false);
            UpdateRecorderToggle(false);

            // Clear any existing relationship lines
            if (relationLineManager != null)
            {
                relationLineManager.ClearAllLines();
            }

            var lazyFollow = this.GetComponentInChildren<LazyFollow>();
            if (lazyFollow != null)
            {
                // disable lazyFollow
                lazyFollow.enabled = false;
            }
        }
    }

    private void UpdateRecorderToggle(bool isOn)
    {
        if (recorderToggle != null)
        {
            if (isOn)
            {
                // Position recorderToggle at the toggle position plus a small offset above
                Vector3 togglePosition = transform.position;
                Vector3 offsetPosition = togglePosition + new Vector3(0f, 0.07f, 0f); // Adjust the Y offset as needed
                recorderToggle.transform.position = offsetPosition;
                
                if (recorder != null && labelUnderSphere != null)
                {
                    recorder.SetObjectLabel(labelUnderSphere.text, this.gameObject);
                    Debug.Log($"Set recorder object label to: {labelUnderSphere.text}");
                }
                else if (recorder == null)
                {
                    Debug.LogWarning("SpeechToTextRecorder component not found on recorderToggle or its parent");
                }
            }
            else
            {
                // Reset recorderToggle position to origin
                recorderToggle.transform.position = Vector3.zero;
                
                // Reset the object label on the SpeechToTextRecorder
                SpeechToTextRecorder recorder = recorderToggle.GetComponent<SpeechToTextRecorder>();
                
                // If not found on the GameObject, try to find it in the parent
                if (recorder == null && recorderToggle.transform.parent != null)
                {
                    recorder = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
                }
                
                if (recorder != null)
                {
                    recorder.ResetObjectLabel();
                    Debug.Log("Reset recorder object label");
                }
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
        while (true)
        {
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

    private void UpdatePointingVisualization()
    {
        if (handTracking != null)
        {
            var handSubsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(handSubsystems);
            
            if (handSubsystems.Count > 0)
            {
                var handSubsystem = handSubsystems[0];
                
                GameObject holdingHand = transform.parent == handTracking.m_SpawnedLeftHand.transform ? 
                    handTracking.m_SpawnedLeftHand : 
                    handTracking.m_SpawnedRightHand;
                
                XRHand pointingHand = (holdingHand == handTracking.m_SpawnedLeftHand) ? 
                    handSubsystem.rightHand : 
                    handSubsystem.leftHand;
                
                if (pointingHand.isTracked && pointingHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose fingerTipPose))
                {
                    if (pointingPlane != null)
                    {
                        //// pointingPlane: the root label that is spawned at the finger tip relative to the holding hand, and it moves with the finger tip while looking at the camera ////

                        // Ensure the plane is active
                        pointingPlane.SetActive(true);

                        // Calculate initial relative position if not set
                        if (relativePosition == Vector3.zero)
                        {
                            relativePosition = holdingHand.transform.InverseTransformPoint(fingerTipPose.position);
                        }

                        // If LazyFollow doesn't exist, add it
                        var lazyFollow = pointingPlane.GetComponent<DualTargetLazyFollow>();
                        if (lazyFollow == null)
                        {
                            lazyFollow = pointingPlane.AddComponent<DualTargetLazyFollow>();
                            
                            // Configure following parameters
                            lazyFollow.movementSpeed = 20f;
                            lazyFollow.movementSpeedVariancePercentage = 0.25f;
                            lazyFollow.minDistanceAllowed = 0.02f;
                            lazyFollow.maxDistanceAllowed = 0.05f;
                            lazyFollow.timeUntilThresholdReachesMaxDistance = 0.3f;
                            
                            lazyFollow.minAngleAllowed = 3f;
                            lazyFollow.maxAngleAllowed = 15f;
                            lazyFollow.timeUntilThresholdReachesMaxAngle = 0.3f;
                            
                            lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;
                            lazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAt;
                            
                            lazyFollow.positionTarget = holdingHand.transform; // move with the holding hand
                            lazyFollow.rotationTarget = Camera.main.transform; // look at the camera
                        }

                        // Update the relative position for continuous tracking
                        relativePosition = holdingHand.transform.InverseTransformPoint(fingerTipPose.position);

                        // Add up offset to the relative position
                        Vector3 offsetPosition = relativePosition + (Vector3.up * planeUpOffset);
                        
                        // Update the LazyFollow target offset
                        lazyFollow.targetOffset = offsetPosition;
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
    }

    private void UnsubscribeFromToggleEvents()
    {
        if (spatialUIToggle != null)
        {
            spatialUIToggle.m_ToggleChanged.RemoveListener(OnSphereToggled);
        }
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
                // Disable LazyFollow component if it exists
                LazyFollow lazyFollow = menuCanvas.GetComponent<LazyFollow>();
                if (lazyFollow != null)
                {
                    lazyFollow.enabled = false;
                }

                // Unsubscribe from toggle events instead of disabling the component
                UnsubscribeFromToggleEvents();
                spatialUIToggle.enableInteraction = false;

                // Deactivate first two children
                if (menuCanvas.childCount >= 2)
                {
                    menuCanvas.GetChild(0).gameObject.SetActive(false);
                    menuCanvas.GetChild(1).gameObject.SetActive(false);
                }

                // Set the Menu canvas as a child of our sphere
                menuCanvas.SetParent(transform);
                menuCanvas.localPosition = menuOffset;

                // Calculate rotation adjustments with dampening
                float dampeningFactor = 0.3f;
                float dampeningFactor2 = 0.1f;
                
                // First apply yaw (Y-axis rotation)
                float horizontalAngle = Mathf.Atan2(menuOffset.x, -menuOffset.z) * Mathf.Rad2Deg * dampeningFactor;
                
                // Calculate vertical tilt
                float verticalAngle = -Mathf.Atan2(menuOffset.y, Mathf.Sqrt(menuOffset.x * menuOffset.x + menuOffset.z * menuOffset.z)) * Mathf.Rad2Deg * dampeningFactor;

                // Calculate compensating Z-rotation based on the offset position
                float zCompensation = -Mathf.Atan2(menuOffset.x, menuOffset.y) * Mathf.Rad2Deg * dampeningFactor2;

                // Apply all rotations with the Z-compensation
                menuCanvas.localRotation = Quaternion.Euler(verticalAngle, horizontalAngle, zCompensation);

                // Trigger the toggle ON functionality
                OnSphereToggled(true);

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
                // Reset the Menu canvas parent to its original parent
                menuCanvas.SetParent(null);

                // Re-enable LazyFollow component if it exists
                LazyFollow lazyFollow = menuCanvas.GetComponent<LazyFollow>();
                if (lazyFollow != null)
                {
                    lazyFollow.enabled = true;
                }

                // Resubscribe to toggle events
                SubscribeToToggleEvents();
                spatialUIToggle.enableInteraction = true;

                // Reactivate first two children
                if (menuCanvas.childCount >= 2)
                {
                    menuCanvas.GetChild(0).gameObject.SetActive(true);
                    menuCanvas.GetChild(1).gameObject.SetActive(true);
                }

                // Trigger the toggle OFF functionality
                OnSphereToggled(false);

                // Stop object inspection
                OnObjectInspected(false);
            }
        }
    }

    private void HandlePointingStateChanged(bool isPointing)
    {
        if (!isPointing)
        {
            if (pointingPlane != null)
            {
                // Disable the plane and its LazyFollow component
                var lazyFollow = pointingPlane.GetComponent<DualTargetLazyFollow>();
                if (lazyFollow != null)
                {
                    Destroy(lazyFollow);
                }
                pointingPlane.SetActive(false);
            }
        }
        else
        {
            if (pointingPlane != null)
            {
                pointingPlane.SetActive(true);
                UpdatePointingVisualization();
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
}
