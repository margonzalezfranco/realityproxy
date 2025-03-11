using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// A simple REST client for calling Google's Gemini model to generate content.
/// Adjust the JSON structure and fields as needed to match the Gemini API docs.
/// </summary>
public class GeminiAPI
{
    private readonly HttpClient _httpClient;
    private readonly string _baseEndpoint;

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
    /// If you need inline images or extra config, adjust the 'parts' and request body.
    /// </summary>
    public async Task<string> GenerateContent(string userPrompt, string base64Image = null)
    {
        // Build the request data. 
        // Adjust to match your bounding box prompt structure if needed.
        // For example, if you want to embed an inline image, you might do something like:
        //   parts: [ { text = userPrompt },
        //            { inlineData = { data = base64Image, mimeType="image/png" }} ]

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
        // For more complex scenarios, you may prefer a more flexible library than JsonUtility.
        string payloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(requestData);

        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        // POST to the Gemini model endpoint
        var response = await _httpClient.PostAsync(_baseEndpoint, content);
        var responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Debug.LogError($"Gemini API request failed with code {response.StatusCode}\nResponse: {responseString}");
        }

        return responseString;
    }
}
