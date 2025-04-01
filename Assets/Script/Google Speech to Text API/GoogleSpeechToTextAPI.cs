using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
#if !UNITY_VISIONOS
using Microsoft.CSharp; // Required for dynamic type binding
#endif
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Plain C# class (NOT a MonoBehaviour) for making synchronous Speech-to-Text requests.
/// You do NOT attach this to a GameObject directly.
/// </summary>
public class GoogleSpeechToTextAPI
{
    private readonly HttpClient _httpClient;
    private readonly string _baseEndpoint;

    private ConcurrentDictionary<string, Task<string>> _activeRequests
        = new ConcurrentDictionary<string, Task<string>>();

    public GoogleSpeechToTextAPI(string apiKey)
    {
        _httpClient = new HttpClient();
        // Synchronous recognition endpoint with API key
        _baseEndpoint = $"https://speech.googleapis.com/v1/speech:recognize?key={apiKey}";
    }

    public Task<string> TranscribeAudio(
        byte[] audioData,
        string languageCode = "en-US",
        string encoding = "LINEAR16",
        int sampleRateHertz = 16000,
        bool enableAutomaticPunctuation = true)
    {
        string requestId = Guid.NewGuid().ToString();
        var requestTask = MakeApiRequest(
            requestId,
            audioData,
            languageCode,
            encoding,
            sampleRateHertz,
            enableAutomaticPunctuation
        );

        _activeRequests[requestId] = requestTask;

        return requestTask.ContinueWith(t =>
        {
            _activeRequests.TryRemove(requestId, out _);
            return t.Result;
        });
    }

    private async Task<string> MakeApiRequest(
        string requestId,
        byte[] audioData,
        string languageCode,
        string encoding,
        int sampleRateHertz,
        bool enableAutomaticPunctuation)
    {
        try 
        {
            // Check for empty audio data
            if (audioData == null || audioData.Length == 0)
            {
                Debug.LogError("[STT] Audio data is empty or null");
                return "{\"error\": \"Empty audio data\"}";
            }
            
            // Check for oversized request (Google STT API has a 10MB limit)
            const int maxAudioBytes = 10 * 1024 * 1024; // 10MB
            if (audioData.Length > maxAudioBytes)
            {
                Debug.LogError($"[STT] Audio data exceeds maximum size: {audioData.Length} bytes > {maxAudioBytes} bytes");
                return "{\"error\": \"Audio data exceeds maximum size of 10MB\"}";
            }

            // Convert audio bytes to base64
            string base64Audio = Convert.ToBase64String(audioData);

            // Build the request body
            var requestData = new
            {
                config = new
                {
                    languageCode = languageCode,
                    encoding = encoding,
                    sampleRateHertz = sampleRateHertz,
                    enableAutomaticPunctuation = enableAutomaticPunctuation,
                    model = "default" // Add model parameter
                },
                audio = new
                {
                    content = base64Audio
                }
            };

            string payloadJson = JsonConvert.SerializeObject(requestData);

            Debug.Log($"[STT] Request {requestId}: Sending {audioData.Length} bytes of audio data to Google STT API");
            
            using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            try
            {
                // Set a reasonable timeout
                var cts = new System.Threading.CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30 second timeout
                
                var response = await _httpClient.PostAsync(_baseEndpoint, content, cts.Token);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Debug.LogError(
                        $"[STT] Request {requestId} failed with {response.StatusCode}\n{responseString}");
                    
                    // Try to extract a more detailed error message from the response
                    try
                    {
                        var errorResponse = JsonConvert.DeserializeObject<JObject>(responseString);
                        if (errorResponse != null && errorResponse["error"] != null && errorResponse["error"]["message"] != null)
                        {
                            string errorMsg = errorResponse["error"]["message"].ToString();
                            Debug.LogError($"[STT] API Error: {errorMsg}");
                            
                            // Check for common errors
                            if (errorMsg.Contains("API key"))
                            {
                                Debug.LogError("[STT] API key issue detected. Please check your API key validity and quota.");
                            }
                            else if (errorMsg.Contains("quota"))
                            {
                                Debug.LogError("[STT] Quota exceeded error detected. Check your Google Cloud quota limits.");
                            }
                            
                            // Return formatted error that preserves the error message but will not be parsed as a transcription
                            return $"{{\"error\": \"{errorMsg}\"}}";
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[STT] Failed to parse error response: {ex.Message}");
                    }
                }
                else
                {
                    // Validate that we have some results in the response
                    if (!responseString.Contains("results"))
                    {
                        Debug.LogWarning($"[STT] Response may be invalid - no 'results' field found: {responseString}");
                    }
                    else
                    {
                        Debug.Log($"[STT] Request {requestId} completed successfully");
                    }
                }

                return responseString;
            }
            catch (TaskCanceledException)
            {
                Debug.LogError($"[STT] Request {requestId} timed out after 30 seconds");
                return "{\"error\": \"Request timed out\"}";
            }
            catch (Exception ex)
            {
                Debug.LogError($"[STT] Network exception on request {requestId}: {ex.Message}");
                return $"{{\"error\": \"{ex.Message}\"}}";
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[STT] Exception preparing request {requestId}: {ex.Message}\n{ex.StackTrace}");
            return $"{{\"error\": \"{ex.Message}\"}}";
        }
    }

    public int GetActiveRequestCount()
    {
        return _activeRequests.Count;
    }
}
