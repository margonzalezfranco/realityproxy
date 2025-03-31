using System;
using UnityEngine.XR.Hands;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json; // Added for JSON parsing
using PolySpatial.Template; // Added for SceneObjectManager etc.
using System.Linq;

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

    [Tooltip("System prompt template to use in global mode. Use {0} for scene context, {1} for object list, {2} for transcribed text")]
    [TextArea(5, 15)]
    [SerializeField] private string environmentLevelPromptTemplate = @"You are analyzing a user's request within a scene.
Scene context: {0}
Detected objects: {1}
User request: {2}

Determine if the user is asking about RELATIONSHIPS between objects or wants to HIGHLIGHT specific objects:

For RELATIONSHIPS between objects (e.g., ""how are these objects related?"" or ""show connections between items""):
1. Identify objects in the scene that are related to each other based on the user's request.
2. Find meaningful relationships between objects in the detected list.
3. Output a JSON array of relationship objects with the following structure:
```json
{{
  ""type"": ""relationships"",
  ""data"": [
    {{
      ""Source Object"": ""name_of_source_object"",
      ""Target Object"": ""name_of_target_object"",
      ""Relation Label"": ""brief relationship description (5 words max)""
    }},
    {{
      ""Source Object"": ""name_of_another_source"",
      ""Target Object"": ""name_of_another_target"",
      ""Relation Label"": ""another relationship description""
    }}
  ]
}}
```

For HIGHLIGHT requests (e.g., ""show me all food items"" or ""highlight the kitchen tools""):
1. Identify which objects from the detected list match the user's criteria.
2. Output a JSON object with the following structure:
```json
{{
  ""type"": ""highlight"",
  ""data"": {{
    ""objects"": [
      ""object1"",
      ""object2"",
      ""object3""
    ],
    ""rationale"": ""Brief explanation of why these objects were selected (1-2 sentences)""
  }}
}}
```

If no relevant objects or relationships can be identified, return:
```json
{{
  ""type"": ""none"",
  ""message"": ""No relevant objects or relationships found""
}}
```

