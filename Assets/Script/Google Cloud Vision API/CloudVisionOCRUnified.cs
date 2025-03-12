using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System;

public class CloudVisionOCRUnified : MonoBehaviour
{
    public enum OCRMode { FullText, BoundingBox }
    
    [Header("Google Cloud Vision Settings")]
    [Tooltip("Your Google Cloud Vision API Key")]
    [SerializeField] private string apiKey = "<YOUR_API_KEY>";
    [Tooltip("RenderTexture to capture and analyze")]
    [SerializeField] private RenderTexture sourceRenderTexture;
    [Tooltip("Choose the OCR mode (FullText: simple text, BoundingBox: word + bounding boxes)")]
    public OCRMode ocrMode = OCRMode.FullText;
    
    // Shared API endpoint URL (for OCR using images:annotate)
    private string visionEndpoint = "https://vision.googleapis.com/v1/images:annotate";

    // Store a custom source texture that overrides the render texture if set
    private Texture2D customSourceTexture;
    
    /// <summary>
    /// Set a custom texture to use for OCR instead of the render texture
    /// </summary>
    /// <param name="texture">The texture to analyze</param>
    public void SetSourceTexture(Texture2D texture)
    {
        customSourceTexture = texture;
        Debug.Log($"Custom source texture set: {texture.width}x{texture.height}");
    }

    /// <summary>
    /// Clear the custom source texture and revert to using the render texture
    /// </summary>
    public void ClearSourceTexture()
    {
        if (customSourceTexture != null)
        {
            Destroy(customSourceTexture);
            customSourceTexture = null;
            Debug.Log("Custom source texture cleared");
        }
    }

    #region Request Payload Data Classes
    // Using a common image data class
    [Serializable]
    public class ImageContent
    {
        public string content;
    }

    [Serializable]
    public class Feature
    {
        public string type;
        public int maxResults = 1;
    }

    [Serializable]
    public class AnnotateImageRequest
    {
        public ImageContent image;
        public List<Feature> features;
    }

    [Serializable]
    public class AnnotateImageRequests
    {
        public List<AnnotateImageRequest> requests;
    }
    #endregion

    #region Full Text Response Data Classes (TEXT_DETECTION)
    [Serializable]
    public class TextAnnotation
    {
        public string description;
    }

    [Serializable]
    private class AnnotateImageResponse
    {
        public TextAnnotation[] textAnnotations;
    }

    [Serializable]
    private class AnnotateImageResponses
    {
        public AnnotateImageResponse[] responses;
    }
    #endregion

    #region Bounding Box Response Data Classes (DOCUMENT_TEXT_DETECTION)
    [Serializable]
    public class Vertex
    {
        public int x;
        public int y;
    }

    [Serializable]
    public class BoundingPoly
    {
        public List<Vertex> vertices;
    }

    // Each word is returned as a series of symbols (characters)
    [Serializable]
    public class Symbol
    {
        public string text;
    }

    [Serializable]
    public class Word
    {
        public BoundingPoly boundingBox;
        public List<Symbol> symbols;
    }

    [Serializable]
    public class Paragraph
    {
        public List<Word> words;
        public BoundingPoly boundingBox;
    }

    [Serializable]
    public class Block
    {
        public List<Paragraph> paragraphs;
        public BoundingPoly boundingBox;
    }

    [Serializable]
    public class Page
    {
        public List<Block> blocks;
        public BoundingPoly boundingBox;
    }

    [Serializable]
    public class FullTextAnnotation
    {
        public List<Page> pages;
        public string text;
    }

    [Serializable]
    public class DocumentResponse
    {
        public FullTextAnnotation fullTextAnnotation;
        public TextAnnotation[] textAnnotations;
    }

    [Serializable]
    public class VisionResponse
    {
        public List<DocumentResponse> responses;
    }
    #endregion

