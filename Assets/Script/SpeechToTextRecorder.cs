using System;
using UnityEngine.XR.Hands;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(SpeechToTextGeneral))]
public class SpeechToTextRecorder : GeminiGeneral
{
    [Header("Recording Settings")]
    [SerializeField] private bool isRecording = false;
    [SerializeField] private int recordingFrequency = 16000;
    [SerializeField] private int maxRecordingLength = 10; // in seconds
    [SerializeField] private string deviceName = null; // null = default microphone

    [Header("Prompt Settings")]
    [Tooltip("System prompt template to use. Use {0} where the transcribed text should be inserted")]
    [TextArea(3, 10)]
    [SerializeField] private string systemPromptTemplate = "You are a helpful AI assistant responding to the user's request. You can see what the user is currently looking at. Please respond concisely and avoid using markdown formatting. User request: {0}";

    [Header("Gesture Control")]
    [SerializeField] private bool useMiddlePinchControl = true;
    [Tooltip("Which hand to use for middle finger pinch control")]
    [SerializeField] private bool useLeftHand = true;

    [Header("Gemini Response")]
    [SerializeField] private GameObject geminiHandSphere;
    [SerializeField] private TMPro.TextMeshPro requestText;
    [SerializeField] private TMPro.TextMeshPro responseText;
    [SerializeField] private UnityEngine.Events.UnityEvent<string> onGeminiResponseReceived;
    [SerializeField] private GameObject chatbox;
    [Header("Debug")]
    [SerializeField] private AudioClip recordedAudio;
    [SerializeField] private string transcriptionResult;
    [SerializeField] private bool isProcessing = false;
    [SerializeField] private string geminiResponse;

    private SpeechToTextGeneral speechToText;
    private float recordingStartTime;
    private bool wasRecordingLastFrame = false;
    private XRHandSubsystem handSubsystem; // Cache the hand subsystem

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
        // Format the prompt using the template
        string formattedPrompt = string.Format(systemPromptTemplate, prompt);
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
            
            // 5) Update UI if available
            if (responseText != null && !string.IsNullOrEmpty(geminiResponse))
            {
                responseText.text = geminiResponse;
            }
            
            // 6) Invoke event with the response
            onGeminiResponseReceived?.Invoke(geminiResponse);
        }
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