IMPORTANT: Only include objects that are in the detected objects list provided above.";

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

    [Header("Scene Dependencies")] // Added Header
    [Tooltip("Manager that tracks all recognized objects in the scene.")]
    [SerializeField] private SceneObjectManager sceneObjectManager;
    [Tooltip("Manager that draws lines between related items.")]
    [SerializeField] private RelationshipLineManager relationshipLineManager;
    [Tooltip("Manager that provides scene context.")]
    [SerializeField] private SceneContextManager sceneContextManager;

    [Header("Debug")]
    [SerializeField] private AudioClip recordedAudio;
    [SerializeField] private string transcriptionResult;
    [SerializeField] private bool isProcessing = false;
    [SerializeField] private string geminiResponse;

    private SpeechToTextGeneral speechToText;
    private float recordingStartTime;
    private bool wasRecordingLastFrame = false;
    private XRHandSubsystem handSubsystem; // Cache the hand subsystem

    public bool objectLevelRecordingToggle = true;
    private string currentObjectLabel = null;
    private GameObject originalRecorderParent = null;

    protected override void Awake()
    {
        base.Awake(); // Call GeminiGeneral's Awake to initialize the Gemini client
        speechToText = GetComponent<SpeechToTextGeneral>();

        // Find dependencies if not assigned
        if (sceneObjectManager == null) sceneObjectManager = FindFirstObjectByType<SceneObjectManager>();
        if (relationshipLineManager == null) relationshipLineManager = FindFirstObjectByType<RelationshipLineManager>();
        if (sceneContextManager == null) sceneContextManager = FindFirstObjectByType<SceneContextManager>();

        if (sceneObjectManager == null) Debug.LogError("SceneObjectManager not found!");
        if (relationshipLineManager == null) Debug.LogError("RelationshipLineManager not found!");
        if (sceneContextManager == null) Debug.LogError("SceneContextManager not found!");

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

    public void TalkToGemini(string userQuery)
    {
        string finalPrompt;
        bool isObjectMode = !string.IsNullOrEmpty(currentObjectLabel);

        // If objectLevelRecordingToggle is true and we have a current object label,
        // use the object context prompt template
        if (isObjectMode)
        {
            finalPrompt = string.Format(objectContextPromptTemplate, currentObjectLabel, userQuery);
            Debug.Log($"[SpeechRecorder] Using OBJECT context prompt with label '{currentObjectLabel}'");
        }
        else // Global Mode
        {
             // --- Prepare context for Global Mode Prompt ---
            string currentSceneContext = "unknown environment";
            List<string> itemLabels = new List<string>();
            string objectListString = "none";

            // Get Scene Context
            if (sceneContextManager != null && sceneContextManager.GetCurrentAnalysis() != null)
            {
                var analysis = sceneContextManager.GetCurrentAnalysis();
                currentSceneContext = analysis.sceneType ?? "unknown environment";
                // Could potentially add task context here too if needed later
            }

            // Get Object List
            if (sceneObjectManager != null)
            {
                var anchors = sceneObjectManager.GetAllAnchors();
                foreach (var a in anchors)
                {
                    itemLabels.Add(a.label);
                }
                if (itemLabels.Count > 0)
                {
                    objectListString = string.Join(", ", itemLabels);
                }
            }
             // --- Format the Global Prompt ---
            finalPrompt = string.Format(environmentLevelPromptTemplate, currentSceneContext, objectListString, userQuery);
            Debug.Log($"[SpeechRecorder] Using ENVIRONMENT context prompt.");
            Debug.Log($"[SpeechRecorder] Scene: {currentSceneContext}, Objects: {objectListString}");
        }

        Debug.Log($"[SpeechRecorder] Final prompt: {finalPrompt}");

        // Use MakeGeminiRequest for concurrency management inherited from GeminiGeneral
        var request = MakeGeminiRequest(finalPrompt, null); // Assuming no image needed for these prompts

        // Start the coroutine to wait for the response and handle it
        StartCoroutine(GeminiQueryRoutine(request, isObjectMode));
    }

    private IEnumerator GeminiQueryRoutine(RequestStatus requestStatus, bool isObjectMode)
    {
        Debug.Log("[SpeechRecorder] Waiting for Gemini response...");
        // Wait until the transcription task is completed
        while (!requestStatus.IsCompleted)
        {
            yield return null;
        }

        // Check if there was an error
        if (requestStatus.Error != null)
        {
            Debug.LogError($"Gemini API error: {requestStatus.Error.Message}");
            geminiResponse = "Error communicating with Gemini.";
        }
        else
        {
            string rawResponse = requestStatus.Result;
            geminiResponse = ParseGeminiRawResponse(rawResponse);
            Debug.Log($"Gemini response: {geminiResponse}");
            
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
            
            // 7) Parse the response for relationships or highlights if it's not in object mode
            if (!isObjectMode && sceneObjectManager != null && relationshipLineManager != null)
            {
                try
                {
                    // Clear any existing relationship lines first
                    relationshipLineManager.ClearAllLines();
                    
                    // Try to extract JSON from the response
                    string jsonContent = TryExtractJson(geminiResponse);
                    Debug.Log($"[SpeechRecorder] Extracted JSON: {jsonContent}");
                    
                    if (!string.IsNullOrEmpty(jsonContent))
                    {
                        try 
                        {
                            // First, try to parse as a ResponseWrapper to determine the type
                            ResponseWrapper responseWrapper = null;
                            try
                            {
                                responseWrapper = JsonConvert.DeserializeObject<ResponseWrapper>(jsonContent);
                            }
                            catch (JsonException)
                            {
                                Debug.LogWarning("[SpeechRecorder] Failed to parse as ResponseWrapper, trying legacy formats");
                            }
                            
                            if (responseWrapper != null && !string.IsNullOrEmpty(responseWrapper.type))
                            {
                                // Process according to the response type
                                switch (responseWrapper.type.ToLower())
                                {
                                    case "relationships":
                                        // Convert the data object to a JSON string first, then deserialize to the proper type
                                        string relationshipsJson = JsonConvert.SerializeObject(responseWrapper.data);
                                        List<RelationshipInfo> relationships = JsonConvert.DeserializeObject<List<RelationshipInfo>>(relationshipsJson);
                                        ProcessRelationships(relationships, sceneObjectManager.GetAllAnchors());
                                        break;
                                        
                                    case "highlight":
                                        // Convert the data object to a JSON string first, then deserialize to the proper type
                                        string highlightJson = JsonConvert.SerializeObject(responseWrapper.data);
                                        HighlightData highlightData = JsonConvert.DeserializeObject<HighlightData>(highlightJson);
                                        ProcessHighlights(highlightData, sceneObjectManager.GetAllAnchors());
                                        break;
                                        
                                    case "none":
                                        Debug.Log($"[SpeechRecorder] No relevant objects or relationships found: {responseWrapper.message}");
                                        break;
                                        
                                    default:
                                        Debug.LogWarning($"[SpeechRecorder] Unknown response type: {responseWrapper.type}");
                                        break;
                                }
                            }
                            // Fall back to legacy formats if needed
                            else if (jsonContent.Contains("Source Object") || jsonContent.Contains("Target Object"))
                            {
                                // Try to parse as relationships array directly
                                try
                                {
                                    if (jsonContent.Trim().StartsWith("["))
                                    {
                                        List<RelationshipInfo> relationships = JsonConvert.DeserializeObject<List<RelationshipInfo>>(jsonContent);
                                        ProcessRelationships(relationships, sceneObjectManager.GetAllAnchors());
                                    }
                                    else if (jsonContent.Trim().StartsWith("{"))
                                    {
                                        var singleRelationship = JsonConvert.DeserializeObject<RelationshipInfo>(jsonContent);
                                        if (singleRelationship != null)
                                        {
                                            ProcessRelationships(new List<RelationshipInfo> { singleRelationship }, sceneObjectManager.GetAllAnchors());
                                        }
                                    }
                                }
                                catch (JsonException je)
                                {
                                    Debug.LogWarning($"[SpeechRecorder] Failed to parse as relationships: {je.Message}");
                                }
                            }
                            // Check if it could be highlight format
                            else if (jsonContent.Contains("objects") && jsonContent.Contains("rationale"))
                            {
                                try
                                {
                                    HighlightData highlightData = JsonConvert.DeserializeObject<HighlightData>(jsonContent);
                                    ProcessHighlights(highlightData, sceneObjectManager.GetAllAnchors());
                                }
                                catch (JsonException je)
                                {
                                    Debug.LogWarning($"[SpeechRecorder] Failed to parse as highlight data: {je.Message}");
                                }
                            }
                            // Legacy dictionary format
                            else
                            {
                                try 
                                {
                                    Dictionary<string, string> oldFormatDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonContent);
                                    if (oldFormatDict != null && oldFormatDict.Count > 0)
                                    {
                                        Debug.Log("[SpeechRecorder] Found old format dictionary, converting to new format");
                                        List<RelationshipInfo> relationships;
                                        ConvertOldFormatToNewFormat(oldFormatDict, out relationships);
                                        ProcessRelationships(relationships, sceneObjectManager.GetAllAnchors());
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogWarning($"[SpeechRecorder] Not in old format either: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[SpeechRecorder] Error processing JSON response: {ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.Log("[SpeechRecorder] No valid JSON found in response");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SpeechRecorder] Error processing response: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Tries to extract a JSON object/array from a text string that might contain
    /// JSON within code blocks marked by ```json or similar formats.
    /// </summary>
    private string TryExtractJson(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        // Case 1: Check for ```json blocks first
        if (text.Contains("```json"))
        {
            var splitted = text.Split(new[] { "```json" }, StringSplitOptions.None);
            if (splitted.Length > 1)
            {
                var splitted2 = splitted[1].Split(new[] { "```" }, StringSplitOptions.None);
                return splitted2[0].Trim();
            }
        }
        
        // Case 2: Check for just ``` blocks (without explicit json)
        if (text.Contains("```"))
        {
            var splitted = text.Split(new[] { "```" }, StringSplitOptions.None);
            if (splitted.Length > 1)
            {
                // Get the content of the first code block
                var codeBlock = splitted[1].Trim();
                
                // Only return it if it starts with { or [ (likely JSON)
                if ((codeBlock.StartsWith("{") && codeBlock.EndsWith("}")) || 
                    (codeBlock.StartsWith("[") && codeBlock.EndsWith("]")))
                {
                    return codeBlock;
                }
            }
        }

        // Case 3: Check for JSON array first since we're now expecting an array format
        int openBracket = text.IndexOf('[');
        int closeBracket = text.LastIndexOf(']');
        
        if (openBracket >= 0 && closeBracket > openBracket)
        {
            string jsonSubstring = text.Substring(openBracket, closeBracket - openBracket + 1);
            // Validate this is actually JSON
            try
            {
                JsonConvert.DeserializeObject(jsonSubstring);
                return jsonSubstring; // Only return if it's valid JSON
            }
            catch
            {
                // Not valid JSON, ignore
                Debug.LogWarning($"Found array-like structure but failed to parse as JSON: {jsonSubstring}");
            }
        }

        // Case 4: Try to find a JSON object directly in the text
        int openBrace = text.IndexOf('{');
        int closeBrace = text.LastIndexOf('}');
        
        if (openBrace >= 0 && closeBrace > openBrace)
        {
            string jsonSubstring = text.Substring(openBrace, closeBrace - openBrace + 1);
            // Validate this is actually JSON by trying to parse it
            try
            {
                JsonConvert.DeserializeObject(jsonSubstring);
                return jsonSubstring; // Only return if it's valid JSON
            }
            catch
            {
                // Not valid JSON, ignore
                Debug.LogWarning($"Found object-like structure but failed to parse as JSON: {jsonSubstring}");
            }
        }

        // No valid JSON found
        return null;
    }

    // Define a class to match the new JSON structure
    [System.Serializable]
    private class RelationshipInfo
    {
        [JsonProperty("Source Object")]
        public string SourceObject;
        
        [JsonProperty("Target Object")]
        public string TargetObject;
        
        [JsonProperty("Relation Label")]
        public string RelationLabel;
    }
    
    // Helper method to convert from old format to new format
    private void ConvertOldFormatToNewFormat(Dictionary<string, string> oldFormat, out List<RelationshipInfo> newFormat)
    {
        newFormat = new List<RelationshipInfo>();
        
        // Determine a source object (first key or try to find a primary object)
        string sourceObject = oldFormat.Keys.FirstOrDefault();
        
        // For each key-value pair, create a relationship
        foreach (var kvp in oldFormat)
        {
            if (kvp.Key != sourceObject) // Avoid self-relationships
            {
                newFormat.Add(new RelationshipInfo 
                { 
                    SourceObject = sourceObject,
                    TargetObject = kvp.Key,
                    RelationLabel = kvp.Value
                });
            }
        }
        
        Debug.Log($"[SpeechRecorder] Converted {oldFormat.Count} old format items to {newFormat.Count} new format relationships");
    }

    // New method to process relationships data
    private void ProcessRelationships(List<RelationshipInfo> relationships, List<SceneObjectAnchor> allAnchors)
    {
        if (relationships == null || relationships.Count == 0)
        {
            Debug.Log("[SpeechRecorder] No relationships to process");
            return;
        }
        
        Debug.Log($"[SpeechRecorder] Processing {relationships.Count} relationships");
        
        // Log details of each relationship for debugging
        for (int i = 0; i < relationships.Count; i++)
        {
            var rel = relationships[i];
            Debug.Log($"[SpeechRecorder] Relationship {i+1}: {rel.SourceObject} -> {rel.TargetObject}: '{rel.RelationLabel}'");
        }
        
        // Convert our internal RelationshipInfo objects to the RelationshipLineManager format
        List<RelationshipLineManager.RelationshipInfo> relationshipLineInfos = 
            relationships.Select(r => new RelationshipLineManager.RelationshipInfo
            {
                SourceObject = r.SourceObject,
                TargetObject = r.TargetObject,
                RelationLabel = r.RelationLabel
            }).ToList();
        
        // Call the bidirectional relationship visualization
        relationshipLineManager.ShowBidirectionalRelationships(relationshipLineInfos, allAnchors);
        Debug.Log($"[SpeechRecorder] Visualized {relationships.Count} bidirectional relationships");
    }

    // New method to process highlight data
    private void ProcessHighlights(HighlightData highlightData, List<SceneObjectAnchor> allAnchors)
    {
        if (highlightData == null || highlightData.objects == null || highlightData.objects.Count == 0)
        {
            Debug.Log("[SpeechRecorder] No objects to highlight");
            return;
        }
        
        Debug.Log($"[SpeechRecorder] Highlighting {highlightData.objects.Count} objects: {string.Join(", ", highlightData.objects)}");
        Debug.Log($"[SpeechRecorder] Rationale: {highlightData.rationale}");
        
        // Default highlight color (bright green)
        Color highlightColor = new Color(0.2f, 0.9f, 0.3f, 1.0f);
        
        // Find and highlight each object
        int highlightedCount = 0;
        foreach (string objectName in highlightData.objects)
        {
            SceneObjectAnchor anchor = FindBestMatchingAnchor(objectName, allAnchors);
            
            if (anchor != null && anchor.sphereObj != null)
            {
                var renderer = anchor.sphereObj.GetComponent<Renderer>();
                if (renderer != null && renderer.material != null)
                {
                    renderer.material.color = highlightColor;
                    highlightedCount++;
                    Debug.Log($"[SpeechRecorder] Highlighted object: {anchor.label}");
                }
            }
            else
            {
                Debug.LogWarning($"[SpeechRecorder] Could not find object to highlight: {objectName}");
            }
        }
        
        // Update response text with the rationale if objects were highlighted
        if (highlightedCount > 0 && responseText != null)
        {
            string highlightMessage = $"Highlighted {highlightedCount} objects: {highlightData.rationale}";
            responseText.text = highlightMessage;
        }
    }

    // Helper method to find best matching anchor for a given label
    private SceneObjectAnchor FindBestMatchingAnchor(string label, List<SceneObjectAnchor> anchors)
    {
        // Step 1: Try exact match (case-insensitive)
        var exactMatch = anchors.Find(a => string.Equals(a.label, label, System.StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
        {
            return exactMatch;
        }
        
        // Step 2: Try contains match
        var containsMatch = anchors.Find(a => 
            a.label.IndexOf(label, System.StringComparison.OrdinalIgnoreCase) >= 0 || 
            label.IndexOf(a.label, System.StringComparison.OrdinalIgnoreCase) >= 0);
            
        if (containsMatch != null)
        {
            return containsMatch;
        }
        
        // Step 3: Try word-by-word match for multi-word labels
        string[] words = label.Split(' ', '-', '_');
        if (words.Length > 1)
        {
            foreach (var word in words)
            {
                if (word.Length < 3) continue; // Skip short words
                
                var wordMatch = anchors.Find(a => 
                    a.label.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    
                if (wordMatch != null)
                {
                    return wordMatch;
                }
            }
        }
        
        return null;
    }

    // Additional classes to support the new response format
    [System.Serializable]
    private class ResponseWrapper
    {
        public string type;
        public object data;
        public string message;
    }

    [System.Serializable]
    private class HighlightData
    {
        public List<string> objects;
        public string rationale;
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