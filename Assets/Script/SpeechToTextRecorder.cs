using System;
using UnityEngine.XR.Hands;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

[RequireComponent(typeof(SpeechToTextGeneral))]
public class SpeechToTextRecorder : GeminiGeneral
{
    [Header("Recording Settings")]
    [SerializeField] private bool isRecording = false;
    [SerializeField] private int recordingFrequency = 16000;
    [SerializeField] private int maxRecordingLength = 100; // in seconds
    [SerializeField] private string deviceName = null; // null = default microphone

    [Header("Prompt Settings")]
    [Tooltip("System prompt template to use. Use {0} where the transcribed text should be inserted")]
    [TextArea(3, 10)]
    [SerializeField] private string systemPromptTemplate = "You are a helpful AI assistant responding to the user's request. You can see what the user is currently looking at. Please respond concisely and avoid using markdown formatting. User request: {0}";

    [Tooltip("System prompt template to use with object context. Use {0} for object name and {1} for transcribed text")]
    [TextArea(3, 10)]
    [SerializeField] private string objectContextPromptTemplate = "You are a helpful AI assistant responding to the user's request about {0}. You can see what the user is currently looking at, but you don't have to only rely on that. You don't have to wait to see the object to respond. Only respond information about this object. Please respond concisely and avoid using markdown formatting. User request: {1}";

    [Tooltip("Template for environment-level relationships. Use {0} for transcribed text")]
    [TextArea(3, 10)]
    [SerializeField] private string environmentLevelPromptTemplate = @"
Given the current scene, analyze the relationships between objects in the environment.
Find objects that are most related to each other, considering:
1. The overall scene context
2. Spatial relationships
3. Functional relationships
4. Common usage patterns

Return a JSON object where each key is an object name and its value is a dictionary of related objects and relationship descriptions.
Example format:
{{
  ""object1"": {{
    ""object2"": ""used together for cooking"",
    ""object3"": ""located next to item""
  }},
  ""object4"": {{
    ""object5"": ""complementary function""
  }}
}}

If you don't find any meaningful relationships in the current scene, return an empty JSON object.
User query: {0}";

    [TextArea(3, 10)]
    [SerializeField] private string detailLevelPromptTemplate = "";

    [Header("Gesture Control")]
    [SerializeField] private bool useMiddlePinchControl = true;
    [Tooltip("Which hand to use for middle finger pinch control")]
    [SerializeField] private bool useLeftHand = true;

    [Header("Gemini Response")]
    [SerializeField] private GameObject geminiHandSphere;
    [SerializeField] private TMPro.TextMeshPro requestText;
    [SerializeField] private TMPro.TextMeshPro responseText;
    [SerializeField] private TMPro.TextMeshPro responseTextOnObject;
    [SerializeField] private UnityEngine.Events.UnityEvent<string> onGeminiResponseReceived;
    [SerializeField] private GameObject chatbox;
    [SerializeField] private GameObject chatboxOnObject;
    [Header("Debug")]
    [SerializeField] private AudioClip recordedAudio;
    [SerializeField] private string transcriptionResult;
    [SerializeField] private bool isProcessing = false;
    [SerializeField] private string geminiResponse;

    [Header("Relationship Visualization")]
    [SerializeField] private RelationshipLineManager relationLineManager;
    [SerializeField] private SceneObjectManager sceneObjManager;
    [SerializeField] private SceneContextManager sceneContextManager;

    private SpeechToTextGeneral speechToText;
    private float recordingStartTime;
    private bool wasRecordingLastFrame = false;
    private XRHandSubsystem handSubsystem; // Cache the hand subsystem

    public bool objectLevelRecordingToggle = true;
    private string currentObjectLabel = null;
    private GameObject originalRecorderParent = null;
    private bool isRelationshipMode = false;
    
    // Public accessor for relationship mode - now determined by parent relationship
    public bool IsInRelationshipMode => originalRecorderParent != null && transform.parent == null;

