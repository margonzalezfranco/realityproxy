using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using Newtonsoft.Json;

/// <summary>
/// Analyzes frames to detect if hands are grabbing objects using Gemini vision model.
/// </summary>
public class HandGrabbingDetector : GeminiGeneral
{
    [Header("Analysis Settings")]
    [Tooltip("Time between hand grabbing detection calls (in seconds)")]
    public float detectionPeriod = 1f;

    [Tooltip("Whether the detector is currently running")]
    public bool isDetecting = true;

    [Tooltip("Enable detailed debug logging")]
    public bool enableDebugLogging = true;

    [Header("Optional References")]
    [Tooltip("SceneObjectManager to get current objects in scene")]
    public SceneObjectManager sceneManager;

    [Header("Hand References")]
    [Tooltip("Reference to the left hand GameObject")]
    public GameObject leftHandObject;
    
    [Tooltip("Reference to the right hand GameObject")]
    public GameObject rightHandObject;

    [Header("Current Detection Results")]
    [SerializeField, ReadOnly]
    private bool isHandGrabbing = false;
    
    [SerializeField, ReadOnly]
    private string grabbedObjectName = "None";
    
    [SerializeField, ReadOnly]
    private string grabbingHand = "None";
    
    [SerializeField, ReadOnly]
    private string lastDetectionTime = "Never";

    // Event that other components can subscribe to
    public System.Action<GrabbingInfo> OnGrabbingDetected;
    public System.Action OnGrabbingReleased;
    public System.Action<GrabbingInfo> OnGrabbingUpdated; // New event for label updates

    private GrabbingInfo currentGrabbingInfo;
    private bool wasGrabbingLastFrame = false;
    private string lastGrabbedObjectName = "";

    // List of known object labels in the scene
    private List<string> knownObjectLabels = new List<string>();
    
    // Minimum number of consistent detections before firing events
    private int minConsistentDetections = 2;
    private Dictionary<string, int> detectionCounts = new Dictionary<string, int>();
    private string mostLikelyObject = null;
    
    // Tracking for grabbing state consistency
    private int consecutiveGrabbingDetections = 0;
    private int consecutiveNonGrabbingDetections = 0;
    private int requiredConsecutiveDetections = 2; // How many consecutive detections needed to change state

    // Track which hand is grabbing
    private Dictionary<string, int> handDetectionCounts = new Dictionary<string, int>() {
        { "left", 0 },
        { "right", 0 }
    };
    // private string mostLikelyHand = null;

    // Track the currently running detection coroutine
    private bool isDetectionInProgress = false;
    private float lastDetectionStartTime = 0f;

    // Track the timestamp of each detection to determine which is most recent
    private long currentDetectionId = 0;
    private long lastProcessedDetectionId = -1;

    private void Start()
    {
        StartCoroutine(PeriodicDetectionRoutine());
        
        // Initialize known object labels
        UpdateKnownObjectLabels();
        
        // Auto-find hand references if not set
        if (leftHandObject == null || rightHandObject == null)
        {
            FindHandReferences();
        }
    }
    
    private void FindHandReferences()
    {
        // Try to find hands by common naming patterns
        if (leftHandObject == null)
        {
            leftHandObject = GameObject.Find("LeftHand") ?? 
                             GameObject.Find("Left Hand") ?? 
                             GameObject.Find("Hand_L");
            
            if (leftHandObject != null && enableDebugLogging)
            {
                Debug.Log($"[HandGrabbingDetector] Auto-found left hand: {leftHandObject.name}");
            }
        }
        
        if (rightHandObject == null)
        {
            rightHandObject = GameObject.Find("RightHand") ?? 
                              GameObject.Find("Right Hand") ?? 
                              GameObject.Find("Hand_R");
            
            if (rightHandObject != null && enableDebugLogging)
            {
                Debug.Log($"[HandGrabbingDetector] Auto-found right hand: {rightHandObject.name}");
            }
        }
    }

