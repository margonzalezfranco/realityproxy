using System;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using System.Text;

/// <summary>
/// MonoBehaviour that wraps our GoogleSpeechToTextAPI usage.
/// This is the script you attach to a GameObject.
/// </summary>
public class SpeechToTextGeneral : MonoBehaviour
{
    [Header("Google Speech-to-Text Settings")]
    public string speechApiKey = "YOUR_SPEECH_API_KEY";
    public string languageCode = "en-US";
    public string encoding = "LINEAR16";
    public int sampleRateHertz = 16000;
    public bool enableAutomaticPunctuation = true;

    private GoogleSpeechToTextAPI _speechClient;

    // Simple class to track the status of a request
    public class RequestStatus
    {
        public bool IsCompleted { get; private set; }
        public string Result { get; private set; }
        public Exception Error { get; private set; }

        private Task<string> _task;

        public RequestStatus(Task<string> task)
        {
            _task = task;
            _task.ContinueWith(t =>
            {
                IsCompleted = true;
                if (t.IsFaulted)
                {
                    Error = t.Exception;
                }
                else
                {
                    Result = t.Result;
                }
            });
        }
    }

    private void Awake()
    {
        // Instantiate the non-MonoBehaviour Speech client
        _speechClient = new GoogleSpeechToTextAPI(speechApiKey);
    }

    /// <summary>
    /// Public method to request transcription from a raw audio byte array.
    /// </summary>
    public RequestStatus TranscribeAudio(byte[] audioData)
    {
        var task = _speechClient.TranscribeAudio(
            audioData,
            languageCode,
            encoding,
            sampleRateHertz,
            enableAutomaticPunctuation
        );
        return new RequestStatus(task);
    }

    /// <summary>
    /// Utility method to parse the first recognized transcript from the JSON response.
    /// </summary>
    public static string ParseTranscriptionResult(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            var data = JsonConvert.DeserializeObject<SpeechResponse>(json);
            if (data?.results == null || data.results.Length == 0)
                return null;

            // Google Speech API can split responses into multiple "results" segments
            // We need to concatenate all of them to get the full transcription
            StringBuilder fullTranscript = new StringBuilder();
            
            foreach (var result in data.results)
            {
                if (result.alternatives != null && result.alternatives.Length > 0)
                {
                    // Use the alternative with the highest confidence (always the first one)
                    string transcript = result.alternatives[0].transcript;
                    
                    // Only add a space between segments if needed
                    if (fullTranscript.Length > 0 && 
                        !fullTranscript.ToString().EndsWith(" ") && 
                        !transcript.StartsWith(" "))
                    {
                        fullTranscript.Append(" ");
                    }
                    
                    fullTranscript.Append(transcript);
                }
            }
            
            Debug.Log($"Parsed transcript with {data.results.Length} segments: \"{fullTranscript}\"");
            return fullTranscript.ToString().Trim();
        }
        catch (Exception ex)
        {
            Debug.LogError("Error parsing STT JSON: " + ex.Message);
            return null;
        }
    }

    [Serializable]
    public class SpeechResponse
    {
        public SpeechResult[] results;
    }

    [Serializable]
    public class SpeechResult
    {
        public SpeechAlternative[] alternatives;
    }

    [Serializable]
    public class SpeechAlternative
    {
        public string transcript;
        public float confidence;
    }
}
