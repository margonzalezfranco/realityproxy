using System;
using UnityEngine.XR.Hands;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if !UNITY_VISIONOS
using Microsoft.CSharp; // Required for dynamic type binding
#endif
using Newtonsoft.Json; // Added for JSON parsing
using Newtonsoft.Json.Linq; // Added for JObject
using PolySpatial.Template; // Added for SceneObjectManager etc.
using System.Linq;
using UnityEngine.XR.Interaction.Toolkit.UI;
using System.Text;
using TMPro;

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
    [SerializeField] protected string systemPromptTemplate = "You are a helpful AI assistant responding to the user's request. You can see what the user is currently looking at. Please respond concisely and avoid using markdown formatting. User request: {0}";

    [Tooltip("System prompt template to use with object context. Use {0} for object name and {1} for transcribed text")]
    [TextArea(3, 10)]
    [SerializeField] protected string objectContextPromptTemplate = "You are a helpful AI assistant responding to the user's request about {0}. You can see what the user is currently looking at, but you don't have to only rely on that. You don't have to wait to see the object to respond. Only respond information about this object. Please respond concisely and avoid using markdown formatting. User request: {1}";

    [Tooltip("System prompt template to use when user is pointing at a specific part. Use {0} for object name, {1} for part name, {2} for part description, and {3} for transcribed text")]
    [TextArea(3, 10)]
    [SerializeField] protected string pointingPromptTemplate = "You are a helpful AI assistant responding to the user's request about a specific part of an object. The user is pointing at the '{1}' part of {0}. This part is described as: '{2}'. Please provide specific information about this part. Respond concisely and avoid using markdown formatting. User request: {3}";

    [Tooltip("System prompt template to use in global mode. Use {0} for scene context, {1} for object list, {2} for transcribed text")]
    [TextArea(5, 15)]
    [SerializeField] protected string environmentLevelPromptTemplate = @"You are analyzing a user's request within a scene.
Scene context: {0}
Detected objects: {1}
User request: {2}

Determine if the user is asking about RELATIONSHIPS between objects, wants to HIGHLIGHT specific objects, or needs INSTRUCTIONS for completing a task:

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
  ],
  ""message"": ""Found [number] relationships. [object1] is [brief relation] to [object2].""
}}
```

For HIGHLIGHT requests (e.g., ""show me all food items"" or ""highlight the kitchen tools""):
1. Identify which objects from the detected list match the user's criteria.
2. Directly answer the user's question, not just indicate that items were highlighted.
3. Output a JSON object with the following structure:
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
  }},
  ""message"": ""[Direct answer to user's question] These [objects] contain/are [key property]. [One additional relevant fact about the items].""
}}
```

Examples of good highlight messages:
- For ""What items contain gluten?"": ""Bread, pasta, and cake contain gluten. These items are made with wheat flour.""
- For ""Show me kitchen tools"": ""Spatula, whisk, and knife are kitchen tools. They're essential for basic food preparation.""
- For ""Which items are toxic?"": ""Bleach and ammonia are toxic. Never mix these chemicals as they produce dangerous fumes.""

For INSTRUCTION requests (e.g., ""how do I make a cake?"" or ""show me the steps to assemble this""):
1. Identify a sequence of objects from the detected list needed to complete the task.
2. Order them in the correct sequence for completing the task.
3. For each step, include a specific action and purpose (what to do and why).
4. Output a JSON object with the following structure:
```json
{{
  ""type"": ""instruction"",
  ""data"": [
    {{
      ""order"": 1,
      ""object"": ""first_object_name""
    }},
    {{
      ""order"": 2,
      ""object"": ""second_object_name""
    }},
    {{
      ""order"": 3,
      ""object"": ""third_object_name""
    }}
  ],
  ""message"": ""To [accomplish task]: 1. Use [first_object] to [specific action/purpose]. 2. Then [action with second_object]. 3. Finally, [action with third_object].""
}}
```

If no relevant objects or relationships can be identified, return:
```json
{{
  ""type"": ""none"",
  ""message"": ""No matching objects found. Try rephrasing.""
}}
```

