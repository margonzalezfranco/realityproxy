using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using Newtonsoft.Json;
using System;

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

    private bool isDetectionInProgress = false;
    private float detectionTimeout = 15f; // Increased timeout to account for retries

    private Coroutine currentDetectionCoroutine = null;

    private float detectionStartTime = 0f;
    private float maxDetectionDuration = 20f; // Maximum time a detection should take before we consider it stuck

    // Add a request ID to track the latest request
    private int currentRequestId = 0;
    private int latestCompletedRequestId = 0;

    private IEnumerator ProcessGeminiRequest(string contextPrompt, string base64Image)
    {
        // Increment the request ID to track this request
        int thisRequestId = ++currentRequestId;
        
        if (enableDebugLogging)
        {
            Debug.Log($"[HandGrabbingDetector] Starting request #{thisRequestId}");
        }
        
        // Call Gemini API without try-catch
        yield return WaitForGeminiResponse(contextPrompt, base64Image);
        
        // Check if this request is still the most recent one
        if (thisRequestId < currentRequestId && Time.time - detectionStartTime > 3f)
        {
            if (enableDebugLogging)
            {
                Debug.Log($"[HandGrabbingDetector] Ignoring results from request #{thisRequestId} because newer request #{currentRequestId} is in progress");
            }
            
            // Don't process the result if a newer request is already in progress
            isDetectionInProgress = false;
            yield break;
        }
        
        // Process the result after the API call is complete
        ProcessDetectionResult(thisRequestId);
    }
    
    private IEnumerator WaitForGeminiResponse(string contextPrompt, string base64Image)
    {
        // Log the start of the API call
        if (enableDebugLogging)
        {
            Debug.Log($"[HandGrabbingDetector] Starting Gemini API call at {System.DateTime.Now.ToString("HH:mm:ss.fff")}");
            Debug.Log($"[HandGrabbingDetector] Image size: {base64Image.Length} characters");
            Debug.Log($"[HandGrabbingDetector] Prompt size: {contextPrompt.Length} characters");
        }
        
        // Call Gemini with timeout
        var request = geminiClient.GenerateContent(contextPrompt, base64Image);
        
        // Add timeout handling with more detailed logging
        float timeElapsed = 0f;
        float logInterval = 2.0f; // Log every 2 seconds
        float nextLogTime = logInterval;
        
        while (!request.IsCompleted && timeElapsed < detectionTimeout)
        {
            timeElapsed += Time.deltaTime;
            
            // Check if a newer request has been started
            if (currentRequestId > latestCompletedRequestId + 1 && timeElapsed > 3f)
            {
                if (enableDebugLogging)
                {
                    Debug.LogWarning("[HandGrabbingDetector] Newer request has started, abandoning this API call");
                }
                yield break;
            }
            
            // Log progress periodically
            if (enableDebugLogging && timeElapsed >= nextLogTime)
            {
                Debug.Log($"[HandGrabbingDetector] API call in progress for {timeElapsed:F1} seconds...");
                nextLogTime = timeElapsed + logInterval;
                
                // Check if the request has a status we can log
                if (request.Status != null)
                {
                    Debug.Log($"[HandGrabbingDetector] Request status: {request.Status}");
                }
            }
            
            yield return null;
        }
        
        // Store the response in a class variable
        if (timeElapsed >= detectionTimeout)
        {
            // Handle timeout with more detailed information
            if (enableDebugLogging)
            {
                Debug.LogWarning($"[HandGrabbingDetector] Detection timed out after {detectionTimeout} seconds at {System.DateTime.Now.ToString("HH:mm:ss.fff")}");
                
                // Try to get more information about the request
                if (request.Exception != null)
                {
                    Debug.LogError($"[HandGrabbingDetector] Request exception: {request.Exception}");
                }
                
                // Check network connectivity
                CheckNetworkConnectivity();
            }
            currentResponse = null;
        }
        else
        {
            try
            {
                if (enableDebugLogging)
                {
                    Debug.Log($"[HandGrabbingDetector] API call completed after {timeElapsed:F1} seconds");
                }
                
                currentResponse = request.Result;
                
                if (enableDebugLogging)
                {
                    Debug.Log($"[HandGrabbingDetector] Response received, length: {(currentResponse != null ? currentResponse.Length : 0)} characters");
                    
                    // Log a snippet of the response for debugging
                    if (currentResponse != null && currentResponse.Length > 0)
                    {
                        string snippet = currentResponse.Length > 100 ? 
                            currentResponse.Substring(0, 100) + "..." : 
                            currentResponse;
                        Debug.Log($"[HandGrabbingDetector] Response snippet: {snippet}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[HandGrabbingDetector] Exception getting API result: {ex}");
                
                // Log the full exception details for debugging
                Debug.LogException(ex);
                
                currentResponse = null;
            }
        }
    }
    
    private void CheckNetworkConnectivity()
    {
        // Check if we can reach the Gemini API endpoint
        try
        {
            System.Net.NetworkInformation.Ping ping = new System.Net.NetworkInformation.Ping();
            System.Net.NetworkInformation.PingReply reply = ping.Send("generativelanguage.googleapis.com", 1000);
            
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                Debug.Log($"[HandGrabbingDetector] Network connectivity check: Success (ping time: {reply.RoundtripTime}ms)");
            }
            else
            {
                Debug.LogWarning($"[HandGrabbingDetector] Network connectivity check: Failed (status: {reply.Status})");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[HandGrabbingDetector] Network connectivity check failed: {ex.Message}");
        }
    }
    
    private string currentResponse = null;
    
    private void ProcessDetectionResult(int requestId)
    {
        try
        {
            // Update the latest completed request ID
            latestCompletedRequestId = requestId;
            
            // Parse response
            GrabbingInfo grabbingInfo = null;
            if (currentResponse != null)
            {
                grabbingInfo = ParseGrabbingResponse(currentResponse);
            }
            
            // Update inspector and notify subscribers
            if (grabbingInfo != null)
            {
                HandleGrabbingDetection(grabbingInfo);
            }
            else
            {
                // Handle failed detection
                if (enableDebugLogging)
                {
                    Debug.LogWarning("[HandGrabbingDetector] Detection failed or timed out");
                }
                
                // Update UI to show detection failed
                isHandGrabbing = false;
                grabbedObjectName = "Detection failed";
                grabbingHand = "None";
                lastDetectionTime = System.DateTime.Now.ToString("HH:mm:ss");
            }
        }
        catch (System.Exception ex)
        {
            // Handle any unexpected exceptions
            Debug.LogError($"[HandGrabbingDetector] Exception during response processing: {ex}");
            
            // Update UI to show detection failed
            isHandGrabbing = false;
            grabbedObjectName = "Exception: " + ex.Message;
            grabbingHand = "None";
            lastDetectionTime = System.DateTime.Now.ToString("HH:mm:ss");
        }
        finally
        {
            // Always reset flags to allow new detection calls
            currentDetectionCoroutine = null;
            isDetectionInProgress = false;
        }
    }

    private void Start()
    {
        // Validate API key and model name
        ValidateGeminiSettings();
        
        // Enable debug logging in the GeminiAPI if needed
        if (geminiClient != null && enableDebugLogging)
        {
            geminiClient.EnableDebugLogging = true;
        }
        
        StartCoroutine(PeriodicDetectionRoutine());
        
        // Initialize known object labels
        UpdateKnownObjectLabels();
        
        // Auto-find hand references if not set
        if (leftHandObject == null || rightHandObject == null)
        {
            FindHandReferences();
        }
    }
    
    private void ValidateGeminiSettings()
    {
        // Check if API key looks valid
        if (string.IsNullOrEmpty(geminiApiKey) || geminiApiKey.Length < 20)
        {
            Debug.LogError("[HandGrabbingDetector] API key appears to be invalid or missing. Please check your Gemini API key.");
        }
        
        // Check if model name is valid
        if (string.IsNullOrEmpty(geminiModelName))
        {
            Debug.LogError("[HandGrabbingDetector] Model name is missing. Please specify a valid Gemini model name.");
        }
        else if (!geminiModelName.StartsWith("gemini-"))
        {
            Debug.LogWarning("[HandGrabbingDetector] Model name doesn't start with 'gemini-'. Make sure you're using a valid Gemini model name.");
        }
        
        // Log the settings
        if (enableDebugLogging)
        {
            Debug.Log($"[HandGrabbingDetector] Using Gemini model: {geminiModelName}");
            Debug.Log($"[HandGrabbingDetector] API key: {geminiApiKey.Substring(0, 5)}...{geminiApiKey.Substring(geminiApiKey.Length - 5)}");
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
            if (isDetecting)
            {
                // Even if a detection is in progress, start a new one if it's been running too long
                if (isDetectionInProgress && Time.time - detectionStartTime > 5f)
                {
                    if (enableDebugLogging)
                    {
                        Debug.LogWarning("[HandGrabbingDetector] Previous detection is taking too long, starting a new one anyway");
                    }
                    
                    // Don't cancel the old one, just let it run in parallel
                    // But mark that we're not waiting for it anymore
                    isDetectionInProgress = false;
                }
                
                if (!isDetectionInProgress)
                {
                    // Cancel any existing detection that might be stuck
                    CancelExistingDetection();
                    
                    // Start a new detection
                    currentDetectionCoroutine = StartCoroutine(DetectHandGrabbingRoutine());
                }
            }
            
            yield return new WaitForSeconds(detectionPeriod);
        }
    }
    
    private void CancelExistingDetection()
    {
        if (currentDetectionCoroutine != null)
        {
            if (enableDebugLogging)
            {
                Debug.LogWarning("[HandGrabbingDetector] Cancelling potentially stuck detection process");
            }
            
            StopCoroutine(currentDetectionCoroutine);
            currentDetectionCoroutine = null;
            isDetectionInProgress = false;
        }
    }

    private void Update()
    {
        // Monitor for stuck detections
        MonitorDetectionHealth();
    }
    
    private void MonitorDetectionHealth()
    {
        // If a detection is in progress and has been running for too long, cancel it
        if (isDetectionInProgress && Time.time - detectionStartTime > maxDetectionDuration)
        {
            if (enableDebugLogging)
            {
                Debug.LogWarning($"[HandGrabbingDetector] Detection has been running for {Time.time - detectionStartTime:F1} seconds, which exceeds the maximum allowed time of {maxDetectionDuration} seconds. Cancelling.");
            }
            
            CancelExistingDetection();
            
            // Update UI to show detection failed
            isHandGrabbing = false;
            grabbedObjectName = "Detection timed out";
            grabbingHand = "None";
            lastDetectionTime = System.DateTime.Now.ToString("HH:mm:ss");
        }
    }

    private IEnumerator DetectHandGrabbingRoutine()
    {
        // Set flag to prevent overlapping detection calls
        isDetectionInProgress = true;
        detectionStartTime = Time.time;
        
        // Update known object labels before detection
        UpdateKnownObjectLabels();
        
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = null;
        
        try
        {
            frameTex = CaptureFrame(cameraRenderTex);
            
            if (frameTex == null)
            {
                if (enableDebugLogging)
                {
                    Debug.LogError("[HandGrabbingDetector] Failed to capture frame from camera render texture");
                }
                
                // Clean up and exit
                isDetectionInProgress = false;
                currentDetectionCoroutine = null;
                yield break;
            }
            
            // Always resize the texture to a smaller size for better performance
            // 256x256 is usually sufficient for hand detection
            frameTex = ResizeTexture(frameTex, 256, 256);
            
            if (enableDebugLogging)
            {
                Debug.Log("[HandGrabbingDetector] Resized texture to 256x256 to reduce API payload size");
            }
            
            // 2) Convert to base64
            string base64Image = ConvertTextureToBase64(frameTex);
            
            // Check if the base64 string is too large
            if (base64Image.Length > 500000) // 500KB
            {
                Debug.LogWarning($"[HandGrabbingDetector] Base64 image is very large ({base64Image.Length / 1024}KB), which may cause API timeouts");
            }
            
            // 3) Build context-aware prompt
            string contextPrompt = BuildContextAwarePrompt();
            
            // 4) Start a separate coroutine for the API call
            StartCoroutine(ProcessGeminiRequest(contextPrompt, base64Image));
        }
        catch (System.Exception ex)
        {
            // Handle any unexpected exceptions
            Debug.LogError($"[HandGrabbingDetector] Exception during detection setup: {ex}");
            
            // Reset flags
            isDetectionInProgress = false;
            currentDetectionCoroutine = null;
        }
        finally
        {
            // Clean up the texture
            if (frameTex != null)
            {
                Destroy(frameTex);
            }
        }
        
        yield break;
    }

    private bool IsTextureTooLarge(Texture2D texture)
    {
        // Check if the texture dimensions are too large
        // A good rule of thumb is to keep images under 1024x1024 for API calls
        return texture.width > 1024 || texture.height > 1024;
    }
    
    private Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
    {
        // Create a temporary RenderTexture
        RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight);
        
        // Blit the source texture to the temporary RenderTexture
        Graphics.Blit(source, rt);
        
        // Remember the active RenderTexture
        RenderTexture prev = RenderTexture.active;
        
        // Set the temporary RenderTexture as active
        RenderTexture.active = rt;
        
        // Create a new texture with the target dimensions
        Texture2D result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
        
        // Read the pixels from the active RenderTexture
        result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        
        // Apply the changes
        result.Apply();
        
        // Restore the previous active RenderTexture
        RenderTexture.active = prev;
        
        // Release the temporary RenderTexture
        RenderTexture.ReleaseTemporary(rt);
        
        return result;
    }

    /// <summary>
    /// Encode to JPG and convert to Base64 string.
    /// </summary>
    protected string ConvertTextureToBase64(Texture2D tex)
    {
        // Use JPG format instead of PNG for smaller payload size
        // Adjust quality as needed (0-100)
        byte[] bytes = tex.EncodeToJPG(75);
        
        if (enableDebugLogging)
        {
            Debug.Log($"[HandGrabbingDetector] Image compressed to {bytes.Length / 1024}KB (JPG format)");
        }
        
        return Convert.ToBase64String(bytes);
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
        // Even if a detection is in progress, start a new one
        if (isDetectionInProgress && Time.time - detectionStartTime > 3f)
        {
            if (enableDebugLogging)
            {
                Debug.LogWarning("[HandGrabbingDetector] Previous detection is taking too long, starting a new one anyway");
            }
            
            // Don't cancel the old one, just let it run in parallel
            // But mark that we're not waiting for it anymore
            isDetectionInProgress = false;
        }
        
        if (!isDetectionInProgress)
        {
            // Cancel any existing detection that might be stuck
            CancelExistingDetection();
            
            // Start a new detection
            currentDetectionCoroutine = StartCoroutine(DetectHandGrabbingRoutine());
        }
    }
    
    /// <summary>
    /// Reset the detection system if it gets stuck
    /// </summary>
    public void ResetDetection()
    {
        if (enableDebugLogging)
        {
            Debug.Log("[HandGrabbingDetector] Manually resetting detection system");
        }
        
        // Cancel any existing detection
        CancelExistingDetection();
        
        // Reset all state variables
        isDetectionInProgress = false;
        currentDetectionCoroutine = null;
        consecutiveGrabbingDetections = 0;
        consecutiveNonGrabbingDetections = 0;
        detectionCounts.Clear();
        mostLikelyObject = null;
        
        // Update UI
        isHandGrabbing = false;
        grabbedObjectName = "System reset";
        grabbingHand = "None";
        lastDetectionTime = System.DateTime.Now.ToString("HH:mm:ss");
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