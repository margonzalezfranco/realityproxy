using System;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

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

            return data.results[0].alternatives[0].transcript;
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
