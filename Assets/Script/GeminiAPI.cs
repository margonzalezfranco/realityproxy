using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using System.Collections.Generic;
using System.Collections.Concurrent;

/// <summary>
/// A simple REST client for calling Google's Gemini model to generate content.
/// Supports concurrent API calls from multiple components.
/// </summary>
public class GeminiAPI
{
    private readonly HttpClient _httpClient;
    private readonly string _baseEndpoint;
    
    // Track active requests to ensure they don't interfere with each other
    private ConcurrentDictionary<string, Task<string>> _activeRequests = new ConcurrentDictionary<string, Task<string>>();

    // Provide your own model name and key here
    // e.g., "gemini-2.0-flash"
    // https://generativelanguage.googleapis.com/v1beta/models/YOUR_MODEL:generateContent?key=YOUR_KEY
    public GeminiAPI(string modelName, string apiKey)
    {
        _httpClient = new HttpClient();
        // We'll put the key in the query string. Alternatively you can put it in request headers if the API requires it differently.
        _baseEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
    }

    /// <summary>
    /// Send a "generateContent" request to the Gemini model with a user prompt.
    /// This method supports concurrent calls from multiple components.
    /// </summary>
    public Task<string> GenerateContent(string userPrompt, string base64Image = null)
    {
        // Generate a unique ID for this request
        string requestId = System.Guid.NewGuid().ToString();
        
        // Create a new task for this specific request
        var requestTask = MakeApiRequest(requestId, userPrompt, base64Image);
        
        // Store the task in our active requests dictionary
        _activeRequests[requestId] = requestTask;
        
        // Return a continuation task that will clean up after completion
        return requestTask.ContinueWith(task => {
            // Remove from active requests when done
            _activeRequests.TryRemove(requestId, out _);
            return task.Result;
        });
    }
    
    /// <summary>
    /// Internal method to make the actual API request
    /// </summary>
    private async Task<string> MakeApiRequest(string requestId, string userPrompt, string base64Image)
    {
        // Build the request data
        var requestData = new
        {
            contents = new[]
            {
                new
                {
                    parts = base64Image == null
                        ? new object[] { new { text = userPrompt } }
                        : new object[]
                        {
                            new { text = userPrompt },
                            new {
                                inlineData = new {
                                    data = base64Image,
                                    mimeType = "image/png"
                                }
                            }
                        }
                }
            },
            // Optional config
            generationConfig = new
            {
                temperature = 0.7f
            }
        };

        // Convert to JSON
        string payloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(requestData);

        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        try {
            // POST to the Gemini model endpoint
            var response = await _httpClient.PostAsync(_baseEndpoint, content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Debug.LogError($"Gemini API request {requestId} failed with code {response.StatusCode}\nResponse: {responseString}");
            }

            return responseString;
        }
        catch (System.Exception ex) {
            Debug.LogError($"Exception in Gemini API request {requestId}: {ex.Message}");
            return $"{{\"error\": \"{ex.Message}\"}}";
        }
    }
    
    /// <summary>
    /// Get the number of currently active requests
    /// </summary>
    public int GetActiveRequestCount()
    {
        return _activeRequests.Count;
    }
}