    private void UpdateKnownObjectLabels()
    {
        knownObjectLabels.Clear();
        
        if (sceneManager != null)
        {
            var anchors = sceneManager.GetAllAnchors();
            if (anchors != null && anchors.Count > 0)
            {
                foreach (var anchor in anchors)
                {
                    knownObjectLabels.Add(anchor.label);
                }
                
                if (enableDebugLogging)
                {
                    Debug.Log($"[HandGrabbingDetector] Updated known object labels: {string.Join(", ", knownObjectLabels)}");
                }
            }
        }
    }

    private IEnumerator PeriodicDetectionRoutine()
    {
        while (true)
        {
            // Check if there's an active anchor before running detection
            bool hasActiveAnchor = HandGrabTrigger.HasActiveAnchor();
            
            if (isDetecting && hasActiveAnchor)
            {
                // Start a new detection if we're not already detecting or if the previous one is taking too long
                float currentTime = Time.time;
                if (!isDetectionInProgress || (currentTime - lastDetectionStartTime > detectionPeriod * 2))
                {
                    // If there's a previous detection that's taking too long, we'll let it continue
                    // but we won't wait for it to complete before starting a new one
                    if (isDetectionInProgress && enableDebugLogging)
                    {
                        Debug.Log($"[HandGrabbingDetector] Previous detection taking too long ({currentTime - lastDetectionStartTime:F1}s). Starting new detection.");
                    }
                    
                    // Start a new detection
                    lastDetectionStartTime = currentTime;
                    StartCoroutine(DetectHandGrabbingRoutineWithTracking());
                }
            }
            else if (enableDebugLogging && !hasActiveAnchor && isDetecting)
            {
                Debug.Log("[HandGrabbingDetector] Skipping detection - no active anchor currently toggled");
            }
            
            // Always wait for the detection period before checking again
            yield return new WaitForSeconds(detectionPeriod);
        }
    }
    
    private IEnumerator DetectHandGrabbingRoutineWithTracking()
    {
        isDetectionInProgress = true;
        
        yield return StartCoroutine(DetectHandGrabbingRoutine());
        
        isDetectionInProgress = false;
    }

    private IEnumerator DetectHandGrabbingRoutine()
    {
        // Generate a unique ID for this detection
        long thisDetectionId = System.Threading.Interlocked.Increment(ref currentDetectionId);
        float startTime = Time.time;
        
        // Update known object labels before detection
        UpdateKnownObjectLabels();
        
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);

        // 2) Convert to base64
        string base64Image = ConvertTextureToBase64(frameTex);

        // 3) Build context-aware prompt
        string contextPrompt = BuildContextAwarePrompt();

        // 4) Call Gemini
        var request = MakeGeminiRequest(contextPrompt, base64Image);
        
        // Wait for the request to complete (no timeout)
        while (!request.IsCompleted)
        {
            yield return null;
        }
        
        // Log how long the request took
        if (enableDebugLogging)
        {
            Debug.Log($"[HandGrabbingDetector] API request {thisDetectionId} completed in {Time.time - startTime:F1}s");
        }
        
        string response = request.Result;

        // 5) Parse response
        GrabbingInfo grabbingInfo = ParseGrabbingResponse(response);
        
        // 6) Only update if this is the most recent completed detection
        // Use Interlocked to safely check and update the last processed ID
        if (thisDetectionId > System.Threading.Interlocked.Read(ref lastProcessedDetectionId))
        {
            System.Threading.Interlocked.Exchange(ref lastProcessedDetectionId, thisDetectionId);
            
            if (enableDebugLogging)
            {
                Debug.Log($"[HandGrabbingDetector] Processing results from detection {thisDetectionId} (most recent)");
            }
            
            // Update inspector and notify subscribers
            HandleGrabbingDetection(grabbingInfo);
        }
        else if (enableDebugLogging)
        {
            Debug.Log($"[HandGrabbingDetector] Skipping results from detection {thisDetectionId} (newer detection {lastProcessedDetectionId} already processed)");
        }