    protected override void Awake()
    {
        base.Awake(); // Call GeminiGeneral's Awake to initialize the Gemini client
        speechToText = GetComponent<SpeechToTextGeneral>();
        
        // Get the hand subsystem once at startup
        var handSubsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(handSubsystems);
        if (handSubsystems.Count > 0)
        {
            handSubsystem = handSubsystems[0];
            Debug.Log("Hand tracking subsystem found and cached for SpeechToTextRecorder");
        }
        else
        {
            Debug.LogWarning("No hand tracking subsystem found for SpeechToTextRecorder");
        }
        
        // Find references if not assigned
        if (relationLineManager == null)
        {
            relationLineManager = FindFirstObjectByType<RelationshipLineManager>();
        }
        
        if (sceneObjManager == null)
        {
            sceneObjManager = FindFirstObjectByType<SceneObjectManager>();
        }
        
        if (sceneContextManager == null)
        {
            sceneContextManager = FindFirstObjectByType<SceneContextManager>();
        }
    }

    private void OnEnable()
    {
        // Subscribe to middle finger pinch events
        if (useMiddlePinchControl)
        {
            MyHandTracking.OnMiddlePinchStarted += HandleMiddlePinchStarted;
            MyHandTracking.OnMiddlePinchEnded += HandleMiddlePinchEnded;
            Debug.Log("Subscribed to middle finger pinch events for speech recording");
        }
    }

    private void OnDisable()
    {
        // Unsubscribe from middle finger pinch events
        if (useMiddlePinchControl)
        {
            MyHandTracking.OnMiddlePinchStarted -= HandleMiddlePinchStarted;
            MyHandTracking.OnMiddlePinchEnded -= HandleMiddlePinchEnded;
        }
    }

    private void HandleMiddlePinchStarted(bool isLeft)
    {
        // Only respond to the configured hand's gestures
        if (isLeft == useLeftHand)
        {
            Debug.Log($"Middle finger pinch started on {(isLeft ? "left" : "right")} hand - starting recording");
            if (!isRecording)
            {
                ToggleRecording();
                UpdateGeminiHandSpherePositionToMiddleFingerTip();
            }
        }
    }

    private void HandleMiddlePinchEnded(bool isLeft)
    {
        // Only respond to the configured hand's gestures
        if (isLeft == useLeftHand)
        {
            Debug.Log($"Middle finger pinch ended on {(isLeft ? "left" : "right")} hand - stopping recording");
            if (isRecording)
            {
                ToggleRecording();
                UpdateGeminiHandSphereForRecordEnd();
            }
        }
    }

    private void Update()
    {
        // Check if recording state changed
        if (isRecording != wasRecordingLastFrame)
        {
            if (isRecording)
            {
                StartRecording();
            }
            else
            {
                StopRecordingAndTranscribe();
            }
            wasRecordingLastFrame = isRecording;
        }

        // Continuously update the Gemini hand sphere position while recording
        if (isRecording && geminiHandSphere != null)
        {
            UpdateGeminiHandSpherePositionToMiddleFingerTip();
        }

        // Update recording time in inspector for visibility
        if (isRecording)
        {
            float recordingTime = Time.time - recordingStartTime;
            if (recordingTime >= maxRecordingLength)
            {
                isRecording = false;
                Debug.Log("Max recording length reached, stopping automatically.");
            }
        }
    }