    /// <summary>
    /// Public method to start the OCR process. Called via ContextMenu or from UI.
    /// </summary>
    [ContextMenu("Start OCR")]
    public void StartOCR()
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("API key not set. Please assign your Google Cloud Vision API key.");
            return;
        }
        
        // Check if we have either a custom texture or render texture to analyze
        if (customSourceTexture == null && sourceRenderTexture == null)
        {
            Debug.LogError("No texture available for analysis. Please assign a RenderTexture or set a custom texture.");
            return;
        }

        if (ocrMode == OCRMode.FullText)
            StartCoroutine(ProcessOCRFullTextRoutine());
        else if (ocrMode == OCRMode.BoundingBox)
            StartCoroutine(ProcessOCRBoundingBoxRoutine());
    }

    /// <summary>
    /// FullText mode: Uses TEXT_DETECTION and parses the simple text annotation.
    /// </summary>
    private IEnumerator ProcessOCRFullTextRoutine()
    {
        string base64Image;
        
        // Use the custom texture if available, otherwise use the render texture
        if (customSourceTexture != null)
        {
            base64Image = ConvertTexture2DToBase64(customSourceTexture);
        }
        else
        {
            base64Image = ConvertRenderTextureToBase64(sourceRenderTexture);
        }
        
        if (base64Image == null)
            yield break;

        // Build payload for TEXT_DETECTION
        AnnotateImageRequests requestData = new AnnotateImageRequests();
        requestData.requests = new List<AnnotateImageRequest>();

        AnnotateImageRequest request = new AnnotateImageRequest();
        request.image = new ImageContent { content = base64Image };
        request.features = new List<Feature> {
            new Feature { type = "TEXT_DETECTION", maxResults = 1 }
        };
        requestData.requests.Add(request);

        string jsonRequest = JsonUtility.ToJson(requestData);

        // Send the POST request
        string requestUrl = $"{visionEndpoint}?key={apiKey}";
        UnityWebRequest webRequest = new UnityWebRequest(requestUrl, "POST");
        byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonRequest);
        webRequest.uploadHandler = new UploadHandlerRaw(jsonBytes);
        webRequest.downloadHandler = new DownloadHandlerBuffer();
        webRequest.SetRequestHeader("Content-Type", "application/json");

        yield return webRequest.SendWebRequest();

        if (webRequest.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"Vision API request failed: {webRequest.error}");
            yield break;
        }

        string jsonResponse = webRequest.downloadHandler.text;
        Debug.Log("Vision API response: " + jsonResponse);

        // Parse JSON to extract full text from textAnnotations
        AnnotateImageResponses responseData = JsonUtility.FromJson<AnnotateImageResponses>(jsonResponse);
        if (responseData.responses != null && responseData.responses.Length > 0)
        {
            AnnotateImageResponse firstResponse = responseData.responses[0];
            if (firstResponse.textAnnotations != null && firstResponse.textAnnotations.Length > 0)
            {
                string detectedText = firstResponse.textAnnotations[0].description;
                Debug.Log("OCR Recognized Text: " + detectedText);
            }
            else
            {
                Debug.LogWarning("No text detected in the image.");
            }
        }
        else
        {
            Debug.LogWarning("Empty response from Vision API.");
        }
    }

    /// <summary>
    /// BoundingBox mode: Uses DOCUMENT_TEXT_DETECTION and parses the hierarchical response,
    /// concatenating symbols in each word and outputting the bounding box.
    /// </summary>
    private IEnumerator ProcessOCRBoundingBoxRoutine()
    {
        string base64Image;
        
        // Use the custom texture if available, otherwise use the render texture
        if (customSourceTexture != null)
        {
            base64Image = ConvertTexture2DToBase64(customSourceTexture);
        }
        else
        {
            base64Image = ConvertRenderTextureToBase64(sourceRenderTexture);
        }
        
        if (base64Image == null)
            yield break;

        // Build payload for DOCUMENT_TEXT_DETECTION
        AnnotateImageRequests requestData = new AnnotateImageRequests();
        requestData.requests = new List<AnnotateImageRequest>();

        AnnotateImageRequest request = new AnnotateImageRequest();
        request.image = new ImageContent { content = base64Image };
        request.features = new List<Feature> {
            new Feature { type = "DOCUMENT_TEXT_DETECTION", maxResults = 10 }
        };
        requestData.requests.Add(request);

        string jsonRequest = JsonUtility.ToJson(requestData);

        // Send the POST request
        string requestUrl = $"{visionEndpoint}?key={apiKey}";
        UnityWebRequest webRequest = new UnityWebRequest(requestUrl, "POST");
        byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonRequest);
        webRequest.uploadHandler = new UploadHandlerRaw(jsonBytes);
        webRequest.downloadHandler = new DownloadHandlerBuffer();
        webRequest.SetRequestHeader("Content-Type", "application/json");

        yield return webRequest.SendWebRequest();

        if (webRequest.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Vision API request failed: " + webRequest.error);
            yield break;
        }

        string jsonResponse = webRequest.downloadHandler.text;
        Debug.Log("Vision API response: " + jsonResponse);

        // Parse the response to extract bounding boxes for each word
        VisionResponse responseData = JsonUtility.FromJson<VisionResponse>(jsonResponse);
        if (responseData.responses != null && responseData.responses.Count > 0)
        {
            // Log the full text from textAnnotations (similar to FullText mode)
            if (responseData.responses[0].textAnnotations != null && responseData.responses[0].textAnnotations.Length > 0)
            {
                string detectedText = responseData.responses[0].textAnnotations[0].description;
                Debug.Log("OCR Recognized Text: " + detectedText);
            }
            
            FullTextAnnotation fullText = responseData.responses[0].fullTextAnnotation;
            if (fullText != null && fullText.pages != null && fullText.pages.Count > 0)
            {
                foreach (Page page in fullText.pages)
                {
                    foreach (Block block in page.blocks)
                    {
                        foreach (Paragraph paragraph in block.paragraphs)
                        {
                            foreach (Word word in paragraph.words)
                            {
                                // Concatenate symbols to form the complete word text.
                                string wordText = "";
                                if (word.symbols != null)
                                {
                                    foreach (Symbol symbol in word.symbols)
                                    {
                                        wordText += symbol.text;
                                    }
                                }
                                string box = GetBoundingBoxAsString(word.boundingBox);
                                Debug.Log($"Detected word: {wordText} with bounding box: {box}");
                            }
                        }
                    }
                }
            }
            else
            {
                Debug.LogWarning("No fullTextAnnotation found in the response.");
            }
        }
        else
        {
            Debug.LogWarning("Empty response from Vision API.");
        }
    }

    /// <summary>
    /// Helper to convert a RenderTexture to a Base64 PNG string.
    /// </summary>
    private string ConvertRenderTextureToBase64(RenderTexture rt)
    {
        try
        {
            RenderTexture currentRT = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            image.Apply();
            RenderTexture.active = currentRT;
            byte[] imageBytes = image.EncodeToPNG();
            Destroy(image);
            return Convert.ToBase64String(imageBytes);
        }
        catch (Exception e)
        {
            Debug.LogError("Error converting RenderTexture: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Helper to convert a Texture2D to a Base64 PNG string.
    /// </summary>
    private string ConvertTexture2DToBase64(Texture2D texture)
    {
        try
        {
            byte[] imageBytes = texture.EncodeToPNG();
            return Convert.ToBase64String(imageBytes);
        }
        catch (Exception e)
        {
            Debug.LogError("Error converting Texture2D: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Helper method to return a string representation of a bounding polygon.
    /// </summary>
    private string GetBoundingBoxAsString(BoundingPoly boundingPoly)
    {
        if (boundingPoly == null || boundingPoly.vertices == null)
            return "No bounding box.";
        StringBuilder sb = new StringBuilder();
        foreach (var vertex in boundingPoly.vertices)
        {
            sb.Append($"({vertex.x}, {vertex.y}) ");
        }
        return sb.ToString();
    }
}