IMPORTANT: 
1. Only include objects that are in the detected objects list provided above.
2. Keep messages focused and clear but include specific actions and purposes (20-25 words max).
3. For instructions, make each step useful with a practical action, not just ""use X"" - explain how/why.
4. Prioritize clarity and helpfulness in your instructions.
5. Avoid technical terms or JSON structure in messages.";

    [Header("Gesture Control")]
    [SerializeField] private bool useMiddlePinchControl = true;
    [Tooltip("Which hand to use for middle finger pinch control")]
    [SerializeField] private bool useLeftHand = true;

    [Header("Gemini Response")]
    [SerializeField] private TextMeshPro requestText;
    [SerializeField] private TextMeshPro responseTextOnObject;
    [SerializeField] private UnityEngine.Events.UnityEvent<string> onGeminiResponseReceived;
    [SerializeField] private GameObject chatboxOnObject;

    [Header("UI Integration")]
    [Tooltip("The parent menu canvas containing the InfoPanel and other UI elements")]
    public Transform infoPanelParent;
    [Tooltip("The transform that will hold the questions (typically the InfoPanel)")]
    public Transform infoPanel;
    [Tooltip("Prefab to use for question buttons")]
    public GameObject questionPrefab;
    [Tooltip("Answer panel to display responses to questions")]
    public GameObject answerPanel;
    [Tooltip("Reference to the GeminiQuestionAnswerer component")]
    public GeminiQuestionAnswerer questionAnswerer;
    
    // List to track created question objects
    private List<GameObject> createdQuestions = new List<GameObject>();

    [Header("Scene Dependencies")] // Added Header
    [Tooltip("Manager that tracks all recognized objects in the scene.")]
    [SerializeField] protected SceneObjectManager sceneObjectManager;
    [Tooltip("Manager that draws lines between related items.")]
    [SerializeField] protected RelationshipLineManager relationshipLineManager;
    [Tooltip("Manager that provides scene context.")]
    [SerializeField] protected SceneContextManager sceneContextManager;

    [Header("Debug")]
    [SerializeField] private AudioClip recordedAudio;
    [SerializeField] private string transcriptionResult;
    [SerializeField] private bool isProcessing = false;
    [SerializeField] private string geminiResponse;

    [Header("UI Display Settings")]
    [Tooltip("Time in seconds to automatically hide the response UI after displaying")]
    [SerializeField] private float responseDisplayDuration = 10f; // 10 seconds default timeout
    [Tooltip("Enable/disable auto-hiding of response UI")]
    [SerializeField] private bool enableResponseAutoHide = true;
    private Coroutine responseHideCoroutine;

    [Header("Step Indicators")]
    [Tooltip("Prefab to use for step number indicators in instructions")]
    [SerializeField] private GameObject stepIndicatorPrefab;

    [Header("Pointing Reference")]
    [Tooltip("Reference to the pointing plane used for part-specific interactions")]
    [SerializeField] protected GameObject pointingPlane;

    private SpeechToTextGeneral speechToText;
    private float recordingStartTime;
    private bool wasRecordingLastFrame = false;
    private XRHandSubsystem handSubsystem; // Cache the hand subsystem

    // Add variables to track pointing state
    protected string currentPointingPartName = null;
    protected string currentPointingPartDescription = null;

    public bool objectLevelRecordingToggle = true;
    protected string currentObjectLabel = null;
    private GameObject originalRecorderParent = null;

    [Header("User Study Logging")]
    [SerializeField] private bool enableUserStudyLogging = true;

    protected override void Awake()
    {
        base.Awake(); // Call GeminiGeneral's Awake to initialize the Gemini client
        speechToText = GetComponent<SpeechToTextGeneral>();

        // Find dependencies if not assigned
        if (sceneObjectManager == null) sceneObjectManager = FindFirstObjectByType<SceneObjectManager>();
        if (relationshipLineManager == null) relationshipLineManager = FindFirstObjectByType<RelationshipLineManager>();
        if (sceneContextManager == null) sceneContextManager = FindFirstObjectByType<SceneContextManager>();
        if (pointingPlane == null) pointingPlane = GameObject.Find("PointingPlane");

        if (sceneObjectManager == null) Debug.LogError("SceneObjectManager not found!");
        if (relationshipLineManager == null) Debug.LogError("RelationshipLineManager not found!");
        if (sceneContextManager == null) Debug.LogError("SceneContextManager not found!");
        if (pointingPlane == null) Debug.LogWarning("PointingPlane not found! Part-specific prompts will not work.");

        // Check critical UI references
        if (chatboxOnObject == null) Debug.LogError("ChatboxOnObject reference is missing! Responses won't be displayed.");
        if (responseTextOnObject == null) 
        {
            Debug.LogError("ResponseTextOnObject reference is missing! Response text won't be displayed.");
            
            // Try to find text component in the chatbox if it exists
            if (chatboxOnObject != null)
            {
                TextMeshPro[] textComponents = chatboxOnObject.GetComponentsInChildren<TextMeshPro>(true);
                if (textComponents.Length > 0)
                {
                    // Use the first TextMeshPro component found
                    responseTextOnObject = textComponents[0];
                    Debug.Log($"[SpeechRecorder] Auto-assigned responseTextOnObject to {responseTextOnObject.name}");
                }
            }
        }
        
        if (chatboxOnObject != null)
        {
            Debug.Log($"ChatboxOnObject found: {chatboxOnObject.name}, Initial state: {(chatboxOnObject.activeSelf ? "Active" : "Inactive")}");
            if (responseTextOnObject != null)
            {
                Debug.Log($"ResponseTextOnObject found: {responseTextOnObject.name}");
                
                // Check if responseTextOnObject is properly nested under chatboxOnObject
                bool isChildOfChatbox = false;
                Transform parent = responseTextOnObject.transform.parent;
                while (parent != null)
                {
                    if (parent.gameObject == chatboxOnObject)
                    {
                        isChildOfChatbox = true;
                        break;
                    }
                    parent = parent.parent;
                }
                
                if (!isChildOfChatbox)
                {
                    Debug.LogError("ResponseTextOnObject is not a child of chatboxOnObject! Text won't display properly in the chatbox.");
                    
                    // Try to find an alternative TextMeshPro component in the chatbox
                    TextMeshPro[] chatboxTextComponents = chatboxOnObject.GetComponentsInChildren<TextMeshPro>(true);
                    if (chatboxTextComponents.Length > 0)
                    {
                        responseTextOnObject = chatboxTextComponents[0];
                        Debug.Log($"[SpeechRecorder] Reassigned responseTextOnObject to {responseTextOnObject.name} inside chatbox");
                    }
                }
                else
                {
                    Debug.Log("ResponseTextOnObject is properly nested under chatboxOnObject.");
                }
            }
        }

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
        
        // Test the API key to catch issues early
        StartCoroutine(TestApiKey());
    }

    private System.Collections.IEnumerator TestApiKey()
    {
        // Short delay to let everything initialize
        yield return new WaitForSeconds(2f);
        
        if (speechToText == null || string.IsNullOrEmpty(speechToText.speechApiKey))
        {
            Debug.LogError("Speech-to-Text API key is not set or component is missing. Transcription will fail.");
            yield break;
        }
        
        if (speechToText.speechApiKey.Contains("YOUR_") || speechToText.speechApiKey.Length < 20)
        {
            Debug.LogError("Speech-to-Text API key appears to be a placeholder or invalid. Please set a valid API key.");
            yield break;
        }
        
        Debug.Log("Testing Speech-to-Text API key...");
        
        // Create a minimal test audio (silence) just to check API connectivity
        byte[] testAudio = new byte[1600]; // 100ms of silence at 16kHz
        
        // Send a minimal request to check if the API key is valid
        var testRequest = speechToText.TranscribeAudio(testAudio);
        
        // Wait for completion
        while (!testRequest.IsCompleted)
        {
            yield return null;
        }
        
        // Check for authentication or quota errors in the response
        if (testRequest.Error != null)
        {
            Debug.LogError($"API key test failed with error: {testRequest.Error.Message}");
            if (testRequest.Error.Message.Contains("401") || testRequest.Error.Message.Contains("403"))
            {
                Debug.LogError("Authorization error: Your Speech-to-Text API key is invalid or has insufficient permissions.");
            }
        }
        else
        {
            string response = testRequest.Result;
            if (response.Contains("error"))
            {
                Debug.LogError($"API key test returned an error: {response}");
                
                try
                {
                    var errorObj = JsonConvert.DeserializeObject<JObject>(response);
                    if (errorObj != null && errorObj["error"] != null && errorObj["error"]["message"] != null)
                    {
                        string errorMsg = errorObj["error"]["message"].ToString();
                        Debug.LogError($"API Error: {errorMsg}");
                        
                        if (errorMsg.Contains("API key"))
                        {
                            Debug.LogError("Your Speech-to-Text API key appears to be invalid.");
                        }
                        else if (errorMsg.Contains("quota"))
                        {
                            Debug.LogError("Your Speech-to-Text API quota has been exceeded.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to parse error response: {ex.Message}");
                }
            }
            else
            {
                Debug.Log("Speech-to-Text API key test successful. Transcription should work correctly.");
            }
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
        
        // Subscribe to pointing state changes
        SphereToggleScript.OnPointingStateChanged += HandlePointingStateChanged;
    }

    private void OnDisable()
    {
        // Unsubscribe from middle finger pinch events
        if (useMiddlePinchControl)
        {
            MyHandTracking.OnMiddlePinchStarted -= HandleMiddlePinchStarted;
            MyHandTracking.OnMiddlePinchEnded -= HandleMiddlePinchEnded;
        }
        
        // Unsubscribe from pointing state changes
        SphereToggleScript.OnPointingStateChanged -= HandlePointingStateChanged;
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
                // Add a small delay before stopping recording
                // This helps prevent cutting off the end of speech
                StartCoroutine(DelayedStopRecording(0.5f));
            }
        }
    }

    private IEnumerator DelayedStopRecording(float delay)
    {
        // Wait for the specified delay
        yield return new WaitForSeconds(delay);
        
        // Only stop if we're still recording
        if (isRecording)
        {
            Debug.Log($"Delayed stop recording after {delay}s");
            ToggleRecording();
        }
    }

    // Function to find description text component by name in the scene
    protected TextMeshPro FindDescriptionTextInScene()
    {
        // Try to find specific game objects with DescriptionText
        TextMeshPro[] allTextComponents = FindObjectsByType<TextMeshPro>(FindObjectsSortMode.None);
        foreach (var textComp in allTextComponents)
        {
            if (textComp.gameObject.name == "DescriptionText" && textComp.gameObject.activeInHierarchy)
            {
                return textComp;
            }
        }

        // If not found by name, try active SphereToggleScript components
        SphereToggleScript[] activeToggles = FindObjectsByType<SphereToggleScript>(FindObjectsSortMode.None);
        foreach (var toggle in activeToggles)
        {
            if (toggle.gameObject.activeInHierarchy && toggle.descriptionText != null)
            {
                return toggle.descriptionText;
            }
        }

        return null;
    }

    // Handler for pointing state changes
    private void HandlePointingStateChanged(bool isPointing)
    {
        if (!isPointing)
        {
            // Reset pointing part info when pointing stops
            currentPointingPartName = null;
            currentPointingPartDescription = null;
            Debug.Log("[SpeechRecorder] Pointing ended, cleared part context");
        }
        else
        {
            // When pointing starts, we'll try to get current part info if possible
            if (pointingPlane != null && pointingPlane.activeSelf)
            {
                var pointingPlaneText = pointingPlane.GetComponentInChildren<TextMeshPro>();
                if (pointingPlaneText != null && !string.IsNullOrEmpty(pointingPlaneText.text) && pointingPlaneText.text != "none")
                {
                    // Look for a description text component
                    TextMeshPro descriptionText = FindDescriptionTextInScene();
                    
                    // Update our pointing information
                    currentPointingPartName = pointingPlaneText.text;
                    currentPointingPartDescription = descriptionText != null ? descriptionText.text : null;
                    
                    Debug.Log($"[SpeechRecorder] Pointing started, captured part info: '{currentPointingPartName}' - '{currentPointingPartDescription}'");
                }
            }
        }
    }

    // Function to update pointing part information - can be called externally
    public void UpdatePointingPartInfo(string partName, string partDescription)
    {
        // If we're clearing the information (both params null)
        if (string.IsNullOrEmpty(partName) && string.IsNullOrEmpty(partDescription))
        {
            if (currentPointingPartName != null)
            {
                Debug.Log($"[SpeechRecorder] Cleared pointing part info (was: '{currentPointingPartName}')");
            }
            currentPointingPartName = null;
            currentPointingPartDescription = null;
            return;
        }
        
        // Only update if we have valid part name
        if (!string.IsNullOrEmpty(partName))
        {
            currentPointingPartName = partName;
            currentPointingPartDescription = partDescription;
            Debug.Log($"[SpeechRecorder] Updated pointing part info: '{partName}' - '{partDescription}'");
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
        
        // Set the objectLevelRecordingToggle flag to true when associated with an object
        objectLevelRecordingToggle = true;
        
        // Save original parent of recorder toggle if this is the first time setting
        if (originalRecorderParent == null && transform.parent != null)
        {
            originalRecorderParent = transform.parent.gameObject;
        }
        
        // Only change parent if we're not already positioned relative to a pointing plane
        bool isAlreadyOnPointingPlane = transform.parent != null && transform.parent.name == "PointingPlane";
        // if (sphereToggle != null && !isAlreadyOnPointingPlane)
        // {
        //     transform.SetParent(sphereToggle.transform);
        // }
        
        Debug.Log($"Recorder now associated with object: {label}, objectLevelRecordingToggle set to {objectLevelRecordingToggle}");
    }

    // Method to reset object label and restore original parent
    public void ResetObjectLabel()
    {
        // Store the current label before clearing it
        string previousLabel = currentObjectLabel;
        currentObjectLabel = null;
        
        // Reset the objectLevelRecordingToggle flag to false
        objectLevelRecordingToggle = false;
        
        // Check if we are currently parented to a pointing plane
        bool wasOnPointingPlane = transform.parent != null && transform.parent.name == "PointingPlane";
        
        transform.SetParent(null);
        
        Debug.Log($"Recorder object association cleared from {previousLabel}, was on pointing plane: {wasOnPointingPlane}, objectLevelRecordingToggle set to {objectLevelRecordingToggle}");
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
        
        if (responseTextOnObject != null) 
        {
            responseTextOnObject.text = "";
            Debug.Log("[SpeechRecorder] Cleared responseTextOnObject text for new recording");
        }
        
        if (chatboxOnObject != null) 
        {
            Debug.Log($"[SpeechRecorder] Setting chatboxOnObject inactive for recording. Current state before: {chatboxOnObject.activeSelf}");
            chatboxOnObject.SetActive(false);
        }
        
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
        try
        {
            // Create a float array for the entire audio clip, not just up to the current position
            // This ensures we don't truncate the recording prematurely
            int totalSamples = recordedAudio.samples;
            float[] fullAudioData = new float[totalSamples * recordedAudio.channels];
            recordedAudio.GetData(fullAudioData, 0);
            
            // Only use up to the recorded position (which is where the microphone stopped)
            float[] audioData = new float[position * recordedAudio.channels];
            Array.Copy(fullAudioData, audioData, audioData.Length);

            // Check if audio data has adequate volume
            float maxVolume = 0;
            float sumVolume = 0;
            for (int i = 0; i < audioData.Length; i++)
            {
                float absValue = Mathf.Abs(audioData[i]);
                maxVolume = Mathf.Max(maxVolume, absValue);
                sumVolume += absValue;
            }
            float avgVolume = sumVolume / audioData.Length;
            
            Debug.Log($"Audio stats - Length: {audioData.Length} samples, Max volume: {maxVolume}, Avg volume: {avgVolume}");
            
            // Warning for low volume
            if (maxVolume < 0.05f)
            {
                Debug.LogWarning("Audio volume appears to be very low. Microphone may not be capturing properly.");
            }

            // Convert float array to PCM byte array (16-bit)
            byte[] byteData = ConvertAudioDataToBytes(audioData);
            
            Debug.Log($"Sending {byteData.Length} bytes of audio data for transcription");

            // Now send for transcription
            var requestStatus = speechToText.TranscribeAudio(byteData);

            // Wait for and handle the result
            StartCoroutine(WaitForTranscriptionResult(requestStatus));
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error processing recording: {ex.Message}\nStack trace: {ex.StackTrace}");
            isProcessing = false;
            transcriptionResult = "Error processing audio.";
        }
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
            if (requestStatus.Error.InnerException != null)
            {
                Debug.LogError($"Inner exception: {requestStatus.Error.InnerException.Message}");
            }
            transcriptionResult = "Error during transcription.";
        }
        else
        {
            string rawJson = requestStatus.Result;
            Debug.Log($"Raw transcription response: {rawJson}");

            // Check for API error message
            if (rawJson.Contains("\"error\":"))
            {
                Debug.LogError("API error detected in response!");
                
                // Try to extract the error message
                try
                {
                    var errorResponse = JsonConvert.DeserializeObject<JObject>(rawJson);
                    if (errorResponse != null && errorResponse["error"] != null)
                    {
                        string errorMsg = errorResponse["error"].ToString();
                        
                        if (errorMsg.Contains("API key"))
                        {
                            transcriptionResult = "API Key Error: Please check your Google API key.";
                            Debug.LogError("Speech-to-Text API key appears to be invalid or expired. Please check the key in the SpeechToTextGeneral component.");
                        }
                        else if (errorMsg.Contains("quota"))
                        {
                            transcriptionResult = "API Error: Quota exceeded. Try again later.";
                            Debug.LogError("Speech-to-Text API quota has been exceeded. You may need to wait or upgrade your quota.");
                        }
                        else
                        {
                            transcriptionResult = $"API Error: {errorMsg}";
                        }
                    }
                    else
                    {
                        transcriptionResult = "Unknown API error.";
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error parsing API error: {ex.Message}");
                    transcriptionResult = "Error parsing API response.";
                }
            }
            // Validate the response has the expected format
            else if (string.IsNullOrEmpty(rawJson))
            {
                Debug.LogError("Received empty response from transcription service.");
                transcriptionResult = "No response from transcription service.";
            }
            else if (!rawJson.Contains("results"))
            {
                Debug.LogError("Response does not contain 'results' field. API key may be invalid or quota exceeded.");
                
                // Check for common responses
                if (rawJson.Contains("totalBilledTime") && rawJson.Contains("requestId") && !rawJson.Contains("results"))
                {
                    Debug.LogError("Received billing info but no results - this typically means the API processed the request but didn't detect any speech in the audio.");
                    Debug.LogError("Make sure your microphone is working and you're speaking clearly during recording.");
                    transcriptionResult = "No speech detected in audio.";
                }
                else
                {
                    Debug.LogError("Check your API key validity, quota, and billing status in the Google Cloud Console.");
                    transcriptionResult = "API error: Invalid response format.";
                }
            }
            else
            {
                // Parse the result to get just the transcript text
                string parsedResult = SpeechToTextGeneral.ParseTranscriptionResult(rawJson);
                
                if (!string.IsNullOrEmpty(parsedResult))
                {
                    transcriptionResult = parsedResult;
                    requestText.text = transcriptionResult;
                    
                    // Log the user query for user study
                    LogUserStudy($"[VOICE_INPUT] USER_QUERY: \"{transcriptionResult}\"");
                    
                    TalkToGemini(transcriptionResult);
                }
                else
                {
                    transcriptionResult = "No text recognized.";
                    Debug.LogWarning("No transcription found in response.");
                }
            }
        }

        isProcessing = false;
    }

    private byte[] ConvertAudioDataToBytes(float[] audioData)
    {
        // Normalize audio to improve speech recognition
        float maxAbs = 0.01f; // Set minimum threshold to avoid division by zero
        
        // Find maximum amplitude for normalization
        for (int i = 0; i < audioData.Length; i++)
        {
            float absValue = Mathf.Abs(audioData[i]);
            if (absValue > maxAbs)
            {
                maxAbs = absValue;
            }
        }
        
        // Apply some light normalization if volume is low
        if (maxAbs < 0.3f)
        {
            float normalizationFactor = Mathf.Min(0.7f / maxAbs, 3.0f); // Cap amplification to 3x
            Debug.Log($"Normalizing audio by factor: {normalizationFactor}");
            
            for (int i = 0; i < audioData.Length; i++)
            {
                audioData[i] *= normalizationFactor;
                // Clamp to valid range to prevent clipping
                audioData[i] = Mathf.Clamp(audioData[i], -0.99f, 0.99f);
            }
        }
        
        // Find actual content range (trim silence from beginning and end)
        int startIndex = 0;
        int endIndex = audioData.Length - 1;
        float silenceThreshold = 0.01f;
        
        // Find start of speech (skip silence at beginning)
        for (int i = 0; i < audioData.Length; i++)
        {
            if (Mathf.Abs(audioData[i]) > silenceThreshold)
            {
                startIndex = Mathf.Max(0, i - 1600); // Include a small buffer before speech (100ms at 16kHz)
                break;
            }
        }
        
        // Find end of speech (skip silence at end)
        for (int i = audioData.Length - 1; i >= 0; i--)
        {
            if (Mathf.Abs(audioData[i]) > silenceThreshold)
            {
                endIndex = Mathf.Min(audioData.Length - 1, i + 1600); // Include a small buffer after speech
                break;
            }
        }
        
        // Create a trimmed version if we found meaningful boundaries and it would save significant space
        if (startIndex < endIndex && (endIndex - startIndex) < audioData.Length * 0.95f)
        {
            int trimmedLength = endIndex - startIndex + 1;
            Debug.Log($"Trimming audio from {audioData.Length} to {trimmedLength} samples (removing silence)");
            
            float[] trimmedData = new float[trimmedLength];
            Array.Copy(audioData, startIndex, trimmedData, 0, trimmedLength);
            audioData = trimmedData;
        }
        else
        {
            Debug.Log("Using full audio without trimming");
        }
        
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

    public virtual void TalkToGemini(string userQuery)
    {
        string finalPrompt;
        // Update this line to check both conditions
        bool isObjectMode = objectLevelRecordingToggle && !string.IsNullOrEmpty(currentObjectLabel);
        string contextType = "UNKNOWN";

        // Check if we're pointing at a specific part of an object
        bool isPointingAtPart = false;
        string pointingPartName = "";
        string pointingPartDescription = "";
        
        // First check if we have stored pointing information
        if (!string.IsNullOrEmpty(currentPointingPartName))
        {
            isPointingAtPart = true;
            pointingPartName = currentPointingPartName;
            pointingPartDescription = currentPointingPartDescription ?? "";
            Debug.Log($"[SpeechRecorder] Using stored pointing info: '{pointingPartName}' with description: '{pointingPartDescription}'");
        }
        // If not, try to get pointing information directly
        else if (isObjectMode && pointingPlane != null && pointingPlane.activeSelf)
        {
            // Try to get the name of the part being pointed at
            var pointingPlaneText = pointingPlane.GetComponentInChildren<TextMeshPro>();
            if (pointingPlaneText != null && !string.IsNullOrEmpty(pointingPlaneText.text) && pointingPlaneText.text != "none")
            {
                isPointingAtPart = true;
                pointingPartName = pointingPlaneText.text;
                
                // Find description text using our helper method
                TextMeshPro descriptionTextInPlane = FindDescriptionTextInScene();
                
                if (descriptionTextInPlane != null && !string.IsNullOrEmpty(descriptionTextInPlane.text))
                {
                    pointingPartDescription = descriptionTextInPlane.text;
                }
                
                // Store this information for future use
                currentPointingPartName = pointingPartName;
                currentPointingPartDescription = pointingPartDescription;
                
                Debug.Log($"[SpeechRecorder] Detected pointing at part: '{pointingPartName}' with description: '{pointingPartDescription}'");
            }
        }

        // If pointing at a specific part of the object, use the pointing prompt template
        if (isObjectMode && isPointingAtPart)
        {
            finalPrompt = string.Format(pointingPromptTemplate, currentObjectLabel, pointingPartName, pointingPartDescription, userQuery);
            Debug.Log($"[SpeechRecorder] Using POINTING context prompt for '{pointingPartName}' part of '{currentObjectLabel}'");
            contextType = "PART_SPECIFIC";
            
            // Log context for user study
            LogUserStudy($"[VOICE_INPUT] [OBJECT_POINTING] CONTEXT: Type=\"{contextType}\", Object=\"{currentObjectLabel}\", Part=\"{pointingPartName}\", PartDescription=\"{pointingPartDescription}\"");
        }
        // Otherwise, if in object mode, use the object context prompt template
        else if (isObjectMode)
        {
            finalPrompt = string.Format(objectContextPromptTemplate, currentObjectLabel, userQuery);
            Debug.Log($"[SpeechRecorder] Using OBJECT context prompt with label '{currentObjectLabel}'");
            contextType = "OBJECT_LEVEL";
            
            // Log context for user study
            LogUserStudy($"[VOICE_INPUT] [OBJECT] CONTEXT: Type=\"{contextType}\", Object=\"{currentObjectLabel}\"");
        }
        else // Global Mode
        {
             // --- Prepare context for Global Mode Prompt ---
            contextType = "ENVIRONMENT_LEVEL";
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
            
            // Log context for user study
            LogUserStudy($"[VOICE_INPUT] [ENV] CONTEXT: Type=\"{contextType}\", SceneType=\"{currentSceneContext}\", Objects=\"{objectListString}\"");
            
             // --- Format the Global Prompt ---
            finalPrompt = string.Format(environmentLevelPromptTemplate, currentSceneContext, objectListString, userQuery);
            Debug.Log($"[SpeechRecorder] Using ENVIRONMENT context prompt.");
            Debug.Log($"[SpeechRecorder] Scene: {currentSceneContext}, Objects: {objectListString}");
        }

        Debug.Log($"[SpeechRecorder] Final prompt: {finalPrompt}");

        // Use MakeGeminiRequest for concurrency management inherited from GeminiGeneral
        var request = MakeGeminiRequest(finalPrompt, null); // Assuming no image needed for these prompts

        // Start the coroutine to wait for the response and handle it
        StartCoroutine(GeminiQueryRoutine(request, isObjectMode, userQuery));
    }

    protected IEnumerator GeminiQueryRoutine(RequestStatus requestStatus, bool isObjectMode, string originalQuery)
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
            
            // Log error for user study
            LogUserStudy($"[VOICE_INPUT] [GEMINI] GEMINI_ERROR: {requestStatus.Error.Message}");
        }
        else
        {
            string rawResponse = requestStatus.Result;
            geminiResponse = ParseGeminiRawResponse(rawResponse);
            Debug.Log($"Gemini response: {geminiResponse}");

            // Update UI if available - now showing the user-friendly message
            if (responseTextOnObject != null && !string.IsNullOrEmpty(geminiResponse))
            {
                try
                {
                    // Try to extract JSON from the response
                    string jsonContent = TryExtractJson(geminiResponse);
                    if (!string.IsNullOrEmpty(jsonContent))
                    {
                        try {
                            ResponseWrapper responseWrapper = JsonConvert.DeserializeObject<ResponseWrapper>(jsonContent);
                            if (responseWrapper != null && !string.IsNullOrEmpty(responseWrapper.message))
                            {
                                // Just display the message part, not the whole JSON
                                responseTextOnObject.text = responseWrapper.message;
                                
                                // Log extracted message for user study
                                LogUserStudy($"[VOICE_INPUT] [GEMINI] DISPLAYED_MESSAGE: {responseWrapper.message}");
                            }
                            else
                            {
                                responseTextOnObject.text = geminiResponse;
                                Debug.Log("[SpeechRecorder] Using raw response as text (JSON wrapper had no message)");
                            }
                        }
                        catch (JsonException jsonEx)
                        {
                            // If JSON parsing fails, show raw response
                            Debug.LogWarning($"[SpeechRecorder] JSON parsing exception: {jsonEx.Message}");
                            responseTextOnObject.text = geminiResponse;
                            LogUserStudy($"[VOICE_INPUT] [GEMINI] DISPLAYED_MESSAGE: {geminiResponse}");
                        }
                    }
                    else
                    {
                        // No JSON found in the response, use the raw text directly
                        responseTextOnObject.text = geminiResponse;
                        Debug.Log("[SpeechRecorder] Using raw response as text (no JSON found)");
                        
                        // Log the displayed message for user study
                        LogUserStudy($"[VOICE_INPUT] [GEMINI] DISPLAYED_MESSAGE: {geminiResponse}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to parse response for object text: {ex.Message}");
                    responseTextOnObject.text = geminiResponse;
                    Debug.Log("[SpeechRecorder] Using raw response as text after exception: " + ex.Message);
                    
                    // Log the displayed message for user study
                    LogUserStudy($"[VOICE_INPUT] [GEMINI] DISPLAYED_MESSAGE: {geminiResponse}");
                }
            }
            else if (responseTextOnObject == null)
            {
                Debug.LogError("[SpeechRecorder] responseTextOnObject is null! Cannot display text response.");
            }

            // Update UI if available
            if (chatboxOnObject != null)
            {
                Debug.Log($"[SpeechRecorder] Setting chatboxOnObject active state to true. Reference exists: {chatboxOnObject != null}, Current active state: {chatboxOnObject.activeSelf}");
                chatboxOnObject.SetActive(true);
                
                // Force update to ensure the object is visible
                if (chatboxOnObject.transform.parent != null)
                {
                    Canvas canvas = chatboxOnObject.GetComponentInParent<Canvas>();
                    if (canvas != null)
                    {
                        Debug.Log("[SpeechRecorder] Found parent canvas, forcing refresh");
                        canvas.enabled = false;
                        canvas.enabled = true;
                    }
                }
                
                // Double-check that responseTextOnObject has the correct text after activation
                if (responseTextOnObject != null)
                {
                    // Ensure the text field has our response
                    if (string.IsNullOrEmpty(responseTextOnObject.text))
                    {
                        Debug.LogWarning("[SpeechRecorder] Response text was empty after activation, forcing update");
                        responseTextOnObject.text = geminiResponse;
                    }
                    
                    // Force the TextMeshPro component to update
                    responseTextOnObject.ForceMeshUpdate();
                    Debug.Log($"[SpeechRecorder] responseTextOnObject text after activation: '{responseTextOnObject.text.Substring(0, Math.Min(50, responseTextOnObject.text.Length))}...'");
                }
                else
                {
                    Debug.LogError("[SpeechRecorder] responseTextOnObject is null when trying to update after chatbox activation!");
                    
                    // Try to find responseTextOnObject in children of chatboxOnObject
                    TextMeshPro[] textComponents = chatboxOnObject.GetComponentsInChildren<TextMeshPro>(true);
                    if (textComponents.Length > 0)
                    {
                        Debug.Log($"[SpeechRecorder] Found {textComponents.Length} TextMeshPro components in chatboxOnObject children");
                        foreach (var textComp in textComponents)
                        {
                            Debug.Log($"[SpeechRecorder] TextMeshPro component: {textComp.name}, Text: {textComp.text.Substring(0, Math.Min(20, textComp.text.Length))}");
                            textComp.text = geminiResponse;
                            textComp.ForceMeshUpdate();
                        }
                    }
                }
                
                // Start the auto-hide timer if enabled
                if (enableResponseAutoHide)
                {
                    // Cancel any existing hide coroutine to prevent multiple timers
                    if (responseHideCoroutine != null)
                    {
                        StopCoroutine(responseHideCoroutine);
                    }
                    
                    // Set up the auto-hide for both UI elements and visual highlights
                    if (relationshipLineManager != null)
                    {
                        // Reset the relationship manager's timer to ensure synchronization
                        relationshipLineManager.highlightTimer = 0f;
                        relationshipLineManager.hasActiveHighlights = true;
                    }
                    
                    // Start our own auto-hide coroutine
                    responseHideCoroutine = StartCoroutine(AutoHideResponseAfterDelay(responseDisplayDuration));
                    Debug.Log($"[SpeechRecorder] Response UI will auto-hide in {responseDisplayDuration} seconds");
                }
                else
                {
                    // If auto-hide is disabled, make sure the relationship manager knows not to auto-hide
                    if (relationshipLineManager != null)
                    {
                        relationshipLineManager.hasActiveHighlights = false;
                    }
                }
            }
            else
            {
                Debug.LogWarning("[SpeechRecorder] chatboxOnObject reference is null! Cannot display response.");
            }
            
            // Invoke event with the response
            onGeminiResponseReceived?.Invoke(geminiResponse);
            
            // Parse the response for relationships or highlights if it's not in object mode
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
                        // Log extracted JSON for user study (environment level only)
                        string sanitizedJson = jsonContent.Replace("\n", " ").Replace("\r", "");
                        LogUserStudy($"[VOICE_INPUT] [ENV] JSON_RESPONSE: {sanitizedJson}");
                        
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
                                        
                                        // Store the original message to use in UI
                                        string relationshipsMessage = responseWrapper.message;
                                        ProcessRelationships(relationships, sceneObjectManager.GetAllAnchors(), originalQuery, relationshipsMessage);
                                        // LogUserStudy($"RESPONSE_TYPE: relationships");
                                        break;
                                        
                                    case "highlight":
                                        // Convert the data object to a JSON string first, then deserialize to the proper type
                                        string highlightJson = JsonConvert.SerializeObject(responseWrapper.data);
                                        HighlightData highlightData = JsonConvert.DeserializeObject<HighlightData>(highlightJson);
                                        
                                        // Store the original message to use in UI
                                        string highlightMessage = responseWrapper.message;
                                        ProcessHighlights(highlightData, sceneObjectManager.GetAllAnchors(), originalQuery, highlightMessage);
                                        // LogUserStudy($"RESPONSE_TYPE: highlight");
                                        break;
                                        
                                    case "instruction":
                                        // Convert the data object to a JSON string first, then deserialize to the proper type
                                        string instructionJson = JsonConvert.SerializeObject(responseWrapper.data);
                                        List<InstructionStep> instructionSteps = JsonConvert.DeserializeObject<List<InstructionStep>>(instructionJson);
                                        
                                        // Store the original message to use in UI
                                        string instructionMessage = responseWrapper.message;
                                        ProcessInstructions(instructionSteps, sceneObjectManager.GetAllAnchors(), originalQuery, instructionMessage);
                                        // LogUserStudy($"RESPONSE_TYPE: instruction");
                                        break;
                                        
                                    case "none":
                                        Debug.Log($"[SpeechRecorder] No relevant objects or relationships found: {responseWrapper.message}");
                                        string sanitizedNoneMessage = responseWrapper.message?.Replace("\n", " ").Replace("\r", "");
                                        // LogUserStudy($"RESPONSE_TYPE: none");
                                        LogUserStudy($"[VOICE_INPUT] [ENV] PROCESS_RESULT: Type=\"none\", Message=\"{sanitizedNoneMessage}\"");
                                        break;
                                        
                                    default:
                                        Debug.LogWarning($"[SpeechRecorder] Unknown response type: {responseWrapper.type}");
                                        // LogUserStudy($"RESPONSE_TYPE: unknown_{responseWrapper.type}");
                                        LogUserStudy($"[VOICE_INPUT] [ENV] PROCESS_RESULT: Type=\"unknown\", ResponseType=\"{responseWrapper.type}\"");
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
                                        // For legacy format, we don't have a message field, so pass null or empty string
                                        ProcessRelationships(relationships, sceneObjectManager.GetAllAnchors(), originalQuery, "");
                                    }
                                    else if (jsonContent.Trim().StartsWith("{"))
                                    {
                                        var singleRelationship = JsonConvert.DeserializeObject<RelationshipInfo>(jsonContent);
                                        if (singleRelationship != null)
                                        {
                                            // For legacy format, we don't have a message field, so pass null or empty string
                                            ProcessRelationships(new List<RelationshipInfo> { singleRelationship }, sceneObjectManager.GetAllAnchors(), originalQuery, "");
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
                                    // For legacy format, we don't have a message field, so pass null or empty string
                                    ProcessHighlights(highlightData, sceneObjectManager.GetAllAnchors(), originalQuery, "");
                                }
                                catch (JsonException je)
                                {
                                    Debug.LogWarning($"[SpeechRecorder] Failed to parse as highlight data: {je.Message}");
                                }
                            }
                            // Check if it could be instruction format
                            else if (jsonContent.Contains("order") && jsonContent.Contains("object"))
                            {
                                try
                                {
                                    List<InstructionStep> instructionSteps = JsonConvert.DeserializeObject<List<InstructionStep>>(jsonContent);
                                    // For legacy format, we don't have a message field, so pass null or empty string
                                    ProcessInstructions(instructionSteps, sceneObjectManager.GetAllAnchors(), originalQuery, "");
                                }
                                catch (JsonException je)
                                {
                                    Debug.LogWarning($"[SpeechRecorder] Failed to parse as instruction steps: {je.Message}");
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
                                        // For old format, we don't have a message field, create a simple one
                                        string oldFormatMessage = $"Found {relationships.Count} relationships between objects";
                                        ProcessRelationships(relationships, sceneObjectManager.GetAllAnchors(), originalQuery, oldFormatMessage);
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
                            LogUserStudy($"[VOICE_INPUT] [ENV] JSON_PARSING_ERROR: {ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.Log("[SpeechRecorder] No valid JSON found in response");
                        LogUserStudy("[VOICE_INPUT] [ENV] NO_JSON_FOUND");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SpeechRecorder] Error processing response: {ex.Message}");
                    LogUserStudy($"[VOICE_INPUT] [ENV] RESPONSE_PROCESSING_ERROR: {ex.Message}");
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

        // Case 3: Try to find a complete JSON object first (prioritize over arrays)
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
                Debug.LogWarning($"Found object-like structure but failed to parse as JSON: {jsonSubstring}");
            }
        }

        // Case 4: Only then check for JSON arrays
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
                Debug.LogWarning($"Found array-like structure but failed to parse as JSON: {jsonSubstring}");
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
    private void ProcessRelationships(List<RelationshipInfo> relationships, List<SceneObjectAnchor> allAnchors, string originalQuery, string relationshipsMessage)
    {
        if (relationships == null || relationships.Count == 0)
        {
            Debug.Log("[SpeechRecorder] No relationships to process");
            LogUserStudy("[VOICE_INPUT] [ENV] PROCESS_RESULT: Type=\"relationships\", Status=\"no_relationships_found\"");
            return;
        }
        
        Debug.Log($"[SpeechRecorder] Processing {relationships.Count} relationships");
        
        // Build a detailed summary for logging
        StringBuilder relationshipSummary = new StringBuilder();
        relationshipSummary.Append($"Count={relationships.Count}");
        
        // Log details of each relationship for debugging and include in user study log
        for (int i = 0; i < relationships.Count; i++)
        {
            var rel = relationships[i];
            Debug.Log($"[SpeechRecorder] Relationship {i+1}: {rel.SourceObject} -> {rel.TargetObject}: '{rel.RelationLabel}'");
            relationshipSummary.Append($", R{i+1}=\"{rel.SourceObject}->{rel.TargetObject}:{rel.RelationLabel}\"");
        }
        
        // Log the relationship details for user study
        string sanitizedMessage = relationshipsMessage?.Replace("\n", " ").Replace("\r", "") ?? "No message";
        LogUserStudy($"[VOICE_INPUT] [ENV] PROCESS_RESULT: Type=\"relationships\", {relationshipSummary.ToString()}, DisplayedMessage=\"{sanitizedMessage}\"");
        
        // Convert our internal RelationshipInfo objects to the RelationshipLineManager format
        List<RelationshipLineManager.RelationshipInfo> relationshipLineInfos = 
            relationships.Select(r => new RelationshipLineManager.RelationshipInfo
            {
                SourceObject = r.SourceObject,
                TargetObject = r.TargetObject,
                RelationLabel = r.RelationLabel
            }).ToList();
        
        // Call the bidirectional relationship visualization with timeout enabled for LLM-generated relationships
        relationshipLineManager.ShowBidirectionalRelationships(relationshipLineInfos, allAnchors, true);
        Debug.Log($"[SpeechRecorder] Visualized {relationships.Count} bidirectional relationships with auto-timeout enabled");
        
        // Create a response message for display
        if (relationships.Count > 0)
        {
            // Use the original message from Gemini instead of constructing a new one
            if (responseTextOnObject != null)
            {
                responseTextOnObject.text = relationshipsMessage;
            }
            
            // Make sure chatbox is shown
            if (chatboxOnObject != null && !chatboxOnObject.activeSelf)
            {
                Debug.Log("[SpeechRecorder] Activating chatbox to show relationship message from Gemini");
                chatboxOnObject.SetActive(true);
                
                // Force update to ensure the object is visible
                if (chatboxOnObject.transform.parent != null)
                {
                    Canvas canvas = chatboxOnObject.GetComponentInParent<Canvas>();
                    if (canvas != null)
                    {
                        canvas.enabled = false;
                        canvas.enabled = true;
                    }
                }
            }
        }
        
        // Generate follow-up questions based on the relationships
        if (relationships.Count > 0)
        {
            GenerateQuestionsAfterProcessJSON(originalQuery, relationships, relationshipsMessage);
        }
    }

    // Helper method to find all matching anchors for a given label
    private List<SceneObjectAnchor> FindMatchingAnchors(string label, List<SceneObjectAnchor> anchors)
    {
        List<SceneObjectAnchor> matches = new List<SceneObjectAnchor>();
        
        // Step 1: Try exact matches (case-insensitive)
        matches.AddRange(anchors.Where(a => string.Equals(a.label, label, System.StringComparison.OrdinalIgnoreCase)));
        if (matches.Count > 0)
        {
            return matches;
        }
        
        // Step 2: Try contains matches
        matches.AddRange(anchors.Where(a => 
            a.label.IndexOf(label, System.StringComparison.OrdinalIgnoreCase) >= 0 || 
            label.IndexOf(a.label, System.StringComparison.OrdinalIgnoreCase) >= 0));
            
        if (matches.Count > 0)
        {
            return matches;
        }
        
        // Step 3: Try word-by-word match for multi-word labels
        string[] words = label.Split(' ', '-', '_');
        if (words.Length > 1)
        {
            foreach (var word in words)
            {
                if (word.Length < 3) continue; // Skip short words
                
                var wordMatches = anchors.Where(a => 
                    a.label.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    
                matches.AddRange(wordMatches);
            }
        }
        
        return matches.Distinct().ToList(); // Remove any duplicates
    }

    // New method to process highlight data
    private void ProcessHighlights(HighlightData highlightData, List<SceneObjectAnchor> allAnchors, string originalQuery, string highlightMessage)
    {
        if (highlightData == null || highlightData.objects == null || highlightData.objects.Count == 0)
        {
            Debug.Log("[SpeechRecorder] No objects to highlight");
            LogUserStudy("[VOICE_INPUT] [ENV] PROCESS_RESULT: Type=\"highlight\", Status=\"no_objects_found\"");
            return;
        }
        
        Debug.Log($"[SpeechRecorder] Highlighting {highlightData.objects.Count} objects: {string.Join(", ", highlightData.objects)}");
        Debug.Log($"[SpeechRecorder] Rationale: {highlightData.rationale}");

        // Log detailed highlight information for user study
        string objectsList = string.Join(", ", highlightData.objects);
        string sanitizedRationale = highlightData.rationale?.Replace("\n", " ").Replace("\r", "");
        string sanitizedMessage = highlightMessage?.Replace("\n", " ").Replace("\r", "") ?? "No message";
        LogUserStudy($"[VOICE_INPUT] [ENV] PROCESS_RESULT: Type=\"highlight\", Count={highlightData.objects.Count}, Objects=\"{objectsList}\", Rationale=\"{sanitizedRationale}\", DisplayedMessage=\"{sanitizedMessage}\"");

        // Clear all previous highlights before starting new ones
        relationshipLineManager.ClearAllHighlightsAndLines();
        
        // Note: We don't need to manually set these anymore as the ShowRelationships method will handle it
        // Keep using auto-timeout for LLM-generated highlights (timeout = true)
        
        // Use the same highlight color as RelationshipLineManager (hex: #2096F3 with 100% alpha)
        Color highlightColor = new Color(
            r: 0.125f,  // 32/255
            g: 0.588f,  // 150/255
            b: 0.953f,  // 243/255
            a: 1.0f     // 100% alpha
        );
        
        // Find and highlight each object
        HashSet<SceneObjectAnchor> highlightedAnchors = new HashSet<SceneObjectAnchor>();
        
        foreach (string objectName in highlightData.objects)
        {
            List<SceneObjectAnchor> matchingAnchors = FindMatchingAnchors(objectName, allAnchors);
            
            foreach (var anchor in matchingAnchors)
            {
                if (anchor != null && anchor.sphereObj != null && !highlightedAnchors.Contains(anchor))
                {
                    // Highlight the sphere
                    var renderer = anchor.sphereObj.GetComponent<Renderer>();
                    if (renderer != null && renderer.material != null)
                    {
                        renderer.material.color = highlightColor;
                    }

                    // Highlight the child label object
                    var labelObj = anchor.sphereObj.transform.GetChild(0)?.gameObject;
                    if (labelObj != null)
                    {
                        var labelRenderer = labelObj.GetComponent<Renderer>();
                        if (labelRenderer != null && labelRenderer.material != null)
                        {
                            labelRenderer.material.color = highlightColor;
                        }
                    }

                    highlightedAnchors.Add(anchor);
                    Debug.Log($"[SpeechRecorder] Highlighted object and label: {anchor.label}");
                }
            }
            
            if (matchingAnchors.Count == 0)
            {
                Debug.LogWarning($"[SpeechRecorder] Could not find any objects to highlight matching: {objectName}");
            }
        }
        
        // Update response text with the rationale if objects were highlighted
        if (highlightedAnchors.Count > 0 && responseTextOnObject != null)
        {
            // Use the original message from Gemini instead of constructing a new one
            responseTextOnObject.text = highlightMessage;
            
            // Make sure chatbox is shown
            if (chatboxOnObject != null && !chatboxOnObject.activeSelf)
            {
                Debug.Log("[SpeechRecorder] Activating chatbox to show highlight message from Gemini");
                chatboxOnObject.SetActive(true);
                
                // Force update to ensure the object is visible
                if (chatboxOnObject.transform.parent != null)
                {
                    Canvas canvas = chatboxOnObject.GetComponentInParent<Canvas>();
                    if (canvas != null)
                    {
                        canvas.enabled = false;
                        canvas.enabled = true;
                    }
                }
            }
        }
        
        // Generate follow-up questions based on the highlight results
        if (highlightData.objects.Count > 0)
        {
            GenerateQuestionsAfterProcessJSON(originalQuery, highlightData, highlightMessage);
        }
    }

    /// <summary>
    /// Generates follow-up questions after processing a JSON response from Gemini
    /// </summary>
    private void GenerateQuestionsAfterProcessJSON(string userQuery, object processedData, string geminiMessage = "")
    {
        Debug.Log($"[SpeechRecorder] Generating follow-up questions for query: {userQuery}");
        
        // Show the menu canvas if it's not already visible
        if (infoPanelParent != null)
        {
            infoPanelParent.gameObject.SetActive(true);
            
            // Position it appropriately
            PositionInfoPanelParent();
        }
        else
        {
            Debug.LogWarning("[SpeechRecorder] infoPanelParent is null, cannot display questions");
            return;
        }
        
        // Determine the context based on data type
        string context = "";
        string contextDetails = "";
        
        if (processedData is HighlightData highlightData)
        {
            // For highlight data, use the highlighted objects and rationale
            string highlightedObjects = string.Join(", ", highlightData.objects);
            context = $"The system has highlighted: {highlightedObjects}";
            contextDetails = $"Reason: {highlightData.rationale}";
            
            // For highlights, include information about the object
            if (highlightData.objects.Count == 1)
            {
                // Single object highlight - likely user is interested in this specific item
                contextDetails += $"\nUser appears to be interested in '{highlightData.objects[0]}'";
            }
            else
            {
                // Multiple highlights - might be comparing or searching
                contextDetails += $"\nUser may be comparing or looking for relationships between {highlightedObjects}";
            }
        }
        else if (processedData is List<RelationshipInfo> relationships)
        {
            // For relationships, provide clearer structure
            List<string> relationshipPairs = new List<string>();
            HashSet<string> involvedObjects = new HashSet<string>();
            
            foreach (var rel in relationships)
            {
                relationshipPairs.Add($"'{rel.SourceObject}' is {rel.RelationLabel} to '{rel.TargetObject}'");
                involvedObjects.Add(rel.SourceObject);
                involvedObjects.Add(rel.TargetObject);
            }
            
            context = $"The system has shown relationships between {string.Join(", ", involvedObjects)}:";
            contextDetails = string.Join("\n", relationshipPairs);
            
            // For relationships, include info about the connection type
            if (relationships.Count == 1)
            {
                // Single relationship - user probably wants to know more about this connection
                contextDetails += $"\nUser is interested in how '{relationships[0].SourceObject}' relates to '{relationships[0].TargetObject}'";
            }
            else
            {
                // Multiple relationships - might want a summary or overview
                contextDetails += $"\nUser may want to understand the overall connections between these objects";
            }
        }
        else if (processedData is List<InstructionStep> instructionSteps)
        {
            // For instructions, provide context about the steps
            List<string> stepDescriptions = new List<string>();
            foreach (var step in instructionSteps)
            {
                stepDescriptions.Add($"Step {step.order}: Use {step.@object}");
            }
            
            context = $"The system has shown a sequence of {instructionSteps.Count} steps:";
            contextDetails = string.Join("\n", stepDescriptions);
            
            // Add specific context for instruction questions
            contextDetails += $"\nUser appears to be asking about a procedure or task involving {instructionSteps.Count} steps.";
        }
        else
        {
            // Generic case if data type is unknown
            context = "The system has responded to the user's query";
            contextDetails = "The user may want to learn more about what they're seeing";
        }
        
        // Start the question generation routine
        StartCoroutine(GenerateQuestionsRoutine(userQuery, context, contextDetails));
    }
    
    /// <summary>
    /// Coroutine that generates questions about the processed result using Gemini
    /// </summary>
    private IEnumerator GenerateQuestionsRoutine(string userQuery, string context, string additionalContext = "")
    {
        Debug.Log($"[SpeechRecorder] Starting question generation for: {userQuery} with context: {context}");
        
        // Clear any existing questions first
        ClearPreviousQuestions();
        
        // Build a prompt for Gemini to generate questions based on the user query and result context
        string prompt = $@"
            Given that the user just asked: ""{userQuery}""
            
            And the system just showed this result: {context}
            
            {additionalContext}
            
            Please predict what the user might genuinely want to ask next.
            
            Return a JSON list of possible follow-up questions FROM THE USER'S PERSPECTIVE.
            The user has seen the result of their query, and now would likely want to know more.
            
            Focus on questions that:
            1. Are genuinely valuable to the user in their current context
            2. Reflect the user's likely motivation for their original query
            3. Help the user learn more about what they just discovered
            4. Represent what a real person would naturally ask next
            5. Are practical and directly relevant to the object(s) in focus
            
            IMPORTANT: Each question MUST be very concise - less than 10 words total.
            Make each question as short as possible while still being clear.
            Focus on brevity and directness.
            
            Return only the most likely questions, up to 5 maximum.
            
            In the format:
            json
            [
            ""Short question 1?"",
            ""Short question 2?"",
            ...
            ]
        ";
        
        // Call Gemini
        Debug.Log("[SpeechRecorder] Sending question generation request to Gemini...");
        var request = MakeGeminiRequest(prompt, null);
        
        // Wait for completion
        while (!request.IsCompleted)
            yield return null;
        
        string geminiResponse = request.Result;
        Debug.Log($"[SpeechRecorder] Received response from Gemini: {geminiResponse}");
        
        // Extract JSON
        string extractedJson = TryExtractQuestionJson(geminiResponse);
        Debug.Log($"[SpeechRecorder] Extracted JSON: {extractedJson}");
        
        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("[SpeechRecorder] Could not find valid JSON block in Gemini question response.");
            LogUserStudy("[VOICE_INPUT] [SUGGESTED_ACTIONS] FOLLOW_UP_QUESTIONS: Failed to generate");
            yield break;
        }
        
        // Parse the JSON into a list of questions
        List<string> questionsList = null;
        try
        {
            questionsList = JsonConvert.DeserializeObject<List<string>>(extractedJson);
            Debug.Log($"[SpeechRecorder] Successfully parsed {questionsList.Count} questions from JSON");
            
            // Log follow-up questions for user study
            if (questionsList != null && questionsList.Count > 0)
            {
                LogUserStudy($"[VOICE_INPUT] [SUGGESTED_ACTIONS] FOLLOW_UP_QUESTIONS: {string.Join(" | ", questionsList)}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpeechRecorder] Failed to parse question array: {e.Message}");
            
            // Fallback parsing - try to manually extract the strings
            try
            {
                Debug.Log("[SpeechRecorder] Attempting fallback parsing...");
                questionsList = new List<string>();
                
                // Look for string patterns in the format: "Question text"
                string pattern = "\"([^\"]+)\"";
                var matches = System.Text.RegularExpressions.Regex.Matches(extractedJson, pattern);
                
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        string question = match.Groups[1].Value.Trim();
                        questionsList.Add(question);
                    }
                }
                
                if (questionsList.Count > 0)
                {
                    Debug.Log($"[SpeechRecorder] Fallback parsing extracted {questionsList.Count} questions");
                }
                else
                {
                    Debug.LogWarning("[SpeechRecorder] Fallback parsing also failed to extract questions");
                    
                    // Last resort - create a generic question list
                    questionsList = new List<string>
                    {
                        "What's this used for?",
                        "Why is this important?",
                        "Show me similar items?",
                        "How does this work?",
                        "Where can I find more?"
                    };
                    Debug.Log("[SpeechRecorder] Using generic fallback questions");
                }
            }
            catch (Exception fallbackEx)
            {
                Debug.LogError($"[SpeechRecorder] Fallback parsing also failed: {fallbackEx.Message}");
                yield break;
            }
        }
        
        // Create UI elements for each question
        if (questionsList != null && questionsList.Count > 0 && infoPanel != null)
        {
            float currentY = -60f;  // Start at the top
            float questionHeight = 54f;  // Height of each question block, adjust as needed
            float spacing = 0f;  // Space between questions
            
            foreach (var q in questionsList)
            {
                // Skip empty questions
                if (string.IsNullOrWhiteSpace(q)) continue;
                
                // Instantiate the question prefab
                GameObject go = Instantiate(questionPrefab, infoPanel);
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
                            Debug.LogWarning("[SpeechRecorder] No GeminiQuestionAnswerer component assigned!");
                        }
                    };
                }
                else
                {
                    Debug.LogWarning("[SpeechRecorder] Question prefab is missing SpatialUIButton component.");
                }
                
                // Store the created question object for cleanup later
                createdQuestions.Add(go);
            }
            
            Debug.Log($"[SpeechRecorder] Created {createdQuestions.Count} question UI elements");
        }
    }

    /// <summary>
    /// Clears any previously created question UI elements
    /// </summary>
    private void ClearPreviousQuestions()
    {
        foreach (GameObject question in createdQuestions)
        {
            if (question != null)
            {
                Destroy(question);
            }
        }
        
        createdQuestions.Clear();
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

    public void ClearResponseText()
    {
        if (responseTextOnObject != null)
        {
            responseTextOnObject.text = "";
        }
        
        // Cancel any existing hide timer when manually clearing
        if (responseHideCoroutine != null)
        {
            StopCoroutine(responseHideCoroutine);
            responseHideCoroutine = null;
        }
    }

    public void HideChatbox()
    {
        if (chatboxOnObject != null)
        {
            chatboxOnObject.SetActive(false);
        }
        
        // Also clear the request text when hiding the chatbox
        if (requestText != null)
        {
            requestText.text = "";
        }
        
        // Hide the questions panel
        if (infoPanel != null)
        {
            infoPanel.gameObject.SetActive(false);
        }
        
        // Clear any created questions
        ClearPreviousQuestions();
        
        // Hide the answer panel if it exists
        if (answerPanel != null)
        {
            answerPanel.SetActive(false);
        }
        
        // Cancel any existing hide timer when manually hiding
        if (responseHideCoroutine != null)
        {
            StopCoroutine(responseHideCoroutine);
            responseHideCoroutine = null;
        }
    }

    // Add this method at the end of the class
    public void ForceShowChatbox()
    {
        if (chatboxOnObject != null)
        {
            Debug.Log($"[SpeechRecorder] Force-showing chatboxOnObject. Current state: {chatboxOnObject.activeSelf}");
            chatboxOnObject.SetActive(true);
            
            // Try to ensure the parent canvas is refreshed
            Canvas parentCanvas = chatboxOnObject.GetComponentInParent<Canvas>();
            if (parentCanvas != null)
            {
                Debug.Log("[SpeechRecorder] Refreshing parent canvas");
                parentCanvas.enabled = false;
                parentCanvas.enabled = true;
            }
        }
        else
        {
            Debug.LogWarning("[SpeechRecorder] Cannot force-show chatboxOnObject because reference is null");
        }
    }

    // New coroutine to auto-hide the response UI after a delay
    private IEnumerator AutoHideResponseAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        Debug.Log($"[SpeechRecorder] Auto-hiding response UI after {delay} seconds");
        
        // Explicitly clear all visual elements first, but only if they were created via voice interactions
        // (relationships created by manual anchor toggling won't be affected)
        if (relationshipLineManager != null)
        {
            // Only clear visual elements if they're set to auto-hide (voice activated)
            if (relationshipLineManager.hasActiveHighlights)
            {
                relationshipLineManager.ClearAllHighlightsAndLines();
                relationshipLineManager.ClearAllStepIndicators();
                // Reset the internal state
                relationshipLineManager.hasActiveHighlights = false;
                relationshipLineManager.highlightTimer = 0f;
                Debug.Log("[SpeechRecorder] Cleared relationship lines and highlights that were set to auto-hide");
            }
            else
            {
                // If hasActiveHighlights is false, these were likely created by manual anchor toggling
                Debug.Log("[SpeechRecorder] Leaving relationship lines intact as they weren't set to auto-hide");
            }
        }
        
        // Hide the chatbox
        if (chatboxOnObject != null)
        {
            chatboxOnObject.SetActive(false);
        }
        
        // Clear the request text
        if (requestText != null)
        {
            requestText.text = "";
        }
        
        // Also clear the response text
        if (responseTextOnObject != null)
        {
            responseTextOnObject.text = "";
        }
        
        // Hide the questions panel
        if (infoPanel != null)
        {
            infoPanel.gameObject.SetActive(false);
        }
        
        // Clear any created questions
        ClearPreviousQuestions();
        
        // Hide the answer panel if it exists
        if (answerPanel != null)
        {
            answerPanel.SetActive(false);
        }
        
        responseHideCoroutine = null;
    }

    /// <summary>
    /// Positions the menu canvas appropriately in the scene, similar to SurfaceScanOCR
    /// </summary>
    private void PositionInfoPanelParent()
    {
        if (infoPanelParent == null) return;
        
        infoPanelParent.SetParent(null);
        
        LazyFollow lazyFollow = infoPanelParent.GetComponent<LazyFollow>();
        if (lazyFollow != null)
        {
            lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;
        }

        if (infoPanel != null)
        {
            infoPanel.gameObject.SetActive(true);
        }
    }

    /// <summary>
    /// Specialized version of TryExtractJson for question arrays, with better handling of newlines
    /// </summary>
    private string TryExtractQuestionJson(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
            
        Debug.Log($"[SpeechRecorder] Raw question response: {text}");
            
        // Extract from Gemini root structure if present
        try
        {
            var root = JsonConvert.DeserializeObject<GeminiRoot>(text);
            if (root?.candidates != null && root.candidates.Count > 0)
            {
                string rawText = root.candidates[0].content.parts[0].text;
                text = rawText; // Update text to the extracted content
                Debug.Log($"[SpeechRecorder] Extracted from Gemini response: {text}");
            }
        }
        catch
        {
            // Continue with the text as-is if we can't parse the root structure
        }
            
        // Check for ```json blocks
        if (text.Contains("```json"))
        {
            var parts = text.Split(new[] { "```json" }, StringSplitOptions.None);
            if (parts.Length > 1)
            {
                var codeBlock = parts[1].Split(new[] { "```" }, StringSplitOptions.None)[0];
                text = codeBlock;
                Debug.Log($"[SpeechRecorder] Extracted from code block: {text}");
            }
        }
        // Check for general code blocks
        else if (text.Contains("```"))
        {
            var parts = text.Split(new[] { "```" }, StringSplitOptions.None);
            if (parts.Length > 1)
            {
                text = parts[1];
                Debug.Log($"[SpeechRecorder] Extracted from general code block: {text}");
            }
        }
            
        // Find array brackets if present
        int startBracket = text.IndexOf('[');
        int endBracket = text.LastIndexOf(']');
        if (startBracket >= 0 && endBracket > startBracket)
        {
            text = text.Substring(startBracket, endBracket - startBracket + 1);
            Debug.Log($"[SpeechRecorder] Extracted array content: {text}");
        }
            
        // Clean the text - remove any leading/trailing whitespace and handle escaped newlines
        text = text.Trim();
        
        // Remove all escaped newlines and replace with spaces where needed
        text = text.Replace("\\n", " ");
        
        // Remove any literal newlines that might be in the JSON string
        text = text.Replace("\n", "").Replace("\r", "");
        
        Debug.Log($"[SpeechRecorder] Final cleaned JSON: {text}");
        
        // Validate by attempting to deserialize
        try
        {
            var testParse = JsonConvert.DeserializeObject<List<string>>(text);
            if (testParse != null)
            {
                Debug.Log($"[SpeechRecorder] Successfully validated JSON with {testParse.Count} items");
                return text;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SpeechRecorder] Validation failed: {ex.Message}");
        }
        
        return text; // Return the processed text even if validation failed - sometimes the parser is too strict
    }

    // Add a new class for instruction steps
    [System.Serializable]
    private class InstructionStep
    {
        public int order;
        public string @object;
    }

    // New method to process instructions
    private void ProcessInstructions(List<InstructionStep> instructionSteps, List<SceneObjectAnchor> allAnchors, string originalQuery, string instructionMessage)
    {
        if (instructionSteps == null || instructionSteps.Count == 0)
        {
            Debug.Log("[SpeechRecorder] No instruction steps to process");
            LogUserStudy("[VOICE_INPUT] [ENV] PROCESS_RESULT: Type=\"instruction\", Status=\"no_steps_found\"");
            return;
        }
        
        Debug.Log($"[SpeechRecorder] Processing {instructionSteps.Count} instruction steps");
        
        // Sort the steps by order to ensure correct sequence
        instructionSteps = instructionSteps.OrderBy(step => step.order).ToList();
        
        // Build detailed step information for user study logging
        StringBuilder stepsInfo = new StringBuilder();
        stepsInfo.Append($"Count={instructionSteps.Count}");
        
        // Log details of each step for debugging
        for (int i = 0; i < instructionSteps.Count; i++)
        {
            var step = instructionSteps[i];
            Debug.Log($"[SpeechRecorder] Step {step.order}: {step.@object}");
            stepsInfo.Append($", Step{step.order}=\"{step.@object}\"");
        }
        
        // Log instruction steps details for user study
        string sanitizedMessage = instructionMessage?.Replace("\n", " ").Replace("\r", "") ?? "No message";
        LogUserStudy($"[VOICE_INPUT] [ENV] PROCESS_RESULT: Type=\"instruction\", {stepsInfo.ToString()}, DisplayedMessage=\"{sanitizedMessage}\"");
        
        // Clear any existing highlights or relationship lines
        relationshipLineManager.ClearAllHighlightsAndLines();
        
        // Keep track of anchors we find for each step in the proper sequence
        List<SceneObjectAnchor> stepAnchors = new List<SceneObjectAnchor>();
        
        // Find and process each object in the instruction steps
        foreach (var step in instructionSteps)
        {
            // Find matching anchors for this object
            List<SceneObjectAnchor> matchingAnchors = FindMatchingAnchors(step.@object, allAnchors);
            
            foreach (var anchor in matchingAnchors)
            {
                if (anchor != null && anchor.sphereObj != null)
                {
                    // Create a step number indicator above the object
                    CreateStepNumberIndicator(anchor, step.order);
                    
                    // Also highlight the object
                    HighlightObject(anchor);
                    
                    // Store this anchor for line connections
                    stepAnchors.Add(anchor);
                    
                    Debug.Log($"[SpeechRecorder] Added step {step.order} indicator to {anchor.label}");
                    
                    // We only need one matching anchor per step
                    break;
                }
            }
            
            if (matchingAnchors.Count == 0)
            {
                Debug.LogWarning($"[SpeechRecorder] Could not find any objects matching: {step.@object} for step {step.order}");
            }
        }
        
        // Draw lines between the steps to show the sequence
        if (stepAnchors.Count >= 2 && relationshipLineManager != null)
        {
            // Create lines to connect each step to the next one in sequence
            for (int i = 0; i < stepAnchors.Count - 1; i++)
            {
                var sourceAnchor = stepAnchors[i];
                var targetAnchor = stepAnchors[i + 1];
                
                // Create line between these two steps
                Dictionary<string, string> connection = new Dictionary<string, string>
                {
                    { targetAnchor.label, "" } // Empty string for no text on the line
                };
                
                // Draw the connection line
                relationshipLineManager.ShowRelationships(sourceAnchor, connection, allAnchors, true);
                
                Debug.Log($"[SpeechRecorder] Connected step {i+1} ({sourceAnchor.label}) to step {i+2} ({targetAnchor.label})");
            }
        }
        
        // Create a response message for display
        if (instructionSteps.Count > 0)
        {
            // Use the original message from Gemini instead of constructing a new one
            if (responseTextOnObject != null)
            {
                responseTextOnObject.text = instructionMessage;
            }
            
            // Make sure chatbox is shown
            if (chatboxOnObject != null && !chatboxOnObject.activeSelf)
            {
                Debug.Log("[SpeechRecorder] Activating chatbox to show instruction message from Gemini");
                chatboxOnObject.SetActive(true);
                
                // Force update to ensure the object is visible
                if (chatboxOnObject.transform.parent != null)
                {
                    Canvas canvas = chatboxOnObject.GetComponentInParent<Canvas>();
                    if (canvas != null)
                    {
                        canvas.enabled = false;
                        canvas.enabled = true;
                    }
                }
            }
        }
        
        // Generate follow-up questions based on the instructions
        GenerateQuestionsAfterProcessJSON(originalQuery, instructionSteps, instructionMessage);
    }

    // Helper method to create a step number indicator above an object
    private void CreateStepNumberIndicator(SceneObjectAnchor anchor, int stepNumber)
    {
        if (stepIndicatorPrefab == null)
        {
            Debug.LogError("[SpeechRecorder] Cannot create step indicator: Missing step indicator prefab");
            return;
        }

        // Use the dedicated step indicator prefab
        GameObject stepIndicator = Instantiate(stepIndicatorPrefab);
        stepIndicator.name = $"StepIndicator_{stepNumber}";
        
        // Position it slightly above the anchor object
        stepIndicator.transform.SetParent(anchor.sphereObj.transform);
        stepIndicator.transform.localPosition = new Vector3(0f, 2.5f, 0f);
        stepIndicator.transform.localRotation = Quaternion.identity;
        
        // Get the TextMeshPro component and update its text to show the step number
        TextMeshPro tmpText = stepIndicator.GetComponentInChildren<TextMeshPro>();
        if (tmpText != null)
        {
            // Just change the text content, don't modify font size or other properties
            tmpText.text = stepNumber.ToString();
            
            // Use the consistent blue highlight color (hex: #2096F3 with 100% alpha)
            Color highlightColor = new Color(
                r: 0.125f,  // 32/255
                g: 0.588f,  // 150/255
                b: 0.953f,  // 243/255
                a: 1.0f     // 100% alpha
            );
            
            // Try to find and update the background if it exists
            Renderer bgRenderer = stepIndicator.GetComponent<Renderer>();
            if (bgRenderer != null && bgRenderer.material != null)
            {
                bgRenderer.material.color = highlightColor;
            }
        }
        else
        {
            Debug.LogWarning($"[SpeechRecorder] Could not find TextMeshPro component in step indicator prefab");
        }
        
        // Add the step indicator to the relationship line manager to track and clean up later
        if (relationshipLineManager != null)
        {
            relationshipLineManager.AddStepIndicator(stepIndicator);
        }
        
        Debug.Log($"[SpeechRecorder] Added step {stepNumber} indicator to {anchor.label}");
    }

    // Helper method to highlight an object
    private void HighlightObject(SceneObjectAnchor anchor)
    {
        if (anchor == null || anchor.sphereObj == null)
            return;
        
        // Use the consistent blue highlight color (hex: #2096F3 with 100% alpha)
        Color highlightColor = new Color(
            r: 0.125f,  // 32/255
            g: 0.588f,  // 150/255
            b: 0.953f,  // 243/255
            a: 1.0f     // 100% alpha
        );
        
        // Highlight the sphere with the blue color
        var renderer = anchor.sphereObj.GetComponent<Renderer>();
        if (renderer != null && renderer.material != null)
        {
            renderer.material.color = highlightColor;
        }

        // Highlight the child label object with the same blue color
        var labelObj = anchor.sphereObj.transform.GetChild(0)?.gameObject;
        if (labelObj != null)
        {
            var labelRenderer = labelObj.GetComponent<Renderer>();
            if (labelRenderer != null && labelRenderer.material != null)
            {
                labelRenderer.material.color = highlightColor;
            }
        }
    }

    // Helper method for creating timestamped user study logs
    protected void LogUserStudy(string message)
    {
        if (!enableUserStudyLogging) return;
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
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