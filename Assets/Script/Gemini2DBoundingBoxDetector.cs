using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;
using TMPro;

/// <summary>
/// Demonstrates how to capture a camera frame from a RenderTexture, send it to Gemini,
/// get back 2D bounding boxes, and display them on a UI Canvas.
/// </summary>
public class Gemini2DBoundingBoxDetector : MonoBehaviour
{
    [Header("Gemini Settings")]
    [Tooltip("Your model name, e.g. 'gemini-2.0-flash-exp'")]
    public string geminiModelName = "gemini-2.0-flash-exp";

    [Tooltip("Your API key")]
    public string geminiApiKey = "AIzaSyAmRPcrP0JjOgeam_UqTugBAsQ21cnrenA";

    [Header("Capture Settings")]
    [Tooltip("RenderTexture that displays the Vision Pro camera feed.")]
    public RenderTexture cameraRenderTex;

    [Header("UI Settings")]
    [Tooltip("Canvas for drawing bounding box overlays (Screen Space - Overlay recommended).")]
    public Canvas overlayCanvas;

    [Tooltip("Prefab for bounding boxes (should have an Image + Text for label).")]
    public GameObject boundingBoxPrefab;

    [Tooltip("Coordinates might require scaling if Gemini boxes are in 1000-based coords, etc.")]
    public bool boxesAreInPixelCoords = true;

    // A reference to our API client
    private GeminiAPI geminiClient;

    public GeminiRaycast m_geminiRaycast;

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


    private void Awake()
    {
        // Initialize the Gemini client
        geminiClient = new GeminiAPI(geminiModelName, geminiApiKey);
    }

    /// <summary>
    /// Example call to detect bounding boxes on the current camera frame.
    /// You can hook this to a UI button or call it automatically.
    /// </summary>
    public void Request2DBoundingBoxes()
    {
        // We'll do it in a coroutine or async
        StartCoroutine(DetectBoxesRoutine());
    }

    private System.Collections.IEnumerator DetectBoxesRoutine()
    {
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);

        // 2) Convert to base64 (PNG)
        string base64Image = ConvertTextureToBase64(frameTex);

        // 3) Build prompt 
        // Example: "Detect SKU items, with no more than 20 items. Output a json list..."
        string prompt = "Detect SKU items, with no more than 20 items. " +
            "Output a json list where each entry contains the 2D bounding box in \"box_2d\" " +
            "and a text label of their name indicating exactly what the item is (the product name) in \"label\".";

        // 4) Call Gemini
        var request = geminiClient.GenerateContent(prompt, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        string response = request.Result;

        Debug.Log(response);

        // 5) Parse JSON
        List<Box2DResult> boxResults = ParseGeminiResponse(response);
        if (boxResults == null)
        {
            Debug.LogError("No valid boxes found or parsing error.");
        }
        else
        {
            Debug.Log($"Got {boxResults.Count} boxes from Gemini!");
            if (m_geminiRaycast != null && boxResults.Count > 0)
            {
                m_geminiRaycast.OnBoxesUpdated(boxResults);
            }
        }

        // 6) Clear old boxes
        ClearOldBoxes();

        // 7) Instantiate new bounding boxes
        if (boxResults != null)
        {
            foreach (var box in boxResults)
            {
                CreateBoundingBoxUI(box);
            }
        }

        // Clean up texture
        Destroy(frameTex);
    }

    /// <summary>
    /// Takes the bounding box data and spawns a UI element in overlayCanvas.
    /// </summary>
    private void CreateBoundingBoxUI(Box2DResult box)
    {
        if (!boundingBoxPrefab || !overlayCanvas)
        {
            Debug.LogWarning("No boundingBoxPrefab or overlayCanvas assigned.");
            return;
        }

        // box_2d: [ymin, xmin, ymax, xmax]
        float ymin = box.box_2d[0];
        float xmin = box.box_2d[1];
        float ymax = box.box_2d[2];
        float xmax = box.box_2d[3];

        float boxWidth = (xmax - xmin);
        float boxHeight = (ymax - ymin);

        // If Gemini uses 1000-based coords or some scaled coords, you might need to scale them
        // If 'boxesAreInPixelCoords' is false, do your own scaling here:
        // e.g. if it's "0~1000" then multiply by (renderTex.width / 1000f), etc.

        if (!boxesAreInPixelCoords)
        {
            float w = cameraRenderTex.width;
            float h = cameraRenderTex.height;

            ymin = ymin / 1000f * h;
            xmin = xmin / 1000f * w;
            ymax = ymax / 1000f * h;
            xmax = xmax / 1000f * w;

            boxWidth = xmax - xmin;
            boxHeight = ymax - ymin;
        }

        // Create a bounding box UI
        var bbObj = Instantiate(boundingBoxPrefab, overlayCanvas.transform);
        bbObj.name = "BoundingBox_" + box.label;

        // Adjust RectTransform to position/size
        RectTransform rt = bbObj.GetComponent<RectTransform>();
        if (rt == null)
        {
            Debug.LogWarning("boundingBoxPrefab has no RectTransform!");
            return;
        }

        // Assume top-left is (0,0) in Canvas or something similar. 
        // If your canvas top-left is 0,0 then the y might be negative. 
        // Often Unity's default UI origin is top-left with y down. 
        // Adjust pivot/anchors accordingly.
        rt.anchoredPosition = new Vector2(xmin, -ymin);
        rt.sizeDelta = new Vector2(boxWidth, boxHeight);

        // Try setting label text
        var text = bbObj.GetComponentInChildren<TMPro.TextMeshProUGUI>();
        if (text)
        {
            text.text = box.label;
        }
    }

    /// <summary>
    /// Convert the raw Gemini response to a list of Box2DResult objects.
    /// If the response has a code block with ```json, strip that out first.
    /// </summary>
    private List<Box2DResult> ParseGeminiResponse(string response)
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
            Debug.Log("Gemini bounding box text:\n" + textWithBackticks);

            if (textWithBackticks.Contains("```json"))
            {
                var splitted = textWithBackticks.Split(new[] { "```json" }, StringSplitOptions.None);
                if (splitted.Length > 1)
                {
                    var splitted2 = splitted[1].Split(new[] { "```" }, StringSplitOptions.None);
                    textWithBackticks = splitted2[0];
                }
            }

            var boxList = JsonConvert.DeserializeObject<List<Box2DResult>>(textWithBackticks);
            return boxList;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error parsing bounding box JSON: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Capture the current RenderTexture to a CPU Texture2D (PNG-encodable).
    /// </summary>
    private Texture2D CaptureFrame(RenderTexture rt)
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
    /// e.g. "iVBORw0KGgoAAAANSUhEUg..."
    /// </summary>
    private string ConvertTextureToBase64(Texture2D tex)
    {
        var bytes = tex.EncodeToPNG();
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Remove old bounding box objects from the canvas.
    /// A simple approach is to destroy any child object with a name containing "BoundingBox_".
    /// Adjust to your preference.
    /// </summary>
    private void ClearOldBoxes()
    {
        if (!overlayCanvas) return;

        var allChildren = overlayCanvas.GetComponentsInChildren<RectTransform>();
        foreach (var child in allChildren)
        {
            if (child.name.StartsWith("BoundingBox_"))
            {
                Destroy(child.gameObject);
            }
        }
    }
}

[Serializable]
public class Box2DResult
{
    /// <summary>
    /// [ymin, xmin, ymax, xmax]
    /// </summary>
    public float[] box_2d;
    public string label;
}
