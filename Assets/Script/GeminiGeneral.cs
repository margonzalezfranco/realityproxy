using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// Base class for Gemini API integration in Unity.
/// Handles common functionality like initialization, image capture, and response parsing.
/// </summary>
public class GeminiGeneral : MonoBehaviour
{
    [Header("Gemini Settings")]
    [Tooltip("Your model name, e.g. 'gemini-2.0-flash'")]
    public string geminiModelName = "gemini-2.0-flash";

    [Tooltip("Your API key")]
    public string geminiApiKey = "AIzaSyAoYU7ZM-AImpfA0faIBBz8ovLb_7n0QF4";

    [Header("Capture Settings")]
    [Tooltip("RenderTexture that displays the Vision Pro camera feed.")]
    public RenderTexture cameraRenderTex;

    // A reference to our API client
    protected GeminiAPI geminiClient;

    [Serializable]
    public class GeminiRoot
    {
        public List<Candidate> candidates;
        public UsageMetadata usageMetadata;
        public string modelVersion;
    }

    // candidates[0]
    [Serializable]
    public class Candidate
    {
        public Content content;
        public string finishReason;
        public List<SafetyRating> safetyRatings;
        public float avgLogprobs; 
    }

    [Serializable]
    public class Content
    {
        public List<Part> parts;
        public string role;
    }

    [Serializable]
    public class Part
    {
        public string text;
    }

    [Serializable]
    public class SafetyRating
    {
        public string category;
        public string probability;
    }

    [Serializable]
    public class UsageMetadata
    {
        public int promptTokenCount;
        public int candidatesTokenCount;
        public int totalTokenCount;
    }

    protected virtual void Awake()
    {
        // Initialize the Gemini client
        geminiClient = new GeminiAPI(geminiModelName, geminiApiKey);
    }

    /// <summary>
    /// Capture the current RenderTexture to a CPU Texture2D (PNG-encodable).
    /// </summary>
    protected Texture2D CaptureFrame(RenderTexture rt)
    {
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        return tex;
    }

    /// <summary>
    /// Encode to PNG and convert to Base64 string.
    /// </summary>
    protected string ConvertTextureToBase64(Texture2D tex)
    {
        var bytes = tex.EncodeToPNG();
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Parse the raw Gemini response into a GeminiRoot object
    /// </summary>
    protected string ParseGeminiRawResponse(string response)
    {
        try
        {
            var root = JsonConvert.DeserializeObject<GeminiRoot>(response);

            if (root?.candidates == null || root.candidates.Count == 0 
                || root.candidates[0].content?.parts == null || root.candidates[0].content.parts.Count == 0)
            {
                Debug.LogError("Gemini root structure incomplete, no candidates or content/parts found.");
                return null;
            }
            
            string textWithBackticks = root.candidates[0].content.parts[0].text ?? "";
            // Debug.Log("Gemini response text:\n" + textWithBackticks);

            if (textWithBackticks.Contains("```json"))
            {
                var splitted = textWithBackticks.Split(new[] { "```json" }, StringSplitOptions.None);
                if (splitted.Length > 1)
                {
                    var splitted2 = splitted[1].Split(new[] { "```" }, StringSplitOptions.None);
                    textWithBackticks = splitted2[0];
                }
            }

            return textWithBackticks;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error parsing Gemini response: {ex}");
            return null;
        }
    }
}