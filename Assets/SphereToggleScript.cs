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
    public Vector3 menuOffset = new Vector3(-6f, 0f, 0.25f); // Default slightly above the anchor

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
    private string currentDescription = "";
    private List<string> descriptionHistory = new List<string>();
    private Coroutine inspectionRoutine;

    // Add at the top of the class with other event declarations
    public delegate void PointingStateChangedHandler(bool isPointing);
    public static event PointingStateChangedHandler OnPointingStateChanged;

    private bool currentlyPointing = false;

    [Header("Hand Tracking")]
    [Tooltip("Reference to the MyHandTracking script")]
    public MyHandTracking handTracking;

    private GameObject pointingSphere;
    private Vector3 relativePosition; // Store relative position to holding hand

    [Header("Pointing Visualization")]
    [Tooltip("Material to apply to the pointing sphere")]
    public Material pointingSphereMaterial;

    private void Start()
    {
        if (geminiClient == null)
        {
            geminiClient = new GeminiAPI(modelName, geminiApiKey);
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

        if (pointingSphere != null)
        {
            Destroy(pointingSphere);
            pointingSphere = null;
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
        }
        else
        {
            // Turn OFF
            InfoPanel.SetActive(false);
            answerPanel.SetActive(false);

            // Clear any existing relationship lines
            if (relationLineManager != null)
            {
                relationLineManager.ClearAllLines();
            }
        }
    }

    private void OnObjectInspected(bool inspected)
    {
        if (inspected)
        {
            descriptionPanel.SetActive(true);
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
            descriptionPanel.SetActive(false);
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
        while (true)  // Will run until inspection is turned off
        {
            // 1) Capture the current frame
            Texture2D frameTex = CaptureFrame(cameraRenderTex);
            string base64Image = ConvertTextureToBase64(frameTex);
            Destroy(frameTex);

            // 2) Build the prompt with history context
            string historyContext = descriptionHistory.Count > 0 
                ? "Previously observed information:\n" + string.Join("\n", descriptionHistory)
                : "No previous observations.";

            // without history, finger pointing at the object
            string prompt = $@"
                You are analyzing a {labelContent} in real-time.
                Scene context: {currentSceneContext}
                Task context: {currentTaskContext}

                Based on the current image and considering the previous observations:
                1. Describe any NEW details or changes you notice about the object
                2. Focus on aspects not mentioned before
                3. Only describe the part where the user is currently pointing at. Don't describe other parts.
                4. Consider the object's current state, position, and interaction with the environment
                5. If you don't see any new information, respond with 'No new observations.'
                6. If the user is not pointing at the object, respond with 'Not being pointed at.'
                7. The user pointing at the object is because they don't fully understand this part. So you should explain it in a way that is easy and straightforward to understand. Try to be helpful.

                Format your response as follows:
                <the part that the user is pointing at>
                <description of the part that you think would be helpful for the user to understand in one sentence>

                Keep the response concise and focused on new information only.
                ";

            // original prompt with history:
            // string prompt = $@"
            //     You are analyzing a {labelContent} in real-time.
            //     Scene context: {currentSceneContext}
            //     Task context: {currentTaskContext}

            //     {historyContext}

            //     Based on the current image and considering the previous observations:
            //     1. Describe any NEW details or changes you notice about the object
            //     2. Focus on aspects not mentioned before
            //     3. Consider the object's current state, position, and interaction with the environment
            //     4. If you don't see any new information, respond with 'No new observations.'

            //     Keep the response concise and focused on new information only.
            //     ";

            // 3) Call Gemini
            var requestTask = geminiClient.GenerateContent(prompt, base64Image);
            
            while (!requestTask.IsCompleted)
                yield return null;

            string rawResponse = requestTask.Result;
            
            // Parse the response using GeminiGeneral's parser
            string newObservation = ParseGeminiRawResponse(rawResponse);

            // 4) Update the description if we have new information
            if (!string.IsNullOrEmpty(newObservation))
            {
                bool isPointingNow = !newObservation.Contains("Not being pointed at");
                
                // Check if pointing state changed
                if (isPointingNow != currentlyPointing)
                {
                    currentlyPointing = isPointingNow;
                    // Invoke the event
                    OnPointingStateChanged?.Invoke(currentlyPointing);
                }

                if (!isPointingNow) newObservation = "";
                
                descriptionHistory.Add(newObservation);
                currentDescription = newObservation;

                // Update the UI
                if (descriptionText != null)
                {
                    descriptionText.text = currentDescription;
                }
            }

            // // 4) Update the description if we have new information, additionally add to history
            // if (!string.IsNullOrEmpty(newObservation) && !newObservation.Contains("No new observations"))
            // {
            //     // Add to history
            //     descriptionHistory.Add(newObservation);

            //     // Update the full description
            //     if (string.IsNullOrEmpty(currentDescription))
            //     {
            //         currentDescription = newObservation;
            //     }
            //     else
            //     {
            //         currentDescription += "\n\n" + newObservation;
            //     }

            //     // Update the UI
            //     if (descriptionText != null)
            //     {
            //         descriptionText.text = currentDescription;
            //     }
            // }

            // Wait for the specified interval before next update
            yield return new WaitForSeconds(inspectionUpdateInterval);
        }
    }

    private string ParseGeminiRawResponse(string response)
    {
        try
        {
            var root = JsonConvert.DeserializeObject<GeminiRoot>(response);

            if (root?.candidates == null || root.candidates.Count == 0 
                || root.candidates[0].content?.parts == null || root.candidates[0].content.parts.Count == 0)
            {
                Debug.LogError("Gemini root structure incomplete, no candidates or content/parts found.");
                return null;
            }
            
            return root.candidates[0].content.parts[0].text?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error parsing Gemini response: {ex}");
            return null;
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

        // original prompt without context:
        // string prompt = $@"
        //     You are provided an image (inline data) plus the label: '{labelContent}'.
        //     Please return a JSON list of possible user questions about this product/item.
        //     Focus on questions that are relevant to the current scene context and tasks.
        //     Return only the most likely questions, up to 5 maximum.
        //     In the format:
        //     json
        //     [
        //     ""Question 1"",
        //     ""Question 2"",
        //     ...
        //     ]
        //     ";

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

        // 3) Call Gemini
        var requestTask = geminiClient.GenerateContent(prompt, base64Image);

        while (!requestTask.IsCompleted)
            yield return null;

        string geminiResponse = requestTask.Result;
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
            float questionHeight = 60f;  // Height of each question block, adjust as needed
            float spacing = 5f;  // Space between questions

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

        // 3) Call Gemini (not using an image here, but you could if relevant)
        var requestTask = geminiClient.GenerateContent(prompt, null);
        
        while (!requestTask.IsCompleted)
            yield return null;

        string rawResponse = requestTask.Result;
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
                menuCanvas.localRotation = Quaternion.identity;

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
        // Clean up existing sphere if it exists
        if (pointingSphere != null)
        {
            Destroy(pointingSphere);
            pointingSphere = null;
        }

        if (isPointing && handTracking != null)
        {
            var handSubsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(handSubsystems);
            
            if (handSubsystems.Count > 0)
            {
                var handSubsystem = handSubsystems[0];
                
                // Get the holding hand
                GameObject holdingHand = transform.parent == handTracking.m_SpawnedLeftHand.transform ? 
                    handTracking.m_SpawnedLeftHand : 
                    handTracking.m_SpawnedRightHand;
                
                // Get the non-holding hand
                XRHand pointingHand = (holdingHand == handTracking.m_SpawnedLeftHand) ? 
                    handSubsystem.rightHand : 
                    handSubsystem.leftHand;
                
                // Try to get the index fingertip position
                if (pointingHand.isTracked && pointingHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose fingerTipPose))
                {
                    Debug.Log($"Pointing fingertip position: {fingerTipPose.position}");
                    
                    // Create sphere at fingertip position
                    pointingSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    pointingSphere.transform.position = fingerTipPose.position;
                    pointingSphere.transform.localScale = Vector3.one * 0.01f; // Made bigger for visibility
                    
                    // Calculate and store the relative position from holding hand
                    relativePosition = holdingHand.transform.InverseTransformPoint(fingerTipPose.position);
                    
                    // Apply the material if assigned
                    if (pointingSphereMaterial != null)
                    {
                        var renderer = pointingSphere.GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            renderer.material = pointingSphereMaterial;
                        }
                    }
                    
                    pointingSphere.layer = 0; // Default layer for now
                    
                    // Remove the collider
                    Destroy(pointingSphere.GetComponent<Collider>());

                    // Make sphere a child of the holding hand
                    pointingSphere.transform.SetParent(holdingHand.transform);
                    pointingSphere.transform.localPosition = relativePosition;
                }
            }
        }
    }
}
