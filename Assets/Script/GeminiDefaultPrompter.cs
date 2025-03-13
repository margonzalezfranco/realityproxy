using System;
using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// Specialized implementation for generating responses using Gemini API with a default prompt.
/// Takes an image and input text, applies it to a template prompt, and returns a text response.
/// </summary>
public class GeminiDefaultPrompter : GeminiGeneral
{
    [Header("Prompt Settings")]
    [Tooltip("Default prompt template to use. Use {0} where the input text should be inserted")]
    [TextArea(3, 10)]
    public string defaultPromptTemplate = "Given the image, please respond to this: {0}";

    [Header("UI Settings")]
    [Tooltip("Optional - TextMeshPro component to display the response")]
    public TextMeshPro responseText;

    [Tooltip("Optional - Event to be called when response is received")]
    public UnityEngine.Events.UnityEvent<string> onResponseReceived;

    // Property to store the last response
    public string LastResponse { get; private set; }

    /// <summary>
    /// Request a response from Gemini using the default prompt template with the provided text
    /// </summary>
    /// <param name="inputText">The text to insert into the default prompt template</param>
    public void RequestResponse(string inputText)
    {
        StartCoroutine(ResponseRoutine(defaultPromptTemplate, inputText, null));
    }

    /// <summary>
    /// Request a response from Gemini using a custom prompt template with the provided text
    /// </summary>
    /// <param name="promptTemplate">Custom prompt template to use instead of the default</param>
    /// <param name="inputText">The text to insert into the prompt template</param>
    public void RequestResponse(string promptTemplate, string inputText)
    {
        StartCoroutine(ResponseRoutine(promptTemplate, inputText, null));
    }

    /// <summary>
    /// Request a response from Gemini with a callback to receive the result directly
    /// </summary>
    /// <param name="promptTemplate">Custom prompt template to use</param>
    /// <param name="inputText">The text to insert into the prompt template</param>
    /// <param name="callback">Action that will be called with the response text</param>
    public void RequestResponseWithCallback(string promptTemplate, string inputText, Action<string> callback)
    {
        StartCoroutine(ResponseRoutine(promptTemplate, inputText, callback));
    }

    /// <summary>
    /// Request a response from Gemini with the default template and a callback
    /// </summary>
    /// <param name="inputText">The text to insert into the default prompt template</param>
    /// <param name="callback">Action that will be called with the response text</param>
    public void RequestResponseWithCallback(string inputText, Action<string> callback)
    {
        StartCoroutine(ResponseRoutine(defaultPromptTemplate, inputText, callback));
    }

    private IEnumerator ResponseRoutine(string promptTemplate, string inputText, Action<string> callback)
    {
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);

        // 2) Convert to base64 (PNG)
        string base64Image = ConvertTextureToBase64(frameTex);

        // 3) Format the prompt using the template and input text
        string formattedPrompt = string.Format(promptTemplate, inputText);

        // 4) Call Gemini with the formatted prompt
        // This uses the RequestStatus system which supports concurrent API calls
        var request = MakeGeminiRequest(formattedPrompt, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        string response = request.Result;

        Debug.Log($"Input: {inputText}\nPrompt: {formattedPrompt}\nResponse: {response}");

        // 5) Parse the response
        string parsedResponse = ParseResponse(response);
        
        // Store the last response
        LastResponse = parsedResponse;
        
        // 6) Update UI if available
        if (responseText != null && !string.IsNullOrEmpty(parsedResponse))
        {
            responseText.text = parsedResponse;
        }

        // 7) Invoke the callback if provided
        callback?.Invoke(parsedResponse);

        // 8) Invoke event with the response
        onResponseReceived?.Invoke(parsedResponse);

        // Clean up texture
        Destroy(frameTex);
    }

    private string ParseResponse(string response)
    {
        try
        {
            string parsedText = ParseGeminiRawResponse(response);
            if (string.IsNullOrEmpty(parsedText)) return "Failed to parse response";

            // Remove any markdown formatting if present
            parsedText = parsedText.Trim();
            if (parsedText.StartsWith("```") && parsedText.EndsWith("```"))
            {
                parsedText = parsedText.Substring(3, parsedText.Length - 6).Trim();
            }

            return parsedText;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error parsing response: {ex}");
            return "Error parsing response";
        }
    }
} 