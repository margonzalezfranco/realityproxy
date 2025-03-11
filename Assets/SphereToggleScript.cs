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
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine.Networking;
using System.Threading.Tasks;

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
    [Tooltip("Your API key")]
    public string geminiApiKey = "YOUR_API_KEY";
    
    [Tooltip("Your Google Cloud Vision API key for OCR")]
    public string visionApiKey = "YOUR_VISION_API_KEY";
    
    [Tooltip("A reference to your Gemini API client script. Make sure it's initialized.")]
    public string geminiModelName = "gemini-2.0-flash";
    
    [Tooltip("A RenderTexture from the camera feed (like a VisionPro or other XR camera).")]
    public RenderTexture cameraRenderTex;
    
    [Tooltip("Reference to a GeminiGeneral instance to use for API calls")]
    public GeminiGeneral geminiGeneral;

    [Tooltip("Parent transform/container for the newly created question lines.")]
    [HideInInspector]
    public Transform questionsParent;

    [Tooltip("Prefab that displays each question. Should have a TextMeshPro or Text inside.")]
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
    [Tooltip("TextMeshPro component that displays the object description")]
    public TextMeshPro descriptionText;
    [Tooltip("How often to update the description (in seconds)")]
    public float inspectionUpdateInterval = 1f;
    private Coroutine inspectionRoutine;

    // Add OCR-related fields
    [Header("OCR Integration")]
    [Tooltip("Reference to the CloudVisionOCRUnified component")]
    public CloudVisionOCRUnified ocrComponent;
    [Tooltip("How often to update the OCR data (in seconds)")]
    public float ocrUpdateInterval = 3f;
    private Coroutine ocrRoutine;
    private List<string> objectOCRContext = new List<string>();
    private List<string> knownObjectLabels = new List<string>();
    
    // Add fields for optimal OCR context management
    private string optimalOCRContext = "";
    private bool hasOptimalContext = false;
    
    // Add dictionary to store bounding boxes for each label
    private Dictionary<string, Rect> labelBoundingBoxes = new Dictionary<string, Rect>();

    // Add at the top of the class with other event declarations
    public delegate void PointingStateChangedHandler(bool isPointing);
    public static event PointingStateChangedHandler OnPointingStateChanged;

    private bool currentlyPointing = false;

    [Header("Hand Tracking")]
    [Tooltip("Reference to the MyHandTracking script")]
    public MyHandTracking handTracking;

    [Header("Pointing Visualization")]
    [Tooltip("Plane to show which part is being pointed at")]
    public Transform pointingPlane;

    [Tooltip("TextMeshPro component on the pointing plane")]
    public TextMeshPro tmpPointingText;

    [Tooltip("Offset distance above the finger point")]
    public float pointingPlaneOffset = 0.2f;
    
    [Tooltip("Prefab for OCR line visualization")]
    public GameObject ocrLinePrefab;
    
    [Tooltip("Parent transform for OCR line visualizations")]
    public Canvas ocrLineCanvas;
    
    [Tooltip("Material for highlighted OCR line")]
    public Material highlightedLineMaterial;
    
    [Tooltip("Material for normal OCR line")]
    public Material normalLineMaterial;
    
    [Tooltip("Override scale for the OCR canvas (set to 0 for automatic scaling)")]
    public float ocrCanvasScaleOverride = 0f;

    private Vector3 relativePosition; // Store relative position to holding hand
    private List<GameObject> ocrLineVisualizers = new List<GameObject>();
    private string currentPointedArea = "";

    // List to store captured debug logs
    private List<string> capturedLogs = new List<string>();

    // Add a field to store the estimated product dimensions
    private Vector2 estimatedProductSize = new Vector2(0.15f, 0.20f); // Default fallback size (15cm × 20cm)
    private bool hasDimensionEstimate = false;

    private void Start()
    {
        // Find a GeminiGeneral instance if one isn't assigned
        if (geminiGeneral == null)
        {
            geminiGeneral = FindAnyObjectByType<GeminiGeneral>();
            if (geminiGeneral == null)
            {
                Debug.LogError("No GeminiGeneral instance found in the scene. API calls will fail.");
            }
        }
        
        // Initialize the Gemini client (we don't need this anymore since we're using GeminiGeneral)
        // geminiClient = new GeminiAPI(geminiModelName, geminiApiKey);
        
        // Subscribe to toggle events
        SubscribeToToggleEvents();
        
        // Initialize OCR context
        if (sceneContextManager != null)
        {
            UpdateKnownObjectLabels();
        }
        
        // Start OCR routine if enabled
        if (ocrUpdateInterval > 0)
        {
            ocrRoutine = StartCoroutine(UpdateObjectOCRRoutine());
        }
        
        // Subscribe to scene context events
        if (sceneContextManager != null)
        {
            sceneContextManager.OnSceneContextComplete += HandleSceneAnalysis;
            
            // Get current analysis if available
            var currentAnalysis = sceneContextManager.GetCurrentAnalysis();
            if (currentAnalysis != null)
            {
                HandleSceneAnalysis(currentAnalysis);
            }
        }
        
        // Subscribe to log messages for OCR parsing
        Application.logMessageReceived += CaptureOCRLogMessages;
        
        // Start product dimension estimation routine
        StartCoroutine(EstimateProductDimensionsRoutine());
        
        // Create OCR line prefab if needed
        if (ocrLinePrefab == null && ocrLineCanvas != null)
        // Initialize the OCR component
        if (ocrComponent == null)
        {
            // First try to find the component in the scene
            ocrComponent = FindAnyObjectByType<CloudVisionOCRUnified>();
            
            if (ocrComponent == null)
            {
                Debug.LogWarning("No CloudVisionOCRUnified component found in the scene. OCR functionality will be limited.");
            }
            else
            {
                Debug.Log("Found existing CloudVisionOCRUnified component in the scene.");
            }
        }
        
        // Set the Vision API key if provided
        if (!string.IsNullOrEmpty(visionApiKey))
        {
            ocrComponent.SetApiKey(visionApiKey);
        }
        
        // No need to subscribe to log messages anymore since we're getting bounding boxes directly
        // Application.logMessageReceived += CaptureOCRLogMessages;

        // Subscribe to anchor grab/release events
        HandGrabTrigger.OnAnchorGrabbed += HandleAnchorGrabbed;
        HandGrabTrigger.OnAnchorReleased += HandleAnchorReleased;

        // Subscribe to our own pointing state event
        OnPointingStateChanged += HandlePointingStateChanged;
        
        // Initialize known object labels list
        UpdateKnownObjectLabels();

        // Create OCR line container early
        if (ocrLineCanvas == null)
        {
            GameObject containerObj = new GameObject("OCRLineContainer");
            ocrLineCanvas = containerObj.AddComponent<Canvas>();
            Debug.Log("Created OCR line container during Start()");
            
            // If pointing plane exists, parent it immediately
            if (pointingPlane != null)
            {
                ocrLineCanvas.transform.SetParent(pointingPlane, false);
                ocrLineCanvas.transform.localPosition = Vector3.zero;
                ocrLineCanvas.transform.localRotation = Quaternion.identity;
                Debug.Log("Parented OCR line container to pointing plane");
            }
            else
            {
                Debug.LogWarning("Pointing plane is null during Start(), can't parent OCR line container");
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
        OnPointingStateChanged -= HandlePointingStateChanged;
        
        // No need to unsubscribe from log messages anymore
        // Application.logMessageReceived -= CaptureOCRLogMessages;

        // Make sure to set pointing state to false when destroyed
        if (currentlyPointing)
        {
            currentlyPointing = false;
            OnPointingStateChanged?.Invoke(false);
        }

        if (pointingPlane != null)
        {
            pointingPlane.gameObject.SetActive(false);
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
            // descriptionPanel.SetActive(true);
            string labelContent = labelUnderSphere ? labelUnderSphere.text : "unknown object";
            
            // Start continuous inspection updates
            if (inspectionRoutine != null)
            {
                StopCoroutine(inspectionRoutine);
            }
            inspectionRoutine = StartCoroutine(UpdateObjectDescriptionRoutine(labelContent));
            
            // Start OCR updates
            if (ocrRoutine != null)
            {
                StopCoroutine(ocrRoutine);
            }
            ocrRoutine = StartCoroutine(UpdateObjectOCRRoutine());
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
            
            // Stop OCR updates
            if (ocrRoutine != null)
            {
                StopCoroutine(ocrRoutine);
                ocrRoutine = null;
            }
            
            // Make sure to set pointing state to false when inspection stops
            if (currentlyPointing)
            {
                currentlyPointing = false;
                OnPointingStateChanged?.Invoke(false);
            }
        }
    }

    /// <summary>
    /// Updates the list of known object labels from the scene
    /// </summary>
    private void UpdateKnownObjectLabels()
    {
        knownObjectLabels.Clear();
        
        // Add the current object's label
        if (labelUnderSphere != null && !string.IsNullOrEmpty(labelUnderSphere.text))
        {
            knownObjectLabels.Add(labelUnderSphere.text);
        }
        
        // Add labels from scene object manager if available
        if (sceneObjManager != null)
        {
            var anchors = sceneObjManager.GetAllAnchors();
            foreach (var anchor in anchors)
            {
                if (!string.IsNullOrEmpty(anchor.label) && !knownObjectLabels.Contains(anchor.label))
                {
                    knownObjectLabels.Add(anchor.label);
                }
            }
        }
        
        Debug.Log($"Updated known object labels: {string.Join(", ", knownObjectLabels)}");
    }

    /// <summary>
    /// Handles OCR results and updates the objectOCRContext
    /// </summary>
    private void HandleOCRResult(string ocrText, List<string> wordList, Dictionary<string, BoundingBox> wordBoundingBoxes = null)
    {
        if (!string.IsNullOrEmpty(ocrText))
        {
            // Store the latest OCR result
            string latestOCRText = ocrText;
            
            // If we already have an optimal context, compare and decide whether to update
            if (hasOptimalContext)
            {
                StartCoroutine(UpdateOptimalOCRRoutine(latestOCRText, wordBoundingBoxes));
            }
            else
            {
                // First OCR result becomes the optimal context
                optimalOCRContext = latestOCRText;
                hasOptimalContext = true;
                
                // Update the context and labels
                UpdateOCRContextAndLabels(optimalOCRContext, wordList, wordBoundingBoxes);
                
                // Estimate product dimensions
                StartCoroutine(EstimateProductDimensionsRoutine());
            }
        }
    }
    
    /// <summary>
    /// Uses Gemini to compare the latest OCR result with the current optimal context
    /// and decides whether to update the optimal context
    /// </summary>
    private IEnumerator UpdateOptimalOCRRoutine(string latestOCRText, Dictionary<string, BoundingBox> wordBoundingBoxes = null)
    {
        // Skip if the latest text is empty or identical to optimal
        if (string.IsNullOrEmpty(latestOCRText) || latestOCRText == optimalOCRContext)
        {
            yield break;
        }
        
        string prompt = $@"
            Compare these two OCR text results from the same object and determine if the new result should replace the current optimal result.

            CURRENT OPTIMAL OCR RESULT:
            {optimalOCRContext}

            NEW OCR RESULT:
            {latestOCRText}

            Rules for comparison:
            1. If the new result only captures a subset of the optimal result (fewer lines or words), keep the optimal result.
            2. If the new result shows a different aspect of the object (e.g., back side, different panel), replace the optimal result.
            3. If the new result is more complete (more lines or clearer text), replace the optimal result.
            4. If the new result contains unique information not in the optimal result, consider merging them.

            Respond with a JSON object:
            {{
              ""action"": ""keep"" or ""replace"" or ""merge"",
              ""reason"": ""brief explanation of decision"",
              ""mergedText"": ""combined text if action is merge, otherwise empty string""
            }}
        ";
        
        // Call Gemini without an image
        var request = geminiGeneral.MakeGeminiRequest(prompt, null);
        while (!request.IsCompleted)
            yield return null;
            
        string rawResponse = request.Result;
        string jsonStr = TryExtractJson(rawResponse);
        
        if (!string.IsNullOrEmpty(jsonStr))
        {
            try
            {
                var decision = JsonConvert.DeserializeObject<OCRComparisonDecision>(jsonStr);
                
                if (decision != null)
                {
                    Debug.Log($"OCR Comparison Decision: {decision.action} - {decision.reason}");
                    
                    switch (decision.action.ToLower())
                    {
                        case "replace":
                            // Update optimal context with the new result
                            optimalOCRContext = latestOCRText;
                            UpdateOCRContextAndLabels(optimalOCRContext, null, wordBoundingBoxes);
                            break;
                            
                        case "merge":
                            // Use the merged text if provided
                            if (!string.IsNullOrEmpty(decision.mergedText))
                            {
                                optimalOCRContext = decision.mergedText;
                                // Note: When merging, we might lose bounding box information for new text
                                // We'll keep existing bounding boxes and add new ones where possible
                                UpdateOCRContextAndLabels(optimalOCRContext, null, wordBoundingBoxes, true);
                            }
                            break;
                            
                        case "keep":
                        default:
                            // Keep the current optimal context
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to parse OCR comparison decision: {ex}\nJSON string: {jsonStr}");
            }
        }
    }
    
    /// <summary>
    /// Updates the OCR context and known labels based on the optimal OCR text
    /// </summary>
    private void UpdateOCRContextAndLabels(string ocrText, List<string> wordList, Dictionary<string, BoundingBox> wordBoundingBoxes = null, bool isMerging = false)
    {
        // Update the OCR context
        objectOCRContext.Clear();
        objectOCRContext.Add(ocrText);
        
        // Extract lines from OCR text
        List<string> textLines = new List<string>(ocrText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
        
        // Clear previous labels unless we're merging
        if (!isMerging)
        {
            knownObjectLabels.Clear();
            labelBoundingBoxes.Clear();
        }
        
        // Process each line and combine bounding boxes for words in the same line
        foreach (string line in textLines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                string trimmedLine = line.Trim();
                knownObjectLabels.Add(trimmedLine);
                
                // If we have bounding box information, try to combine for this line
                if (wordBoundingBoxes != null && wordBoundingBoxes.Count > 0)
                {
                    // Split the line into words to match with bounding boxes
                    string[] lineWords = trimmedLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    // Find all words in this line that have bounding boxes
                    List<BoundingBox> boxesInLine = new List<BoundingBox>();
                    foreach (string word in lineWords)
                    {
                        if (wordBoundingBoxes.TryGetValue(word, out BoundingBox box))
                        {
                            boxesInLine.Add(box);
                        }
                    }
                    
                    // If we found bounding boxes for words in this line, combine them
                    if (boxesInLine.Count > 0)
                    {
                        Rect combinedBox = CombineBoundingBoxes(boxesInLine);
                        
                        // Store the combined bounding box for this line
                        if (!labelBoundingBoxes.ContainsKey(trimmedLine) || !isMerging)
                        {
                            labelBoundingBoxes[trimmedLine] = combinedBox;
                            Debug.Log($"Combined bounding box for '{trimmedLine}': {combinedBox}");
                        }
                    }
                }
            }
        }
        
        // Also add individual words if they're not already part of a line
        if (wordList != null && wordList.Count > 0)
        {
            foreach (string word in wordList)
            {
                if (!string.IsNullOrWhiteSpace(word) && !knownObjectLabels.Contains(word) && 
                    !textLines.Any(line => line.Contains(word)))
                {
                    knownObjectLabels.Add(word);
                    
                    // Add bounding box for individual word if available
                    if (wordBoundingBoxes != null && wordBoundingBoxes.TryGetValue(word, out BoundingBox box))
                    {
                        labelBoundingBoxes[word] = new Rect(box.MinX, box.MinY, box.Width, box.Height);
                    }
                }
            }
        }
        
        // Debug.Log($"Updated OCR context: {ocrText}");
        // Debug.Log($"Updated known labels (lines and words): {string.Join(", ", knownObjectLabels)}");
        Debug.Log($"Stored bounding boxes for {labelBoundingBoxes.Count} labels");
        
        // Update the visualization if we have bounding boxes
        if (labelBoundingBoxes.Count > 0)
        {
            // If we're already showing the visualization, update it
            if (ocrLineCanvas != null && ocrLineCanvas.gameObject.activeInHierarchy)
            {
                Debug.Log("Updating pointing visualization after OCR context change");
                UpdatePointingVisualization();
            }
        }
    }
    
    /// <summary>
    /// Combines multiple bounding boxes into a single encompassing rectangle
    /// </summary>
    private Rect CombineBoundingBoxes(List<BoundingBox> boxes)
    {
        if (boxes == null || boxes.Count == 0)
            return new Rect(0, 0, 0, 0);
            
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        
        foreach (var box in boxes)
        {
            minX = Mathf.Min(minX, box.MinX);
            minY = Mathf.Min(minY, box.MinY);
            maxX = Mathf.Max(maxX, box.MaxX);
            maxY = Mathf.Max(maxY, box.MaxY);
        }
        
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Coroutine that periodically updates the OCR context for the object being inspected
    /// </summary>
    private IEnumerator UpdateObjectOCRRoutine()
    {
        if (ocrComponent == null)
        {
            // Try to find an existing OCR component in the scene
            ocrComponent = FindAnyObjectByType<CloudVisionOCRUnified>();
            
            // If still not found, create a new one
            if (ocrComponent == null)
            {
                Debug.LogWarning("No OCR component assigned and couldn't find one in the scene. OCR context will not be updated.");
                yield break;
            }
        }
        
        // Ensure the OCR component has a render texture
        if (ocrComponent.sourceRenderTexture == null)
        {
            ocrComponent.sourceRenderTexture = cameraRenderTex;
        }
        
        // Make sure the API key is set
        if (!string.IsNullOrEmpty(visionApiKey))
        {
            ocrComponent.SetApiKey(visionApiKey);
        }
        
        Debug.Log("Starting OCR update routine");
        
        // Create a log interceptor to capture debug logs
        Application.logMessageReceived += CaptureOCRLogMessages;
        capturedLogs.Clear();
        
        while (true)
        {
            // Capture the current frame
            Texture2D frameTex = CaptureFrame(cameraRenderTex);
            if (frameTex != null)
            {
                // Set the render texture on the OCR component
                ocrComponent.sourceRenderTexture = cameraRenderTex;
                
                // Clear previous logs before starting new OCR
                capturedLogs.Clear();
                
                // Create a custom OCR result handler
                OCRResultHandler resultHandler = new OCRResultHandler();
                
                // Subscribe to the standard OCR complete event
                resultHandler.OnOCRComplete += (fullText, wordList, wordBoxes) => {
                    // Use the directly passed bounding boxes instead of extracting from logs
                    HandleOCRResult(fullText, wordList, wordBoxes);
                };
                
                // Start OCR process with bounding box mode
                ocrComponent.ocrMode = CloudVisionOCRUnified.OCRMode.BoundingBox;
                ocrComponent.StartOCR(resultHandler);
                
                // Wait for OCR to complete (with timeout)
                float timeoutCounter = 0;
                float maxTimeout = 10f; // 10 seconds timeout
                while (!resultHandler.IsComplete && timeoutCounter < maxTimeout)
                {
                    timeoutCounter += Time.deltaTime;
                    yield return null;
                }
                
                // Clean up
                resultHandler.OnOCRComplete -= (fullText, wordList, wordBoxes) => HandleOCRResult(fullText, wordList, wordBoxes);
                Destroy(frameTex);
            }
            
            // Wait for the specified interval before next update
            yield return new WaitForSeconds(ocrUpdateInterval);
        }
    }

    /// <summary>
    /// Captures debug log messages to extract bounding box information
    /// </summary>
    private void CaptureOCRLogMessages(string logString, string stackTrace, LogType type)
    {
        // Only capture logs that contain bounding box information
        if (logString.Contains("Detected word:") && logString.Contains("with bounding box:"))
        {
            capturedLogs.Add(logString);
        }
    }
    
    /// <summary>
    /// Extracts bounding box information from captured debug logs
    /// </summary>
    private Dictionary<string, BoundingBox> ExtractBoundingBoxesFromOCR()
    {
        Dictionary<string, BoundingBox> wordBoxes = new Dictionary<string, BoundingBox>();
        
        try
        {
            foreach (string log in capturedLogs)
            {
                // Parse logs in the format: "Detected word: WORD with bounding box: (x1, y1) (x2, y2) (x3, y3) (x4, y4)"
                if (log.Contains("Detected word:") && log.Contains("with bounding box:"))
                {
                    // Extract the word
                    int wordStart = log.IndexOf("Detected word:") + "Detected word:".Length;
                    int wordEnd = log.IndexOf("with bounding box:");
                    if (wordStart >= 0 && wordEnd > wordStart)
                    {
                        string word = log.Substring(wordStart, wordEnd - wordStart).Trim();
                        
                        // Extract the bounding box coordinates
                        int boxStart = log.IndexOf("with bounding box:") + "with bounding box:".Length;
                        string boxString = log.Substring(boxStart).Trim();
                        
                        // Parse the coordinates
                        List<Vector2> vertices = ParseBoundingBoxVertices(boxString);
                        
                        if (vertices.Count == 4)
                        {
                            // Calculate min/max coordinates to create a bounding box
                            float minX = vertices.Min(v => v.x);
                            float minY = vertices.Min(v => v.y);
                            float maxX = vertices.Max(v => v.x);
                            float maxY = vertices.Max(v => v.y);
                            
                            // Create and store the bounding box
                            BoundingBox box = new BoundingBox(minX, minY, maxX, maxY);
                            wordBoxes[word] = box;
                            
                            Debug.Log($"Extracted bounding box for '{word}': {box}");
                        }
                    }
                }
            }
            
            Debug.Log($"Extracted {wordBoxes.Count} word bounding boxes from OCR logs");
            return wordBoxes;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error extracting bounding boxes: {ex.Message}");
            return new Dictionary<string, BoundingBox>();
        }
    }
    
    /// <summary>
    /// Parses a string containing bounding box vertices into a list of Vector2 points
    /// </summary>
    private List<Vector2> ParseBoundingBoxVertices(string boxString)
    {
        List<Vector2> vertices = new List<Vector2>();
        
        try
        {
            // Format is typically: "(x1, y1) (x2, y2) (x3, y3) (x4, y4)"
            // Split by parentheses and process each coordinate pair
            string[] parts = boxString.Split(new[] { '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed) && trimmed.Contains(","))
                {
                    string[] coords = trimmed.Split(',');
                    if (coords.Length == 2)
                    {
                        if (float.TryParse(coords[0].Trim(), out float x) && 
                            float.TryParse(coords[1].Trim(), out float y))
                        {
                            vertices.Add(new Vector2(x, y));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error parsing bounding box vertices: {ex.Message}");
        }
        
        return vertices;
    }

    private IEnumerator UpdateObjectDescriptionRoutine(string labelContent)
    {
        if (inspectionRoutine != null)
        {
            StopCoroutine(inspectionRoutine);
        }
        
        // If we have OCR data, include it in the context
        string ocrContext = "";
        if (objectOCRContext.Count > 0)
        {
            ocrContext = "OCR Text detected on object: " + string.Join(" ", objectOCRContext);
        }
        else if (hasOptimalContext && !string.IsNullOrEmpty(optimalOCRContext))
        {
            ocrContext = "OCR Text detected on object: " + optimalOCRContext;
        }
        
        // Get scene context if available
        string sceneTypeContext = "";
        string taskContext = "";
        
        if (currentSceneAnalysis != null)
        {
            sceneTypeContext = $"Current environment: {currentSceneAnalysis.sceneType}";
            
            if (currentSceneAnalysis.possibleTasks != null && currentSceneAnalysis.possibleTasks.Count > 0)
            {
                taskContext = $"Possible tasks in this environment: {string.Join(", ", currentSceneAnalysis.possibleTasks)}";
            }
        }
        
        // Capture the current frame
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        
        // Convert to base64
        string base64Image = ConvertTextureToBase64(frameTex);
        
        // Build the prompt
        string prompt = $@"
            Analyze this image showing a {labelContent}.
            {ocrContext}
            {sceneTypeContext}
            {taskContext}
            
            Provide a detailed description of the object, including:
            1. What it is and what it's used for
            2. Key features visible in the image
            3. Any text or labels visible on it
            4. How it might be relevant to the current environment
            
            Keep the description concise (max 3-4 sentences) but informative.
        ";
        
        // Call Gemini with the image
        var request = geminiGeneral.MakeGeminiRequest(prompt, base64Image);
        while (!request.IsCompleted)
            yield return null;
            
        string response = request.Result;
        
        // Update the description text
        if (descriptionText != null)
        {
            descriptionText.text = response;
        }
        
        // Wait for the specified interval before next update
        yield return new WaitForSeconds(inspectionUpdateInterval);
    }

    private void UpdatePointingVisualization()
    {
        Debug.Log("ENTER UpdatePointingVisualization");
        
        // Check if we need to create a prefab for OCR lines
        if (ocrLinePrefab == null)
        {
            ocrLinePrefab = CreateOCRLinePrefab();
        }
        
        // Debug information about state
        Debug.Log($"UpdatePointingVisualization - labelBoundingBoxes count: {labelBoundingBoxes.Count}, container: {(ocrLineCanvas != null ? "exists" : "null")}, pointing plane: {(pointingPlane != null ? "exists" : "null")}, currentPointedArea: '{currentPointedArea}'");
        
        bool useHandTracking = false;
        Vector3 pointingDirection = Vector3.forward;
        
        // Try to use hand tracking if available
        if (handTracking != null)
        {
            Debug.Log("HandTracking is not null");
            
            var handSubsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(handSubsystems);
            
            Debug.Log($"Found {handSubsystems.Count} hand subsystems");
            
            if (handSubsystems.Count > 0)
            {
                var handSubsystem = handSubsystems[0];
                
                GameObject holdingHand = transform.parent == handTracking.m_SpawnedLeftHand.transform ? 
                    handTracking.m_SpawnedLeftHand : 
                    handTracking.m_SpawnedRightHand;
                
                Debug.Log($"Holding hand: {(holdingHand == handTracking.m_SpawnedLeftHand ? "Left" : "Right")}");
                
                XRHand pointingHand = (holdingHand == handTracking.m_SpawnedLeftHand) ? 
                    handSubsystem.rightHand : 
                    handSubsystem.leftHand;
                
                Debug.Log($"Pointing hand isTracked: {pointingHand.isTracked}");
                
                bool hasFingerTipPose = pointingHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose fingerTipPose);
                Debug.Log($"Has finger tip pose: {hasFingerTipPose}");
                
                if (pointingHand.isTracked && hasFingerTipPose)
                {
                    Debug.Log("Hand is tracked and has finger tip pose");
                    useHandTracking = true;
                    
                    // Get middle finger joint for more stable pointing direction
                    bool hasMiddleJoint = pointingHand.GetJoint(XRHandJointID.MiddleProximal).TryGetPose(out Pose middleJointPose);
                    
                    // Calculate pointing direction - more stable by using multiple joints if available
                    if (hasMiddleJoint)
                    {
                        // Use direction from middle finger base to index tip for more stability
                        pointingDirection = (fingerTipPose.position - middleJointPose.position).normalized;
                    }
                    else
                    {
                        // Fallback to just using finger forward direction
                        pointingDirection = fingerTipPose.forward;
                    }
                    
                    Debug.Log($"Calculated pointing direction: {pointingDirection}");
                    
                    // Update the relative position for continuous tracking
                    relativePosition = holdingHand.transform.InverseTransformPoint(fingerTipPose.position);
                }
            }
            else
            {
                Debug.LogWarning("No hand subsystems found, using fallback visualization");
            }
        }
        else
        {
            Debug.LogWarning("HandTracking is null, using fallback visualization");
        }
        
        // Setup pointing plane (with or without hand tracking)
        if (pointingPlane != null)
        {
            Debug.Log("Setting up pointing plane");
            
            // Ensure the plane is active
            pointingPlane.gameObject.SetActive(true);

            if (useHandTracking)
            {
                // If LazyFollow doesn't exist, add it
                var lazyFollow = pointingPlane.GetComponent<DualTargetLazyFollow>();
                if (lazyFollow == null)
                {
                    lazyFollow = pointingPlane.gameObject.AddComponent<DualTargetLazyFollow>();
                    
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
                    
                    lazyFollow.positionTarget = transform.parent;
                    lazyFollow.rotationTarget = Camera.main.transform;
                }

                // Add up offset to the relative position
                Vector3 offsetPosition = relativePosition + (Vector3.up * pointingPlaneOffset);
                
                // Update the LazyFollow target offset
                lazyFollow.targetOffset = offsetPosition;
            }
            else
            {
                // For editor testing, position the pointing plane in front of the camera if not already positioned
                if (pointingPlane.transform.position == Vector3.zero)
                {
                    Camera mainCamera = Camera.main;
                    if (mainCamera != null)
                    {
                        // Position the plane in front of the camera
                        pointingPlane.transform.position = mainCamera.transform.position + mainCamera.transform.forward * 0.5f;
                        pointingPlane.transform.rotation = Quaternion.LookRotation(-mainCamera.transform.forward, Vector3.up);
                        Debug.Log("Positioned pointing plane in front of camera for editor testing");
                    }
                }
            }
            
            Debug.Log("Pointing plane setup complete");
        }
        else
        {
            Debug.LogError("Pointing plane is null!");
            return; // Can't proceed without a pointing plane
        }
        
        // Now handle OCR visualization as a child of the pointing plane
        Debug.Log($"About to process labelBoundingBoxes (count: {labelBoundingBoxes.Count})");
        
        if (labelBoundingBoxes.Count > 0)
        {
            Debug.Log($"Processing {labelBoundingBoxes.Count} bounding boxes for visualization");
            
            // Calculate the overall bounding box that encompasses all text
            Rect overallBounds = CalculateOverallBoundingBox();
            Debug.Log($"Overall bounds: {overallBounds}");
            
            // Create or update the container for OCR visualization
            if (ocrLineCanvas == null)
            {
                GameObject containerObj = new GameObject("OCRLineContainer");
                ocrLineCanvas = containerObj.AddComponent<Canvas>();
                Debug.Log("Created OCR line container during visualization update");
            }
            
            // Make the OCR container a child of the pointing plane
            if (ocrLineCanvas.transform.parent != pointingPlane)
            {
                ocrLineCanvas.transform.SetParent(pointingPlane, false);
                ocrLineCanvas.transform.localPosition = Vector3.zero;
                ocrLineCanvas.transform.localRotation = Quaternion.identity;
                Debug.Log("Parented OCR line container to pointing plane");
            }
            
            // Force the container to be visible in hierarchy for debugging
            ocrLineCanvas.gameObject.hideFlags = HideFlags.None;
            
            // Configure the canvas for world space
            if (ocrLineCanvas != null && !ocrLineCanvas.GetComponent<CanvasScaler>())
            {
                // Get the scale of the parent (Pointing object)
                Vector3 parentScale = pointingPlane.lossyScale;
                Debug.Log($"Parent (Pointing) scale is: {parentScale}");
                
                // Calculate the exact inverse scale to neutralize the parent's scale
                // This ensures that 1 unit in the canvas = 1 unit in world space regardless of parent scale
                Vector3 inverseScale = new Vector3(
                    1.0f / parentScale.x,
                    1.0f / parentScale.y,
                    1.0f / parentScale.z
                );
                
                // Apply a small base scale factor to make the canvas a reasonable size
                float baseScaleFactor = 1.0f; // Set to 1.0 to use pure inverse scale
                
                // Use the override scale if it's set
                if (ocrCanvasScaleOverride > 0)
                {
                    baseScaleFactor = ocrCanvasScaleOverride;
                    Debug.Log($"Using manual scale override: {baseScaleFactor}");
                }
                
                Debug.Log($"Calculated inverse scale: {inverseScale}");
                
                // Set up the canvas for world space
                ocrLineCanvas.renderMode = RenderMode.WorldSpace;
                
                // Add a canvas scaler for consistent sizing
                CanvasScaler scaler = ocrLineCanvas.gameObject.AddComponent<CanvasScaler>();
                scaler.dynamicPixelsPerUnit = 10f;
                scaler.referencePixelsPerUnit = 100f;
                
                // FIXED: Explicitly set scale to 0.02 (1/50) in each dimension
                // This bypasses any calculation or override issues
                ocrLineCanvas.transform.localScale = new Vector3(0.02f, 0.02f, 0.02f);
                
                Debug.Log($"Applied fixed scale to canvas: {ocrLineCanvas.transform.localScale}, parent scale: {parentScale}");
                
                // Add a background panel to the canvas that will contain the OCR lines
                if (!ocrLineCanvas.transform.Find("OCRPanel"))
                {
                    GameObject panelObj = new GameObject("OCRPanel");
                    panelObj.transform.SetParent(ocrLineCanvas.transform, false);
                    
                    // Add RectTransform and set it to fill the canvas
                    RectTransform panelRect = panelObj.AddComponent<RectTransform>();
                    panelRect.anchorMin = Vector2.zero;
                    panelRect.anchorMax = Vector2.one;
                    panelRect.offsetMin = Vector2.zero;
                    panelRect.offsetMax = Vector2.zero;
                    
                    // Add Image component for background (optional)
                    Image panelImage = panelObj.AddComponent<Image>();
                    panelImage.color = new Color(0, 0, 0, 0.3f); // Semi-transparent black
                }
            }

            // Set the canvas size based on real-world dimensions, adjusted for parent scale
            if (ocrLineCanvas != null)
            {
                RectTransform canvasRect = ocrLineCanvas.GetComponent<RectTransform>();
                if (canvasRect != null)
                {
                    float canvasWidth, canvasHeight;
                    
                    if (hasDimensionEstimate)
                    {
                        // Use the estimated real-world size in meters, converted to canvas units
                        // For a world space canvas, we typically want 1 unit = 1 meter
                        canvasWidth = estimatedProductSize.x * 100f; // 100 canvas units per meter
                        canvasHeight = estimatedProductSize.y * 100f;
                        Debug.Log($"Using estimated dimensions for canvas: {canvasWidth}x{canvasHeight} canvas units (from {estimatedProductSize.x}x{estimatedProductSize.y} meters)");
                    }
                    else
                    {
                        // Use default dimensions - a reasonable size for UI elements in world space
                        canvasWidth = 15f;
                        canvasHeight = 15f * (overallBounds.height / overallBounds.width);
                        Debug.Log($"Using default dimensions for canvas: {canvasWidth}x{canvasHeight} canvas units");
                    }
                    
                    // Set the canvas rect size
                    canvasRect.sizeDelta = new Vector2(canvasWidth, canvasHeight);
                }
            }

            // Find the panel that will contain the OCR lines
            Transform ocrPanel = ocrLineCanvas.transform.Find("OCRPanel");
            if (ocrPanel == null)
            {
                Debug.LogError("OCRPanel not found in canvas");
                return;
            }
            
            // Find the currently pointed line to calculate positioning offset
            Vector3 containerOffset = Vector3.zero;
            bool hasPointedArea = !string.IsNullOrEmpty(currentPointedArea);
            
            if (hasPointedArea && labelBoundingBoxes.TryGetValue(currentPointedArea, out Rect pointedLineBounds))
            {
                // Calculate the center of the pointed line relative to the overall bounds
                float pointedCenterX = pointedLineBounds.x + pointedLineBounds.width / 2;
                float pointedCenterY = pointedLineBounds.y + pointedLineBounds.height / 2;
                
                // Calculate overall center
                float overallCenterX = overallBounds.x + overallBounds.width / 2;
                float overallCenterY = overallBounds.y + overallBounds.height / 2;
                
                // Calculate normalized offset from center (-1 to 1 range)
                float normalizedOffsetX = (pointedCenterX - overallCenterX) / overallBounds.width * 2;
                float normalizedOffsetY = (pointedCenterY - overallCenterY) / overallBounds.height * 2;
                
                // Invert Y offset for Unity's coordinate system
                normalizedOffsetY = -normalizedOffsetY;
                
                // Apply offset to position the container so the pointed line is centered on the pointing plane
                containerOffset = new Vector3(normalizedOffsetX * 0.05f, normalizedOffsetY * 0.05f, 0.001f);
                
                Debug.Log($"Positioning container for pointed area: '{currentPointedArea}', offset: {containerOffset}");
            }
            else
            {
                Debug.LogWarning($"Current pointed area not found in bounding boxes or is empty. Current area: '{currentPointedArea}', Available boxes: {string.Join(", ", labelBoundingBoxes.Keys)}");
                // Just use default values if no pointed area is found
                containerOffset = Vector3.zero;
            }
            
            // Position the container relative to the pointing plane with calculated offset
            ocrLineCanvas.transform.localPosition = containerOffset;
            
            Debug.Log($"OCR container positioned and ready for lines");
            
            Debug.Log("Container positioned and scaled. About to clear previous visualizers.");
            
            // Clear previous line visualizers
            if (ocrLineVisualizers.Count > 0)
            {
                Debug.Log($"Clearing {ocrLineVisualizers.Count} previous line visualizers");
                foreach (var visualizer in ocrLineVisualizers)
                {
                    if (visualizer != null)
                    {
                        Destroy(visualizer);
                    }
                }
                ocrLineVisualizers.Clear();
            }
            
            // Create visualizations for each text line
            Debug.Log($"Starting to create visualizers for {labelBoundingBoxes.Count} lines");
            int createdCount = 0;
            
            // Check OCR line prefab before starting
            if (ocrLinePrefab == null)
            {
                Debug.LogWarning("OCR line prefab is null! Will use fallback quads.");
            }
            else
            {
                Debug.Log($"OCR line prefab exists: {ocrLinePrefab.name}");
            }
            
            foreach (var kvp in labelBoundingBoxes)
            {
                string lineText = kvp.Key;
                Rect lineBounds = kvp.Value;
                
                Debug.Log($"Processing line: '{lineText}'");
                
                // Calculate normalized position within overall bounds (0-1 range)
                float normalizedX = (lineBounds.x - overallBounds.x) / overallBounds.width;
                float normalizedY = (lineBounds.y - overallBounds.y) / overallBounds.height;
                float normalizedWidth = lineBounds.width / overallBounds.width;
                float normalizedHeight = lineBounds.height / overallBounds.height;
                
                Debug.Log($"Normalized position for '{lineText}': x={normalizedX}, y={normalizedY}, w={normalizedWidth}, h={normalizedHeight}");
                
                // Create line visualizer
                GameObject lineVisualizer = null;
                try
                {
                    if (ocrLinePrefab != null)
                    {
                        Debug.Log($"Using OCR line prefab: {ocrLinePrefab.name} for line: '{lineText}'");
                        lineVisualizer = Instantiate(ocrLinePrefab, ocrPanel);
                        if (lineVisualizer != null)
                        {
                            Debug.Log($"Created line visualizer from prefab for line: '{lineText}'");
                            createdCount++;
                        }
                        else
                        {
                            Debug.LogError($"Failed to instantiate prefab for line: '{lineText}'");
                        }
                    }
                    else
                    {
                        // Fallback to creating a UI element
                        Debug.Log($"No OCR line prefab found, creating fallback UI element for line: '{lineText}'");
                        lineVisualizer = new GameObject("Line_" + lineText);
                        lineVisualizer.transform.SetParent(ocrPanel, false);
                        
                        // Add RectTransform
                        RectTransform rt = lineVisualizer.AddComponent<RectTransform>();
                        
                        // Add Image component for background
                        Image img = lineVisualizer.AddComponent<Image>();
                        img.color = Color.white;
                        
                        if (lineVisualizer != null)
                        {
                            Debug.Log($"Created fallback UI element for line: '{lineText}'");
                            createdCount++;
                        }
                        else
                        {
                            Debug.LogError($"Failed to create UI element for line: '{lineText}'");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error creating line visualizer for '{lineText}': {ex.Message}");
                    continue;
                }
                
                if (lineVisualizer == null)
                {
                    Debug.LogError($"Failed to create visualizer for line: '{lineText}'");
                    continue;
                }
                
                Debug.Log($"Successfully created visualizer for '{lineText}', configuring properties");
                
                // Set line visualizer properties
                lineVisualizer.name = "Line_" + lineText;
                
                // Configure RectTransform for proper UI positioning
                RectTransform rectTransform = lineVisualizer.GetComponent<RectTransform>();
                if (rectTransform != null)
                {
                    // Calculate the size based on normalized dimensions
                    float width = normalizedWidth * ocrPanel.GetComponent<RectTransform>().rect.width;
                    float height = normalizedHeight * ocrPanel.GetComponent<RectTransform>().rect.height;
                    
                    // Set the rect size
                    rectTransform.sizeDelta = new Vector2(width, height);
                    
                    // Use centered anchors for position-based layout
                    rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                    rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                    rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    
                    // Calculate the center position of this element in normalized space
                    float centerX = normalizedX + (normalizedWidth / 2);
                    float centerY = 1 - (normalizedY + (normalizedHeight / 2)); // Invert Y for Unity UI
                    
                    // Convert to panel local coordinates
                    float posX = (centerX - 0.5f) * ocrPanel.GetComponent<RectTransform>().rect.width;
                    float posY = (centerY - 0.5f) * ocrPanel.GetComponent<RectTransform>().rect.height;
                    
                    // Set the position
                    rectTransform.localPosition = new Vector3(posX, posY, 0);
                    
                    Debug.Log($"Updated position for '{lineText}': size = {rectTransform.sizeDelta}, localPosition = {rectTransform.localPosition}");
                }
                
                // Apply appropriate material based on whether this is the pointed line
                bool isPointedLine = lineText == currentPointedArea;
                if (isPointedLine && hasPointedArea)
                {
                    Debug.Log($"Applying highlighted material to pointed line: '{lineText}'");
                    
                    // Get the Image component and change its color/material
                    Image img = lineVisualizer.GetComponent<Image>();
                    if (img != null && highlightedLineMaterial != null)
                    {
                        img.material = highlightedLineMaterial;
                        img.color = Color.yellow;
                    }
                    
                    // Make the pointed line slightly larger
                    if (rectTransform != null)
                    {
                        rectTransform.localScale = Vector3.one * 1.1f;
                    }
                }
                else
                {
                    Debug.Log($"Applying normal material to line: '{lineText}'");
                    
                    // Get the Image component and change its color/material
                    Image img = lineVisualizer.GetComponent<Image>();
                    if (img != null && normalLineMaterial != null)
                    {
                        img.material = normalLineMaterial;
                        img.color = new Color(1f, 1f, 1f, 0.5f); // Semi-transparent white
                    }
                }
                
                // Add TextMeshPro component for text display
                TextMeshPro tmpText = lineVisualizer.GetComponentInChildren<TextMeshPro>();
                if (tmpText == null)
                {
                    Debug.Log($"Creating new TextMeshPro for '{lineText}'");
                    GameObject textObj = new GameObject("Text");
                    textObj.transform.SetParent(lineVisualizer.transform, false);
                    
                    // Add RectTransform to text and make it fill the parent
                    RectTransform textRect = textObj.AddComponent<RectTransform>();
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = Vector2.zero;
                    textRect.offsetMax = Vector2.zero;
                    
                    tmpText = textObj.AddComponent<TextMeshPro>();
                    tmpText.alignment = TextAlignmentOptions.Center;
                    tmpText.fontSize = 14;
                }
                
                // Set text
                if (tmpText != null)
                {
                    tmpText.text = lineText;
                    Debug.Log($"Set text to '{lineText}'");
                    
                    // Additional text configuration to ensure proper display
                    tmpText.textWrappingMode = TextWrappingModes.Normal;
                    tmpText.overflowMode = TextOverflowModes.Truncate;
                    tmpText.margin = new Vector4(2f, 2f, 2f, 2f); // Add small margins
                    // tmpText.enableAutoSizing = true; // Enable auto sizing for better fit
                    tmpText.fontSizeMin = 8;
                    tmpText.fontSizeMax = 18;
                }
                
                // Add to tracking list
                ocrLineVisualizers.Add(lineVisualizer);
                Debug.Log($"Added '{lineText}' visualizer to tracking list");
            }
            
            // After all lines are created, adjust the canvas size to fit the content
            AdjustCanvasSizeToContent();
            
            // Update positions of all line visualizers
            UpdateLineVisualizerPositions();
            
            // Debug bounding boxes
            DebugBoundingBoxes();
            
            // Log the final world scale for debugging
            LogCanvasWorldScale();
            
            // Activate the container
            ocrLineCanvas.gameObject.SetActive(true);
            Debug.Log($"Finished setting up OCR visualization with {createdCount} line visualizers created out of {labelBoundingBoxes.Count} labels");
        }
        else
        {
            Debug.LogWarning("No bounding boxes available for visualization");
        }
        
        Debug.Log("EXIT UpdatePointingVisualization");
    }
    
    /// <summary>
    /// Calculate the overall bounding box that encompasses all text lines
    /// </summary>
    private Rect CalculateOverallBoundingBox()
    {
        if (labelBoundingBoxes.Count == 0)
            return new Rect(0, 0, 1, 1);
            
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        
        foreach (var kvp in labelBoundingBoxes)
        {
            Rect bounds = kvp.Value;
            minX = Mathf.Min(minX, bounds.x);
            minY = Mathf.Min(minY, bounds.y);
            maxX = Mathf.Max(maxX, bounds.x + bounds.width);
            maxY = Mathf.Max(maxY, bounds.y + bounds.height);
        }
        
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private void HandlePointingStateChanged(bool isPointing)
    {
        if (!isPointing)
        {
            // Clear current pointed area
            currentPointedArea = "";
            
            // Clean up pointing visualizations
            if (pointingPlane != null)
            {
                // Disable the plane and its LazyFollow component
                var lazyFollow = pointingPlane.GetComponent<DualTargetLazyFollow>();
                if (lazyFollow != null)
                {
                    Destroy(lazyFollow);
                }
                pointingPlane.gameObject.SetActive(false);
            }
            
            // Clean up OCR line visualizers
            if (ocrLineCanvas != null)
            {
                ocrLineCanvas.gameObject.SetActive(false);
                
                foreach (var visualizer in ocrLineVisualizers)
                {
                    Destroy(visualizer);
                }
                ocrLineVisualizers.Clear();
            }
        }
        else
        {
            if (pointingPlane != null)
            {
                pointingPlane.gameObject.SetActive(true);
            }
            
            if (ocrLineCanvas != null)
            {
                ocrLineCanvas.gameObject.SetActive(true);
            }
            
            UpdatePointingVisualization();
        }
    }
    
    /// <summary>
    /// Updates which area is being pointed at
    /// </summary>
    public void UpdatePointedArea(string areaText)
    {
        currentPointedArea = areaText;
        
        // Update the pointing visualization to highlight the new area
        if (currentlyPointing)
        {
            UpdatePointingVisualization();
        }
    }

    /// <summary>
    /// (Granularity Lv1) Coroutine that captures the camera frame, sends it to Gemini,
    /// parses a JSON array of user questions, and spawns UI lines for each question.
    /// </summary>
    private IEnumerator GenerateQuestionsRoutine(string labelContent)
    {
        // Clear any previous questions
        ClearPreviousQuestions();
        
        // Capture the current frame
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        
        // Convert to base64
        string base64Image = ConvertTextureToBase64(frameTex);
        
        // Build the prompt
        string prompt = $"Analyze this image showing a {labelContent}. Generate 3-5 specific questions that someone might ask about this object. Focus on questions that can be answered by looking at the image. Format as a JSON array of strings.";
        
        // Call Gemini with the image
        var request = geminiGeneral.MakeGeminiRequest(prompt, base64Image);
        while (!request.IsCompleted)
            yield return null;
            
        string response = request.Result;
        
        // Try to extract JSON from the response
        string extractedJson = TryExtractJson(response);
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
        // Capture the current frame
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        
        // Convert to base64
        string base64Image = ConvertTextureToBase64(frameTex);
        
        // Get all known objects from the scene manager
        UpdateKnownObjectLabels();
        
        // Build the prompt
        string objectsContext = knownObjectLabels.Count > 0 ? 
            "Known objects in the scene: " + string.Join(", ", knownObjectLabels) : 
            "No other known objects detected yet.";
            
        string sceneContext = !string.IsNullOrEmpty(currentSceneContext) ? 
            $"Current environment: {currentSceneContext}" : 
            "Unknown environment";
            
        string taskContext = !string.IsNullOrEmpty(currentTaskContext) ? 
            $"Current task context: {currentTaskContext}" : 
            "No specific task";
        
        string prompt = $@"
            Analyze this image showing a person holding a {inHandLabel}.
            
            {objectsContext}
            {sceneContext}
            {taskContext}
            
            Generate a JSON response with:
            1. A list of potential relationships between the {inHandLabel} and other objects in the scene
            2. Possible actions the user might want to take with the {inHandLabel} in this context
            
            Format as:
            {{
              ""relationships"": [
                {{
                  ""relatedObject"": ""object name"",
                  ""relationship"": ""description of relationship""
                }},
                ...
              ],
              ""possibleActions"": [
                ""action 1"",
                ""action 2"",
                ...
              ]
            }}
        ";
        
        // Call Gemini with the image
        var request = geminiGeneral.MakeGeminiRequest(prompt, base64Image);
        while (!request.IsCompleted)
            yield return null;
            
        string response = request.Result;
        
        // Try to extract JSON from the response
        string extractedJson = TryExtractJson(response);
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
        relationLineManager.ShowRelationships(myAnchor, relationshipsDict, sceneObjManager.GetAllAnchors());
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

    // Add this class to parse the JSON response
    [Serializable]
    private class PointingDescription
    {
        public bool isPointing;
        public string pointedArea;
        public string pointingHand;
        public string description;
    }

    // Helper class to handle OCR results
    public class OCRResultHandler
    {
        public delegate void OCRCompleteHandler(string fullText, List<string> wordList, Dictionary<string, BoundingBox> wordBoundingBoxes = null);
        
        public event OCRCompleteHandler OnOCRComplete;
        
        public bool IsComplete { get; private set; } = false;
        
        public void HandleOCRResult(string fullText, List<string> wordList, Dictionary<string, BoundingBox> wordBoundingBoxes = null)
        {
            OnOCRComplete?.Invoke(fullText, wordList, wordBoundingBoxes);
            IsComplete = true;
        }
    }

    // Class to represent a bounding box
    [Serializable]
    public class BoundingBox
    {
        public float MinX { get; set; }
        public float MinY { get; set; }
        public float MaxX { get; set; }
        public float MaxY { get; set; }
        
        public float Width => MaxX - MinX;
        public float Height => MaxY - MinY;
        
        public BoundingBox(float minX, float minY, float maxX, float maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }
        
        public override string ToString()
        {
            return $"({MinX}, {MinY}, {Width}, {Height})";
        }
    }

    // Add this class to parse the OCR comparison decision
    [Serializable]
    private class OCRComparisonDecision
    {
        public string action;
        public string reason;
        public string mergedText;
    }

    // Add a method to estimate product dimensions using Gemini
    private IEnumerator EstimateProductDimensionsRoutine()
    {
        if (hasDimensionEstimate || string.IsNullOrEmpty(optimalOCRContext))
        {
            yield break;
        }
        
        Debug.Log("Estimating product dimensions from OCR text");
        
        string prompt = $@"
            Based on the following OCR text extracted from a product, estimate its real-world physical dimensions (width and height in centimeters).
            
            OCR TEXT:
            {optimalOCRContext}
            
            For context, this text is from a Pocari Sweat package. Please analyze the text to determine:
            1. What type of packaging this is (box, packet, bottle, etc.)
            2. The approximate dimensions based on standard sizes for this product type
            
            Respond with a JSON object:
            {{
              ""productType"": ""box/packet/bottle/etc"",
              ""widthCm"": [estimated width in cm],
              ""heightCm"": [estimated height in cm],
              ""confidenceLevel"": ""high/medium/low"",
              ""reasoning"": ""brief explanation""
            }}
        ";
        
        // Call Gemini without an image
        var request = geminiGeneral.MakeGeminiRequest(prompt, null);
        while (!request.IsCompleted)
            yield return null;
            
        string rawResponse = request.Result;
        string jsonStr = TryExtractJson(rawResponse);
        
        if (!string.IsNullOrEmpty(jsonStr))
        {
            try
            {
                var sizeEstimate = JsonConvert.DeserializeObject<ProductSizeEstimate>(jsonStr);
                
                if (sizeEstimate != null)
                {
                    // Convert cm to meters for Unity scale
                    float widthMeters = sizeEstimate.widthCm / 100f;
                    float heightMeters = sizeEstimate.heightCm / 100f;
                    
                    // Update the estimated size
                    estimatedProductSize = new Vector2(widthMeters, heightMeters);
                    hasDimensionEstimate = true;
                    
                    Debug.Log($"Estimated product dimensions: {sizeEstimate.productType}, {sizeEstimate.widthCm}cm × {sizeEstimate.heightCm}cm ({estimatedProductSize.x}m × {estimatedProductSize.y}m)");
                    Debug.Log($"Confidence: {sizeEstimate.confidenceLevel}, Reasoning: {sizeEstimate.reasoning}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to parse product size estimate: {ex}\nJSON string: {jsonStr}");
            }
        }
    }

    // Add class to store product size estimate from LLM
    [Serializable]
    private class ProductSizeEstimate
    {
        public string productType;
        public float widthCm;
        public float heightCm;
        public string confidenceLevel;
        public string reasoning;
    }

    // Add this helper method to calculate world scale
    /// <summary>
    /// Gets the compound world scale of a transform (accounting for all parent scales)
    /// </summary>
    private Vector3 GetWorldScale(Transform transform)
    {
        Vector3 worldScale = transform.localScale;
        Transform parent = transform.parent;
        
        while (parent != null)
        {
            worldScale.x *= parent.localScale.x;
            worldScale.y *= parent.localScale.y;
            worldScale.z *= parent.localScale.z;
            parent = parent.parent;
        }
        
        return worldScale;
    }

    // Create a prefab for OCR lines programmatically
    private GameObject CreateOCRLinePrefab()
    {
        Debug.Log("Creating OCR line prefab programmatically");
        
        // Create a new GameObject for the prefab
        GameObject prefab = new GameObject("OCRLinePrefab");
        
        // Add a RectTransform component for UI positioning
        RectTransform rt = prefab.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f); // Center anchors
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);     // Center pivot
        rt.sizeDelta = new Vector2(100, 30);    // Default size
        
        // Add an Image component for the background
        Image img = prefab.AddComponent<Image>();
        img.color = new Color(0.2f, 0.2f, 0.2f, 0.7f); // Dark gray, semi-transparent
        
        // Find or create a child GameObject for the text
        GameObject textObj = prefab.transform.Find("Text")?.gameObject;
        TextMeshPro tmpText;
        
        if (textObj != null)
        {
            // Get the existing TextMeshPro component
            tmpText = textObj.GetComponent<TextMeshPro>();
            if (tmpText == null)
            {
                Debug.LogWarning("Text GameObject found but missing TextMeshPro component. Adding one.");
                tmpText = textObj.AddComponent<TextMeshPro>();
            }
        }
        else
        {
            // Create a new Text GameObject if it doesn't exist
            textObj = new GameObject("Text");
            textObj.transform.SetParent(prefab.transform, false);
            
            // Add RectTransform to the text and make it fill the parent
            RectTransform textRt = textObj.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(2, 2); // 2px padding
            textRt.offsetMax = new Vector2(-2, -2); // 2px padding
            
            // Add TextMeshPro component
            tmpText = textObj.AddComponent<TextMeshPro>();
        }
        
        // Configure the TextMeshPro component
        tmpText.alignment = TextAlignmentOptions.Center;
        tmpText.fontSize = 14;
        tmpText.color = Color.white;
        tmpText.textWrappingMode = TextWrappingModes.Normal;
        tmpText.overflowMode = TextOverflowModes.Truncate;
        tmpText.enableAutoSizing = true;
        tmpText.fontSizeMin = 8;
        tmpText.fontSizeMax = 18;
        
        Debug.Log("OCR line prefab created successfully");
        return prefab;
    }

    // Method to adjust the canvas size to better fit the content
    private void AdjustCanvasSizeToContent()
    {
        if (ocrLineCanvas == null || ocrLineVisualizers.Count == 0)
            return;
            
        Debug.Log("Adjusting canvas size to fit content...");
        
        // Find the panel that contains all the lines
        Transform ocrPanel = ocrLineCanvas.transform.Find("OCRPanel");
        if (ocrPanel == null)
            return;
            
        // Calculate the content bounds based on the child rect transforms
        Vector2 minAnchor = Vector2.one;
        Vector2 maxAnchor = Vector2.zero;
        
        foreach (GameObject visualizer in ocrLineVisualizers)
        {
            RectTransform rt = visualizer.GetComponent<RectTransform>();
            if (rt != null)
            {
                // Expand bounds to include this line
                minAnchor.x = Mathf.Min(minAnchor.x, rt.anchorMin.x);
                minAnchor.y = Mathf.Min(minAnchor.y, rt.anchorMin.y);
                maxAnchor.x = Mathf.Max(maxAnchor.x, rt.anchorMax.x);
                maxAnchor.y = Mathf.Max(maxAnchor.y, rt.anchorMax.y);
            }
        }
        
        // Add padding around the content (10% on each side)
        float paddingX = (maxAnchor.x - minAnchor.x) * 0.1f;
        float paddingY = (maxAnchor.y - minAnchor.y) * 0.1f;
        
        minAnchor.x = Mathf.Max(0, minAnchor.x - paddingX);
        minAnchor.y = Mathf.Max(0, minAnchor.y - paddingY);
        maxAnchor.x = Mathf.Min(1, maxAnchor.x + paddingX);
        maxAnchor.y = Mathf.Min(1, maxAnchor.y + paddingY);
        
        // Adjust the panel's content area to tightly fit the lines
        RectTransform canvasRect = ocrLineCanvas.GetComponent<RectTransform>();
        if (canvasRect != null)
        {
            // Calculate the content width and height in world units
            float contentWidth = canvasRect.sizeDelta.x * (maxAnchor.x - minAnchor.x);
            float contentHeight = canvasRect.sizeDelta.y * (maxAnchor.y - minAnchor.y);
            
            // If the content is too small, enforce a minimum size
            contentWidth = Mathf.Max(contentWidth, 10f);
            contentHeight = Mathf.Max(contentHeight, 10f);
            
            // Store the old size to check if we need to update positions
            Vector2 oldSize = canvasRect.sizeDelta;
            
            // Set a new canvas size based on the content
            canvasRect.sizeDelta = new Vector2(contentWidth, contentHeight);
            
            Debug.Log($"Adjusted canvas size from {oldSize} to: {contentWidth}x{contentHeight} to fit content between anchors {minAnchor} and {maxAnchor}");
            
            // If the size changed significantly, we need to update line positions
            if (Mathf.Abs(oldSize.x - contentWidth) > 0.1f || Mathf.Abs(oldSize.y - contentHeight) > 0.1f)
            {
                Debug.Log("Canvas size changed significantly, updating line positions");
                // Force canvas update
                Canvas.ForceUpdateCanvases();
                
                // Update line positions with the new canvas size
                UpdateLinePositionsAfterCanvasResize();
            }
        }
    }
    
    // Update line positions after canvas resize
    private void UpdateLinePositionsAfterCanvasResize()
    {
        if (ocrLineVisualizers.Count == 0 || labelBoundingBoxes.Count == 0)
            return;
            
        Debug.Log("Updating line positions after canvas resize");
        
        // Calculate the overall bounding box
        Rect overallBounds = CalculateOverallBoundingBox();
        
        // Find the panel that contains all the lines
        Transform ocrPanel = ocrLineCanvas.transform.Find("OCRPanel");
        if (ocrPanel == null)
            return;
            
        RectTransform panelRect = ocrPanel.GetComponent<RectTransform>();
        if (panelRect == null)
            return;
            
        // Get the panel's width and height
        float panelWidth = panelRect.rect.width;
        float panelHeight = panelRect.rect.height;
        
        Debug.Log($"Panel dimensions: {panelWidth}x{panelHeight}");
        
        // Update each line visualizer position
        foreach (GameObject visualizer in ocrLineVisualizers)
        {
            if (visualizer == null)
                continue;
                
            string lineText = visualizer.name.Replace("Line_", "");
            
            if (labelBoundingBoxes.TryGetValue(lineText, out Rect lineBounds))
            {
                // Calculate normalized position within overall bounds (0-1 range)
                float normalizedX = (lineBounds.x - overallBounds.x) / overallBounds.width;
                float normalizedY = (lineBounds.y - overallBounds.y) / overallBounds.height;
                float normalizedWidth = lineBounds.width / overallBounds.width;
                float normalizedHeight = lineBounds.height / overallBounds.height;
                
                // Update RectTransform
                RectTransform rectTransform = visualizer.GetComponent<RectTransform>();
                if (rectTransform != null)
                {
                    // Calculate the size based on normalized dimensions
                    float width = normalizedWidth * panelWidth;
                    float height = normalizedHeight * panelHeight;
                    
                    // Set the rect size
                    rectTransform.sizeDelta = new Vector2(width, height);
                    
                    // Use centered anchors for position-based layout
                    rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                    rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                    rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    
                    // Calculate the center position of this element in normalized space
                    float centerX = normalizedX + (normalizedWidth / 2);
                    float centerY = 1 - (normalizedY + (normalizedHeight / 2)); // Invert Y for Unity UI
                    
                    // Convert to panel local coordinates
                    float posX = (centerX - 0.5f) * panelWidth;
                    float posY = (centerY - 0.5f) * panelHeight;
                    
                    // Set the position
                    rectTransform.localPosition = new Vector3(posX, posY, 0);
                    
                    Debug.Log($"Updated position after resize for '{lineText}': size = {rectTransform.sizeDelta}, localPosition = {rectTransform.localPosition}");
                }
            }
        }
        
        // Force canvas update
        Canvas.ForceUpdateCanvases();
    }

    // Log the actual world scale of the canvas for debugging
    private void LogCanvasWorldScale()
    {
        if (ocrLineCanvas == null)
            return;
            
        // Get the local scale
        Vector3 localScale = ocrLineCanvas.transform.localScale;
        
        // Get the world scale
        Vector3 worldScale = ocrLineCanvas.transform.lossyScale;
        
        // Calculate the effective scale (what size 1 unit in the canvas appears as in world space)
        Vector3 effectiveScale = new Vector3(
            worldScale.x / pointingPlane.lossyScale.x,
            worldScale.y / pointingPlane.lossyScale.y,
            worldScale.z / pointingPlane.lossyScale.z
        );
        
        Debug.Log($"Canvas scales - Local: {localScale}, World: {worldScale}, Effective: {effectiveScale}");
        
        // Log the actual world size of the canvas
        RectTransform canvasRect = ocrLineCanvas.GetComponent<RectTransform>();
        if (canvasRect != null)
        {
            Vector2 canvasSize = canvasRect.sizeDelta;
            Vector2 worldSize = new Vector2(
                canvasSize.x * worldScale.x,
                canvasSize.y * worldScale.y
            );
            
            Debug.Log($"Canvas world size: {worldSize.x}m × {worldSize.y}m (from rect size: {canvasSize.x} × {canvasSize.y})");
        }
    }

    // Update positions of all line visualizers based on current bounding boxes
    private void UpdateLineVisualizerPositions()
    {
        if (ocrLineVisualizers.Count == 0 || labelBoundingBoxes.Count == 0)
            return;
            
        Debug.Log("Updating positions of all line visualizers");
        
        // Calculate the overall bounding box
        Rect overallBounds = CalculateOverallBoundingBox();
        
        // Find the panel that contains all the lines
        Transform ocrPanel = ocrLineCanvas.transform.Find("OCRPanel");
        if (ocrPanel == null)
            return;
            
        bool hasPointedArea = !string.IsNullOrEmpty(currentPointedArea);
            
        // Update each line visualizer position
        foreach (GameObject visualizer in ocrLineVisualizers)
        {
            if (visualizer == null)
                continue;
                
            string lineText = visualizer.name.Replace("Line_", "");
            
            if (labelBoundingBoxes.TryGetValue(lineText, out Rect lineBounds))
            {
                // Calculate normalized position within overall bounds (0-1 range)
                float normalizedX = (lineBounds.x - overallBounds.x) / overallBounds.width;
                float normalizedY = (lineBounds.y - overallBounds.y) / overallBounds.height;
                float normalizedWidth = lineBounds.width / overallBounds.width;
                float normalizedHeight = lineBounds.height / overallBounds.height;
                
                // Update RectTransform
                RectTransform rectTransform = visualizer.GetComponent<RectTransform>();
                if (rectTransform != null)
                {
                    rectTransform.anchorMin = new Vector2(normalizedX, 1 - normalizedY - normalizedHeight);
                    rectTransform.anchorMax = new Vector2(normalizedX + normalizedWidth, 1 - normalizedY);
                    rectTransform.anchoredPosition = Vector2.zero;
                    rectTransform.sizeDelta = Vector2.zero;
                    
                    // Set the local position explicitly to ensure proper positioning
                    rectTransform.localPosition = new Vector3(
                        (normalizedX + normalizedWidth/2 - 0.5f) * ocrPanel.GetComponent<RectTransform>().rect.width,
                        (0.5f - (1 - normalizedY - normalizedHeight/2)) * ocrPanel.GetComponent<RectTransform>().rect.height,
                        0
                    );
                    
                    Debug.Log($"Updated position for '{lineText}': anchors = {rectTransform.anchorMin} to {rectTransform.anchorMax}, localPosition = {rectTransform.localPosition}");
                }
                
                // Update material/color based on whether this is the pointed line
                bool isPointedLine = lineText == currentPointedArea;
                Image img = visualizer.GetComponent<Image>();
                
                if (img != null)
                {
                    if (isPointedLine && hasPointedArea)
                    {
                        // Apply highlighted material/color
                        if (highlightedLineMaterial != null)
                        {
                            img.material = highlightedLineMaterial;
                        }
                        img.color = Color.yellow;
                        
                        // Make the pointed line slightly larger
                        if (rectTransform != null)
                        {
                            rectTransform.localScale = Vector3.one * 1.1f;
                        }
                    }
                    else
                    {
                        // Apply normal material/color
                        if (normalLineMaterial != null)
                        {
                            img.material = normalLineMaterial;
                        }
                        img.color = new Color(1f, 1f, 1f, 0.5f); // Semi-transparent white
                        
                        // Reset scale
                        if (rectTransform != null)
                        {
                            rectTransform.localScale = Vector3.one;
                        }
                    }
                }
            }
        }
        
        // Force canvas update
        Canvas.ForceUpdateCanvases();
    }

    // Debug method to log all bounding boxes
    private void DebugBoundingBoxes()
    {
        if (labelBoundingBoxes.Count == 0)
        {
            Debug.LogWarning("No bounding boxes to debug");
            return;
        }
        
        Debug.Log("=== BOUNDING BOX DEBUG ===");
        Debug.Log($"Total bounding boxes: {labelBoundingBoxes.Count}");
        
        Rect overallBounds = CalculateOverallBoundingBox();
        Debug.Log($"Overall bounds: {overallBounds}");
        
        foreach (var kvp in labelBoundingBoxes)
        {
            string lineText = kvp.Key;
            Rect bounds = kvp.Value;
            
            // Calculate normalized position
            float normalizedX = (bounds.x - overallBounds.x) / overallBounds.width;
            float normalizedY = (bounds.y - overallBounds.y) / overallBounds.height;
            float normalizedWidth = bounds.width / overallBounds.width;
            float normalizedHeight = bounds.height / overallBounds.height;
            
            Debug.Log($"Line: '{lineText}' - Bounds: {bounds}, Normalized: X={normalizedX:F3}, Y={normalizedY:F3}, W={normalizedWidth:F3}, H={normalizedHeight:F3}");
            
            // Find the corresponding visualizer
            GameObject visualizer = ocrLineVisualizers.Find(v => v != null && v.name == "Line_" + lineText);
            if (visualizer != null)
            {
                RectTransform rt = visualizer.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Debug.Log($"  Visualizer: AnchorMin={rt.anchorMin}, AnchorMax={rt.anchorMax}, LocalPos={rt.localPosition}");
                }
                else
                {
                    Debug.LogWarning($"  Visualizer for '{lineText}' has no RectTransform");
                }
            }
            else
            {
                Debug.LogWarning($"  No visualizer found for '{lineText}'");
            }
        }
        
        Debug.Log("=== END BOUNDING BOX DEBUG ===");
    }
}
