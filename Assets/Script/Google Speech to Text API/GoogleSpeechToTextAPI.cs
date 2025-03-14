using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

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
                enableAutomaticPunctuation = enableAutomaticPunctuation
            },
            audio = new
            {
                content = base64Audio
            }
        };

        string payloadJson = JsonConvert.SerializeObject(requestData);

        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        try
        {
            var response = await _httpClient.PostAsync(_baseEndpoint, content);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Debug.LogError(
                    $"[STT] Request {requestId} failed with {response.StatusCode}\n{responseString}");
            }

            return responseString;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[STT] Exception on request {requestId}: {ex.Message}");
            return $"{{\"error\": \"{ex.Message}\"}}";
        }
    }

    public int GetActiveRequestCount()
    {
        return _activeRequests.Count;
    }
}
