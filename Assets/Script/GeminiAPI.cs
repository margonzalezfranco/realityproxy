using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using System;
using System.Threading;

/// <summary>
/// A simple REST client for calling Google's Gemini model to generate content.
/// Adjust the JSON structure and fields as needed to match the Gemini API docs.
/// </summary>
public class GeminiAPI
{
    private readonly string _baseEndpoint;
    private readonly string _modelName;
    private readonly string _apiKey;
    
    // Debug flag
    public bool EnableDebugLogging = false;
    
    // Retry settings
    private int _maxRetries = 2;
    private float _retryDelaySeconds = 0.5f;

    // Provide your own model name and key here
    // e.g., "gemini-2.0-flash"
    // https://generativelanguage.googleapis.com/v1beta/models/YOUR_MODEL:generateContent?key=YOUR_KEY
    public GeminiAPI(string modelName, string apiKey)
    {
        _modelName = modelName;
        _apiKey = apiKey;
        
        // We'll put the key in the query string. Alternatively you can put it in request headers if the API requires it differently.
        _baseEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
        
        if (EnableDebugLogging)
        {
            Debug.Log($"[GeminiAPI] Initialized with model: {modelName}");
            Debug.Log($"[GeminiAPI] Endpoint: {_baseEndpoint.Substring(0, _baseEndpoint.IndexOf("?"))}");
        }
    }

    /// <summary>
    /// Send a "generateContent" request to the Gemini model with a user prompt.
    /// If you need inline images or extra config, adjust the 'parts' and request body.
    /// </summary>
    public async Task<string> GenerateContent(string userPrompt, string base64Image = null)
    {
        if (EnableDebugLogging)
        {
            Debug.Log($"[GeminiAPI] Starting API request at {DateTime.Now.ToString("HH:mm:ss.fff")}");
            Debug.Log($"[GeminiAPI] Prompt length: {userPrompt.Length} chars, Image data: {(base64Image != null ? base64Image.Length : 0)} chars");
        }
        
        // Create a new HttpClient for each request to avoid issues with reusing clients
        using (var httpClient = new HttpClient())
        {
            // Set a timeout that's less than our coroutine timeout
            httpClient.Timeout = TimeSpan.FromSeconds(6);
            
            // Add retry logic
            for (int retryCount = 0; retryCount <= _maxRetries; retryCount++)
            {
                try
                {
                    if (retryCount > 0 && EnableDebugLogging)
                    {
                        Debug.Log($"[GeminiAPI] Retry attempt {retryCount} of {_maxRetries}");
                    }
                    
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
                            temperature = 0.7f,
                            maxOutputTokens = 1024,
                            topP = 0.95f,
                            topK = 40
                        }
                    };

                    // Convert to JSON
                    string payloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(requestData);
                    
                    if (EnableDebugLogging)
                    {
                        Debug.Log($"[GeminiAPI] Request payload size: {payloadJson.Length} bytes");
                    }

                    using (var content = new StringContent(payloadJson, Encoding.UTF8, "application/json"))
                    {
                        // Add request headers
                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                        
                        if (EnableDebugLogging)
                        {
                            Debug.Log($"[GeminiAPI] Sending HTTP request...");
                        }

                        // Create a cancellation token with our timeout
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                        {
                            // POST to the Gemini model endpoint with cancellation token
                            var response = await httpClient.PostAsync(_baseEndpoint, content, cts.Token);
                            
                            if (EnableDebugLogging)
                            {
                                Debug.Log($"[GeminiAPI] Received response with status code: {response.StatusCode}");
                            }
                            
                            var responseString = await response.Content.ReadAsStringAsync();

                            if (!response.IsSuccessStatusCode)
                            {
                                // Check if we should retry based on status code
                                if (ShouldRetry((int)response.StatusCode) && retryCount < _maxRetries)
                                {
                                    Debug.LogWarning($"[GeminiAPI] Request failed with code {response.StatusCode}, retrying...");
                                    await Task.Delay((int)(_retryDelaySeconds * 1000 * (retryCount + 1)));
                                    continue;
                                }
                                
                                Debug.LogError($"[GeminiAPI] Request failed with code {response.StatusCode}\nResponse: {responseString}");
                                return null;
                            }
                            
                            if (EnableDebugLogging)
                            {
                                Debug.Log($"[GeminiAPI] Response received at {DateTime.Now.ToString("HH:mm:ss.fff")}, length: {responseString.Length} chars");
                            }

                            return responseString;
                        }
                    }
                }
                catch (TaskCanceledException ex)
                {
                    if (retryCount < _maxRetries)
                    {
                        Debug.LogWarning($"[GeminiAPI] Request timed out, retrying ({retryCount + 1}/{_maxRetries})...");
                        await Task.Delay((int)(_retryDelaySeconds * 1000 * (retryCount + 1)));
                        continue;
                    }
                    
                    Debug.LogError($"[GeminiAPI] Request timed out after all retries: {ex.Message}");
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    if (retryCount < _maxRetries)
                    {
                        Debug.LogWarning($"[GeminiAPI] HTTP request error, retrying ({retryCount + 1}/{_maxRetries}): {ex.Message}");
                        await Task.Delay((int)(_retryDelaySeconds * 1000 * (retryCount + 1)));
                        continue;
                    }
                    
                    Debug.LogError($"[GeminiAPI] HTTP request error after all retries: {ex.Message}");
                    return null;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[GeminiAPI] Unexpected error: {ex.Message}");
                    Debug.LogException(ex);
                    return null;
                }
            }
            
            // If we get here, all retries failed
            Debug.LogError("[GeminiAPI] All retry attempts failed");
            return null;
        }
    }
    
    private bool ShouldRetry(int statusCode)
    {
        // Retry on these status codes:
        // 408 Request Timeout
        // 429 Too Many Requests
        // 500 Internal Server Error
        // 502 Bad Gateway
        // 503 Service Unavailable
        // 504 Gateway Timeout
        return statusCode == 408 || statusCode == 429 || 
               statusCode == 500 || statusCode == 502 || 
               statusCode == 503 || statusCode == 504;
    }
}