        // Clean up
        Destroy(frameTex);
    }

    private string BuildContextAwarePrompt()
    {
        string objectContext = "";
        if (knownObjectLabels.Count > 0)
        {
            objectContext = "Currently detected objects: " + string.Join(", ", knownObjectLabels);
        }

        return $"Analyze this image and determine if a hand is grabbing any object. Provide a JSON response with the following structure:\n" +
               "{\n" +
               "  \"isGrabbing\": true/false,\n" +
               "  \"grabbedObject\": \"[object name or 'unknown' if unclear]\",\n" +
               "  \"grabbingHand\": \"left\"/\"right\",\n" +
               "  \"confidence\": [0.0-1.0 confidence score]\n" +
               "}\n\n" +
               $"{objectContext}\n" +
               "Focus on hand positions and gestures that indicate grabbing.\n" +
               "IMPORTANT: If you detect grabbing, the grabbedObject MUST be one of these exact names: " + string.Join(", ", knownObjectLabels) + "\n" +
               "If the object being grabbed doesn't match any of these names, use the closest match from the list.\n" +
               "Always specify which hand (left or right) is doing the grabbing in the grabbingHand field.\n" +
               "If no grabbing is detected, set isGrabbing to false and leave other fields with default values.";
    }

    private GrabbingInfo ParseGrabbingResponse(string response)
    {
        try
        {
            string jsonText = ParseGeminiRawResponse(response);
            if (string.IsNullOrEmpty(jsonText)) return null;

            GrabbingInfo grabbingInfo = JsonConvert.DeserializeObject<GrabbingInfo>(jsonText);
            
            // Validate and normalize the grabbed object name
            if (grabbingInfo != null && grabbingInfo.isGrabbing)
            {
                // Normalize object name
                if (!string.IsNullOrEmpty(grabbingInfo.grabbedObject))
                {
                    // Try to find an exact match first
                    string exactMatch = knownObjectLabels.Find(label => 
                        string.Equals(label, grabbingInfo.grabbedObject, System.StringComparison.OrdinalIgnoreCase));
                    
                    if (!string.IsNullOrEmpty(exactMatch))
                    {
                        // Use the exact case from our known labels
                        grabbingInfo.grabbedObject = exactMatch;
                    }
                    else
                    {
                        // If no exact match, find the closest match
                        string closestMatch = FindClosestMatch(grabbingInfo.grabbedObject, knownObjectLabels);
                        if (!string.IsNullOrEmpty(closestMatch))
                        {
                            if (enableDebugLogging)
                            {
                                Debug.Log($"[HandGrabbingDetector] Normalized object name from '{grabbingInfo.grabbedObject}' to '{closestMatch}'");
                            }
                            grabbingInfo.grabbedObject = closestMatch;
                        }
                    }
                }
                
                // Normalize hand name
                if (!string.IsNullOrEmpty(grabbingInfo.grabbingHand))
                {
                    // Normalize to lowercase
                    grabbingInfo.grabbingHand = grabbingInfo.grabbingHand.ToLower();
                    
                    // Make sure it's either "left" or "right"
                    if (grabbingInfo.grabbingHand != "left" && grabbingInfo.grabbingHand != "right")
                    {
                        // Default to right if unclear
                        grabbingInfo.grabbingHand = "right";
                        
                        if (enableDebugLogging)
                        {
                            Debug.Log($"[HandGrabbingDetector] Normalized invalid hand '{grabbingInfo.grabbingHand}' to 'right'");
                        }
                    }
                }
                else
                {
                    // Default to right if not specified
                    grabbingInfo.grabbingHand = "right";
                    
                    if (enableDebugLogging)
                    {
                        Debug.Log("[HandGrabbingDetector] No hand specified, defaulting to 'right'");
                    }
                }
            }
            
            return grabbingInfo;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[HandGrabbingDetector] Error parsing hand grabbing response: {ex}");
            return null;
        }
    }

    // Find the closest matching string from a list
    private string FindClosestMatch(string input, List<string> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;
        
        string bestMatch = null;
        float bestScore = 0;
        
        foreach (string candidate in candidates)
        {
            float score = CalculateSimilarity(input.ToLower(), candidate.ToLower());
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = candidate;
            }
        }
        
        // Only return a match if it's reasonably close (adjust threshold as needed)
        return bestScore > 0.5f ? bestMatch : null;
    }
    
    // Simple string similarity calculation (Jaccard similarity)
    private float CalculateSimilarity(string s1, string s2)
    {
        HashSet<char> set1 = new HashSet<char>(s1);
        HashSet<char> set2 = new HashSet<char>(s2);
        
        // Count common characters
        int intersection = 0;
        foreach (char c in set1)
        {
            if (set2.Contains(c)) intersection++;
        }
        
        // Calculate Jaccard similarity
        int union = set1.Count + set2.Count - intersection;
        return union > 0 ? (float)intersection / union : 0;
    }

    /// <summary>
    /// Manually trigger a hand grabbing detection outside the periodic routine
    /// </summary>
    public void TriggerDetection()
    {
        // Check if there's an active anchor before running detection
        bool hasActiveAnchor = HandGrabTrigger.HasActiveAnchor();
        
        if (!hasActiveAnchor)
        {
            if (enableDebugLogging)
            {
                Debug.Log("[HandGrabbingDetector] Cannot trigger detection - no active anchor currently toggled");
            }
            return;
        }
        
        // Only start a new detection if one isn't already in progress or if the current one is taking too long
        if (!isDetectionInProgress || (Time.time - lastDetectionStartTime > detectionPeriod * 2))
        {
            lastDetectionStartTime = Time.time;
            StartCoroutine(DetectHandGrabbingRoutineWithTracking());
        }
        else if (enableDebugLogging)
        {
            Debug.Log("[HandGrabbingDetector] Detection already in progress, skipping manual trigger");
        }
    }

    private void HandleGrabbingDetection(GrabbingInfo grabbingInfo)
    {
        if (grabbingInfo == null)
        {
            // Reset if analysis failed
            currentGrabbingInfo = null;
            isHandGrabbing = false;
            grabbedObjectName = "Detection failed";
            grabbingHand = "None";
            lastDetectionTime = System.DateTime.Now.ToString("HH:mm:ss");
            return;
        }

        // Track consecutive detections for grabbing state consistency
        if (grabbingInfo.isGrabbing)
        {
            consecutiveGrabbingDetections++;
            consecutiveNonGrabbingDetections = 0;
        }
        else
        {
            consecutiveNonGrabbingDetections++;
            consecutiveGrabbingDetections = 0;
        }

        // Apply consistency logic to grabbing state
        // Only change from grabbing to not grabbing after multiple consecutive non-grabbing detections
        if (isHandGrabbing && !grabbingInfo.isGrabbing && consecutiveNonGrabbingDetections < requiredConsecutiveDetections)
        {
            // Ignore this detection - we need more consecutive non-grabbing detections to confirm
            if (enableDebugLogging)
            {
                Debug.Log($"[HandGrabbingDetector] Ignoring potential false negative (still grabbing). Need {requiredConsecutiveDetections - consecutiveNonGrabbingDetections} more.");
            }
            return;
        }
        
        // Only change from not grabbing to grabbing after multiple consecutive grabbing detections
        if (!isHandGrabbing && grabbingInfo.isGrabbing && consecutiveGrabbingDetections < requiredConsecutiveDetections)
        {
            // Ignore this detection - we need more consecutive grabbing detections to confirm
            if (enableDebugLogging)
            {
                Debug.Log($"[HandGrabbingDetector] Ignoring potential false positive (not grabbing yet). Need {requiredConsecutiveDetections - consecutiveGrabbingDetections} more.");
            }
            return;
        }

        // Update detection counts for consistency tracking
        if (grabbingInfo.isGrabbing && !string.IsNullOrEmpty(grabbingInfo.grabbedObject))
        {
            // Increment count for this object
            if (!detectionCounts.ContainsKey(grabbingInfo.grabbedObject))
            {
                detectionCounts[grabbingInfo.grabbedObject] = 0;
            }
            detectionCounts[grabbingInfo.grabbedObject]++;

            // Find the object with the highest count
            int highestCount = 0;
            string highestObject = null;
            
            foreach (var kvp in detectionCounts)
            {
                if (kvp.Value > highestCount)
                {
                    highestCount = kvp.Value;
                    highestObject = kvp.Key;
                }
            }
            
            // Update most likely object
            mostLikelyObject = highestObject;
            
            // If we have a new most likely object with sufficient detections, use it
            if (mostLikelyObject != null && detectionCounts[mostLikelyObject] >= minConsistentDetections)
            {
                // Override the current detection with our most consistent object
                grabbingInfo.grabbedObject = mostLikelyObject;
            }
        }
        else if (!grabbingInfo.isGrabbing)
        {
            // Reset detection counts when not grabbing
            detectionCounts.Clear();
            mostLikelyObject = null;
        }

        // Store previous state for comparison
        bool wasGrabbing = isHandGrabbing;
        string previousObjectName = grabbedObjectName;
        string previousHand = grabbingHand;
        
        // Update current state
        currentGrabbingInfo = grabbingInfo;
        isHandGrabbing = grabbingInfo.isGrabbing;
        grabbedObjectName = grabbingInfo.isGrabbing ? (grabbingInfo.grabbedObject ?? "Unknown") : "None";
        grabbingHand = grabbingInfo.isGrabbing ? grabbingInfo.grabbingHand : "None";
        lastDetectionTime = System.DateTime.Now.ToString("HH:mm:ss");
        
        // Log only the final detection result
        if (enableDebugLogging && grabbingInfo.isGrabbing)
        {
            Debug.Log($"[HandGrabbingDetector] RESULT: {grabbingHand} hand grabbing {grabbedObjectName} (Confidence: {grabbingInfo.confidence:P0})");
        }
        
        // Handle state transitions
        if (grabbingInfo.isGrabbing)
        {
            if (!wasGrabbingLastFrame)
            {
                // Started grabbing
                if (enableDebugLogging)
                {
                    Debug.Log($"[HandGrabbingDetector] {grabbingHand} hand started grabbing: {grabbedObjectName}");
                }
                OnGrabbingDetected?.Invoke(grabbingInfo);
                wasGrabbingLastFrame = true;
                lastGrabbedObjectName = grabbedObjectName;
            }
            else if (grabbedObjectName != lastGrabbedObjectName || grabbingHand != previousHand)
            {
                // Object label or grabbing hand changed while still grabbing
                if (enableDebugLogging)
                {
                    if (grabbedObjectName != lastGrabbedObjectName)
                    {
                        Debug.Log($"[HandGrabbingDetector] Grabbed object updated from '{lastGrabbedObjectName}' to '{grabbedObjectName}'");
                    }
                    if (grabbingHand != previousHand)
                    {
                        Debug.Log($"[HandGrabbingDetector] Grabbing hand changed from '{previousHand}' to '{grabbingHand}'");
                    }
                }
                OnGrabbingUpdated?.Invoke(grabbingInfo);
                lastGrabbedObjectName = grabbedObjectName;
            }
        }
        else if (!grabbingInfo.isGrabbing && wasGrabbingLastFrame)
        {
            // Stopped grabbing
            if (enableDebugLogging)
            {
                Debug.Log("[HandGrabbingDetector] Hand released object");
            }
            OnGrabbingReleased?.Invoke();
            wasGrabbingLastFrame = false;
            lastGrabbedObjectName = "";
        }
    }

    public GrabbingInfo GetCurrentGrabbingInfo()
    {
        return currentGrabbingInfo;
    }
    
    /// <summary>
    /// Get the GameObject reference for the specified hand
    /// </summary>
    public GameObject GetHandObject(string handName)
    {
        if (handName == "left")
        {
            return leftHandObject;
        }
        else if (handName == "right")
        {
            return rightHandObject;
        }
        return null;
    }
}

[System.Serializable]
public class GrabbingInfo
{
    public bool isGrabbing;
    public string grabbedObject;
    public string grabbingHand;
    public float confidence;

    public override string ToString()
    {
        return $"GrabbingInfo(isGrabbing: {isGrabbing}, object: {grabbedObject}, hand: {grabbingHand}, confidence: {confidence:P0})";
    }
} 