    private void UpdateGeminiHandSpherePositionToMiddleFingerTip()
    {
        if (geminiHandSphere != null)
        {
            // Only clear text and set color when first starting recording
            if (!wasRecordingLastFrame && isRecording)
            {
                // clear the request text and the response text
                requestText.text = "";
                responseText.text = "";
                chatbox.SetActive(false);
                Material material = geminiHandSphere.GetComponent<Renderer>().material;
                material.color = new Color(material.color.r, material.color.g, material.color.b, 1.0f);
            }
            
            // Directly use the cached hand subsystem
            if (handSubsystem != null && handSubsystem.running)
            {
                // Get the appropriate hand based on the useLeftHand setting
                var hand = useLeftHand ? handSubsystem.leftHand : handSubsystem.rightHand;
                
                // Check if the hand is tracked
                if (hand.isTracked)
                {
                    // Try to get the middle fingertip position
                    if (hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out Pose middleTipPose))
                    {
                        // Update the position of the gemini hand sphere to the position of the middle finger tip
                        geminiHandSphere.transform.position = middleTipPose.position;
                    }
                }
            }
            else
            {
                Debug.LogWarning("Hand subsystem not available or not running");
            }
        }
    }

    private void UpdateGeminiHandSphereForRecordEnd()
    {
        if (geminiHandSphere != null)
        {
            // get the material of the geminiHandSphere and set it to 50% transparent
            Material material = geminiHandSphere.GetComponent<Renderer>().material;
            material.color = new Color(material.color.r, material.color.g, material.color.b, 0.2f);
            chatbox.SetActive(true);
        }
    }

    [ContextMenu("Toggle Recording")]
    public void ToggleRecording()
    {
        isRecording = !isRecording;
        Debug.Log($"Recording toggled to: {isRecording}");
        
        // Remove relationship mode toggling from here
    }

    // Method to be called from SphereToggleScript to set the current object label
    public void SetObjectLabel(string label, GameObject sphereToggle)
    {
        currentObjectLabel = label;
        
        // Save original parent of recorder toggle if this is the first time setting
        if (originalRecorderParent == null && transform.parent != null)
        {
            originalRecorderParent = transform.parent.gameObject;
        }
        
        // Set parent to the sphere toggle
        if (sphereToggle != null)
        {
            transform.SetParent(sphereToggle.transform);
        }
        
        Debug.Log($"Recorder now associated with object: {label}");
    }

    // Method to reset object label and restore original parent
    public void ResetObjectLabel()
    {
        currentObjectLabel = null;
        
        // Restore original parent if it exists
        if (originalRecorderParent != null)
        {
            transform.SetParent(originalRecorderParent.transform);
        }
        else
        {
            transform.SetParent(null);
        }
        
        Debug.Log("Recorder object association cleared");
    }

    private void StartRecording()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("No microphone device found!");
            isRecording = false;
            return;
        }

        // If no specific device is selected, use the default
        if (string.IsNullOrEmpty(deviceName))
        {
            deviceName = Microphone.devices[0];
            Debug.Log($"Using default microphone: {deviceName}");
        }

        // Start recording with the appropriate sample rate
        recordedAudio = Microphone.Start(deviceName, false, maxRecordingLength, recordingFrequency);
        recordingStartTime = Time.time;
        transcriptionResult = "";
        if (responseTextOnObject != null) responseTextOnObject.text = "";
        if (chatboxOnObject != null) chatboxOnObject.SetActive(false);
        Debug.Log($"Started recording using {deviceName}");
    }

    private void StopRecordingAndTranscribe()
    {
        if (Microphone.IsRecording(deviceName))
        {
            // Get the position to know how long the recording was
            int position = Microphone.GetPosition(deviceName);
            Microphone.End(deviceName);
            Debug.Log($"Stopped recording. Length: {position} samples");

            if (position <= 0)
            {
                Debug.LogWarning("Recording was too short, nothing to transcribe.");
                return;
            }

            isProcessing = true;
            ProcessRecordingAndTranscribe(position);
        }
    }

    private void ProcessRecordingAndTranscribe(int position)
    {
        // Create a float array for the audio data
        float[] audioData = new float[position * recordedAudio.channels];
        recordedAudio.GetData(audioData, 0);

        // Convert float array to PCM byte array (16-bit)
        byte[] byteData = ConvertAudioDataToBytes(audioData);

        // Now send for transcription
        var requestStatus = speechToText.TranscribeAudio(byteData);

        // Wait for and handle the result
        StartCoroutine(WaitForTranscriptionResult(requestStatus));
    }

    private System.Collections.IEnumerator WaitForTranscriptionResult(SpeechToTextGeneral.RequestStatus requestStatus)
    {
        // Wait until the transcription task is completed
        while (!requestStatus.IsCompleted)
        {
            yield return null;
        }

        // Check if there was an error
        if (requestStatus.Error != null)
        {
            Debug.LogError($"Transcription error: {requestStatus.Error.Message}");
            transcriptionResult = "Error during transcription.";
        }
        else
        {
            string rawJson = requestStatus.Result;
            Debug.Log($"Raw transcription response: {rawJson}");

            // Parse the result to get just the transcript text
            string parsedResult = SpeechToTextGeneral.ParseTranscriptionResult(rawJson);
            
            if (!string.IsNullOrEmpty(parsedResult))
            {
                transcriptionResult = parsedResult;
                requestText.text = transcriptionResult;
                TalkToGemini(transcriptionResult);
            }
            else
            {
                transcriptionResult = "No text recognized.";
                Debug.LogWarning("No transcription found in response.");
            }
        }

        isProcessing = false;
    }

    private byte[] ConvertAudioDataToBytes(float[] audioData)
    {
        // Convert float array (-1.0f to 1.0f) to Int16 PCM (LINEAR16)
        byte[] byteData = new byte[audioData.Length * 2]; // 16-bit = 2 bytes per sample

        int byteIndex = 0;
        for (int i = 0; i < audioData.Length; i++)
        {
            // Convert to 16-bit PCM
            short pcmValue = (short)(audioData[i] * short.MaxValue);
            
            // Store as bytes (Little Endian)
            byteData[byteIndex++] = (byte)(pcmValue & 0xFF);
            byteData[byteIndex++] = (byte)((pcmValue >> 8) & 0xFF);
        }

        return byteData;
    }

    // For debugging: Save audio to WAV file
    [ContextMenu("Save Recording To File")]
    public void SaveRecordingToFile()
    {
        if (recordedAudio == null)
        {
            Debug.LogError("No recorded audio to save.");
            return;
        }

        string filePath = Path.Combine(Application.persistentDataPath, "recording.wav");
        SavWav.Save(filePath, recordedAudio);
        Debug.Log($"Saved recording to: {filePath}");
    }

    public void TalkToGemini(string prompt)
    {
        string formattedPrompt;
        
        // If we're attached to an object (has currentObjectLabel and parent is a sphere)
        if (!string.IsNullOrEmpty(currentObjectLabel) && transform.parent != null && 
            transform.parent.GetComponent<SphereToggleScript>() != null)
        {
            // Use the object context prompt template - this takes TWO parameters: label and prompt
            formattedPrompt = string.Format(objectContextPromptTemplate, currentObjectLabel, prompt);
            Debug.Log($"Using object context prompt with label '{currentObjectLabel}'");
        }
        else
        {
            // We're in global/environmental mode - get scene objects and context
            if (sceneObjManager != null) 
            {
                // Get all anchors in the scene
                var anchors = sceneObjManager.GetAllAnchors();
                List<string> itemLabels = new List<string>();
                foreach (var a in anchors)
                {
                    itemLabels.Add(a.label);
                }
                
                // Get scene context if available
                string sceneContext = "unknown environment";
                string taskContext = "no specific task";
                
                if (sceneContextManager != null && sceneContextManager.GetCurrentAnalysis() != null)
                {
                    var analysis = sceneContextManager.GetCurrentAnalysis();
                    sceneContext = analysis.sceneType ?? "unknown environment";
                    if (analysis.possibleTasks != null && analysis.possibleTasks.Count > 0)
                    {
                        taskContext = string.Join(", ", analysis.possibleTasks);
                    }
                }
                
                // Create a custom prompt with scene context and objects
                formattedPrompt = $@"
Given this scene context: {sceneContext},
the potential tasks: {taskContext},

Find objects that are most related to each other in the current scene, considering:
1. The overall scene context and task
2. Spatial relationships
3. Functional relationships
4. Common usage patterns

Choose only from these detected items: {string.Join(", ", itemLabels)}.

Return a JSON object where each key is an object name and its value is a dictionary of related objects and relationship descriptions (max 5 words).
Example format:
{{
  ""object1"": {{
    ""object2"": ""used together for cooking"",
    ""object3"": ""located next to item""
  }},
  ""object4"": {{
    ""object5"": ""complementary function""
  }}
}}

If you don't find any meaningful relationships, return an empty JSON object.
User query: {prompt}";

                Debug.Log("Using environment level prompt with scene objects");
            }
            else
            {
                // Fallback to basic environment prompt if no scene manager
                formattedPrompt = string.Format(environmentLevelPromptTemplate, prompt);
                Debug.Log("Using basic environment level prompt (no scene objects available)");
            }
        }
        
        Debug.Log($"Formatted prompt: {formattedPrompt}");
        
        StartCoroutine(GeminiQueryRoutine(formattedPrompt));
    }

    private IEnumerator GeminiQueryRoutine(string prompt)
    {
        Debug.Log($"Sending prompt to Gemini: {prompt}");
        
        // 1) Capture frame from RenderTexture (if available)
        string base64Image = null;
        if (cameraRenderTex != null)
        {
            Texture2D frameTex = CaptureFrame(cameraRenderTex);
            base64Image = ConvertTextureToBase64(frameTex);
            Destroy(frameTex); // Clean up texture
        }
        
        // 2) Make the request to Gemini
        var request = MakeGeminiRequest(prompt, base64Image);
        
        // 3) Wait for the request to complete
        while (!request.IsCompleted)
        {
            yield return null;
        }
        
        // 4) Process the response
        if (request.Error != null)
        {
            Debug.LogError($"Gemini API error: {request.Error.Message}");
            geminiResponse = "Error communicating with Gemini.";
        }
        else
        {
            string rawResponse = request.Result;
            geminiResponse = ParseGeminiRawResponse(rawResponse);
            Debug.Log($"Gemini response: {geminiResponse}");
            
            // Process for relationship visualization if not in object context mode
            // and the necessary components are available
            if (relationLineManager != null && sceneObjManager != null)
            {
                // If we're in environment mode (not attached to an object) OR 
                // if we're forced to check for relationship responses
                if (IsInRelationshipMode || (!string.IsNullOrEmpty(currentObjectLabel) && 
                    transform.parent == null))
                {
                    // Process as relationships
                    ProcessEnvironmentLevelResponse(rawResponse);
                }
            }
            
            // 5) Update UI if available
            if (responseText != null && !string.IsNullOrEmpty(geminiResponse))
            {
                responseText.text = geminiResponse;
            }

            // 5) Update UI if available
            if (responseTextOnObject != null && !string.IsNullOrEmpty(geminiResponse))
            {
                responseTextOnObject.text = geminiResponse;
            }

            // 6) Update UI if available
            if (chatboxOnObject != null && !string.IsNullOrEmpty(geminiResponse))
            {
                chatboxOnObject.SetActive(true);
            }
            
            // 6) Invoke event with the response
            onGeminiResponseReceived?.Invoke(geminiResponse);
        }
    }

    // Process environment-level response and visualize relationships
    private void ProcessEnvironmentLevelResponse(string rawResponse)
    {
        if (relationLineManager == null || sceneObjManager == null)
        {
            Debug.LogWarning("Cannot process environment response: Missing RelationshipLineManager or SceneObjectManager");
            return;
        }
        
        try
        {
            // First extract any JSON from the response
            string jsonStr = TryExtractJson(rawResponse);
            if (string.IsNullOrEmpty(jsonStr))
            {
                Debug.LogWarning("No valid JSON found in environment-level response");
                return;
            }
            
            Debug.Log($"Extracted JSON: {jsonStr}");
            
            // Parse the JSON to get relationships
            Dictionary<string, Dictionary<string, string>> relationshipsMap = null;
            try 
            {
                relationshipsMap = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(jsonStr);
                Debug.Log("Successfully parsed JSON into relationship map");
            }
            catch (Exception jsonEx)
            {
                Debug.LogError($"Error parsing JSON as relationship map: {jsonEx.Message}");
                // Try to debug the structure of the JSON
                try
                {
                    var obj = JsonConvert.DeserializeObject(jsonStr);
                    Debug.Log($"JSON structure type: {obj.GetType().Name}");
                    return;
                }
                catch
                {
                    Debug.LogError("Couldn't even parse as generic JSON object");
                    return;
                }
            }
                
            if (relationshipsMap == null || relationshipsMap.Count == 0)
            {
                Debug.Log("No relationships found in environment-level response");
                relationLineManager.ClearAllLines();
                return;
            }
            
            // Clear existing lines
            relationLineManager.ClearAllLines();
            
            // Get all anchors in the scene
            var allAnchors = sceneObjManager.GetAllAnchors();
            
            // Log all available anchors
            Debug.Log($"Available anchors in scene: {string.Join(", ", allAnchors.Select(a => a.label))}");
            
            // For each object in the map, find its anchor and draw relationships
            foreach (var objEntry in relationshipsMap)
            {
                string sourceLabel = objEntry.Key;
                var sourceAnchor = allAnchors.Find(a => a.label == sourceLabel);
                
                if (sourceAnchor == null)
                {
                    // Try case-insensitive search or partial match
                    sourceAnchor = allAnchors.Find(a => 
                        a.label.Equals(sourceLabel, StringComparison.OrdinalIgnoreCase) ||
                        sourceLabel.Equals(a.label, StringComparison.OrdinalIgnoreCase) ||
                        a.label.ToLowerInvariant().Contains(sourceLabel.ToLowerInvariant()) || 
                        sourceLabel.ToLowerInvariant().Contains(a.label.ToLowerInvariant()));
                    
                    if (sourceAnchor != null)
                    {
                        Debug.Log($"Found anchor '{sourceAnchor.label}' using fuzzy match for '{sourceLabel}'");
                    }
                    else
                    {
                        Debug.LogWarning($"No anchor found for '{sourceLabel}'. Skipping this relationship set.");
                        continue;
                    }
                }
                else
                {
                    Debug.Log($"Found exact match for anchor '{sourceLabel}'");
                }
                
                // Process each relationship for this source
                Dictionary<string, string> relationships = objEntry.Value;
                Dictionary<string, string> validRelationships = new Dictionary<string, string>();
                
                // For each relationship, check if target exists and add to valid relationships
                foreach (var rel in relationships)
                {
                    string targetLabel = rel.Key;
                    string relationDesc = rel.Value;
                    
                    // First try exact match
                    var targetAnchor = allAnchors.Find(a => a.label == targetLabel);
                    
                    // If no exact match, try fuzzy match
                    if (targetAnchor == null)
                    {
                        targetAnchor = allAnchors.Find(a => 
                            a.label.Equals(targetLabel, StringComparison.OrdinalIgnoreCase) ||
                            targetLabel.Equals(a.label, StringComparison.OrdinalIgnoreCase) ||
                            a.label.ToLowerInvariant().Contains(targetLabel.ToLowerInvariant()) || 
                            targetLabel.ToLowerInvariant().Contains(a.label.ToLowerInvariant()));
                        
                        if (targetAnchor != null)
                        {
                            Debug.Log($"Found target anchor '{targetAnchor.label}' using fuzzy match for '{targetLabel}'");
                            // Use the actual label in the scene instead of what came in the JSON
                            validRelationships[targetAnchor.label] = relationDesc;
                        }
                        else
                        {
                            Debug.LogWarning($"No target anchor found for '{targetLabel}'. Skipping this relationship.");
                        }
                    }
                    else
                    {
                        // Exact match found
                        validRelationships[targetLabel] = relationDesc;
                    }
                }
                
                // Only show relationships for valid targets
                if (validRelationships.Count > 0)
                {
                    relationLineManager.ShowRelationships(sourceAnchor, validRelationships, allAnchors);
                    Debug.Log($"Drew relationships for '{sourceLabel}': {string.Join(", ", validRelationships.Keys)}");
                }
                else
                {
                    Debug.LogWarning($"No valid targets found for '{sourceLabel}'. No relationships drawn.");
                }
            }
            
            Debug.Log($"Successfully visualized {relationshipsMap.Count} objects with relationships");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to process environment-level response: {e.Message}\n{e.StackTrace}");
        }
    }
    
    // Helper for extracting JSON from a text response
    private string TryExtractJson(string fullResponse)
    {
        try
        {
            // First check if the response contains Gemini API format
            if (fullResponse.Contains("\"candidates\"") && fullResponse.Contains("\"content\""))
            {
                try 
                {
                    // This might be the raw Gemini API response
                    var root = JsonConvert.DeserializeObject<GeminiRoot>(fullResponse);
                    if (root?.candidates != null && root.candidates.Count > 0)
                    {
                        string rawText = root.candidates[0].content.parts[0].text;
                        if (!string.IsNullOrEmpty(rawText))
                        {
                            fullResponse = rawText; // Replace with the actual content
                            Debug.Log("Extracted text content from Gemini API response");
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to parse as Gemini API response: {e.Message}");
                }
            }
            
            // Try to parse as is (might already be valid JSON)
            try
            {
                JsonConvert.DeserializeObject(fullResponse);
                Debug.Log("Input is already valid JSON");
                return fullResponse; // If no exception, it's valid JSON
            }
            catch
            {
                // Not direct JSON, continue extraction
            }
            
            // Try to extract JSON from markdown code blocks
            if (fullResponse.Contains("```json"))
            {
                var splitted = fullResponse.Split(new[] { "```json" }, StringSplitOptions.None);
                if (splitted.Length > 1)
                {
                    var splitted2 = splitted[1].Split(new[] { "```" }, StringSplitOptions.None);
                    Debug.Log("Extracted JSON from ```json code block");
                    return splitted2[0].Trim();
                }
            }
            else if (fullResponse.Contains("```"))
            {
                var splitted = fullResponse.Split(new[] { "```" }, StringSplitOptions.None);
                if (splitted.Length > 1)
                {
                    Debug.Log("Extracted JSON from ``` code block");
                    return splitted[1].Trim();
                }
            }
            
            // Try to find JSON between curly braces
            int openBrace = fullResponse.IndexOf('{');
            int closeBrace = fullResponse.LastIndexOf('}');
            
            if (openBrace >= 0 && closeBrace > openBrace)
            {
                Debug.Log("Extracted JSON between curly braces");
                return fullResponse.Substring(openBrace, closeBrace - openBrace + 1);
            }
            
            Debug.LogWarning("No JSON found in response");
            return null; // No JSON found
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error extracting JSON: {e.Message}");
            return null;
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
}

// Helper class to save AudioClip as WAV file
public static class SavWav
{
    public static bool Save(string filepath, AudioClip clip)
    {
        if (clip == null)
            return false;

        Debug.Log($"Saving AudioClip to WAV: {filepath}");

        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        using (FileStream fs = CreateEmpty(filepath))
        {
            ConvertAndWrite(fs, samples, clip.frequency, clip.channels);
            WriteHeader(fs, clip);
        }

        return true;
    }

    private static FileStream CreateEmpty(string filepath)
    {
        FileStream fileStream = new FileStream(filepath, FileMode.Create);
        byte emptyByte = new byte();

        for (int i = 0; i < 44; i++) // 44 = WAV header size
        {
            fileStream.WriteByte(emptyByte);
        }

        return fileStream;
    }

    private static void ConvertAndWrite(FileStream fileStream, float[] samples, int sampleRate, int channels)
    {
        using (BinaryWriter writer = new BinaryWriter(fileStream))
        {
            foreach (float sample in samples)
            {
                short shortSample = (short)(sample * 32768.0f);
                writer.Write(shortSample);
            }
        }
    }

    private static void WriteHeader(FileStream fileStream, AudioClip clip)
    {
        int hz = clip.frequency;
        int channels = clip.channels;
        int samples = clip.samples;

        fileStream.Seek(0, SeekOrigin.Begin);

        using (BinaryWriter writer = new BinaryWriter(fileStream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples * 2 * channels);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((ushort)1); // PCM format
            writer.Write((ushort)channels);
            writer.Write(hz);
            writer.Write(hz * channels * 2); // Byte rate
            writer.Write((ushort)(channels * 2)); // Block align
            writer.Write((ushort)16); // Bits per sample
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(samples * channels * 2);
        }
    }
} 