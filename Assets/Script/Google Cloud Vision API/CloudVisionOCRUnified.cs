using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System;
using System.Linq;

public class CloudVisionOCRUnified : MonoBehaviour
{
    public enum OCRMode { FullText, BoundingBox }
    
    [Header("Google Cloud Vision Settings")]
    [Tooltip("Your Google Cloud Vision API Key")]
    [SerializeField] private string apiKey = "<YOUR_API_KEY>";
    [Tooltip("RenderTexture to capture and analyze")]
    public RenderTexture sourceRenderTexture;
    [Tooltip("Choose the OCR mode (FullText: simple text, BoundingBox: word + bounding boxes)")]
    public OCRMode ocrMode = OCRMode.FullText;
    
    // Shared API endpoint URL (for OCR using images:annotate)
    private string visionEndpoint = "https://vision.googleapis.com/v1/images:annotate";

    // Delegate for OCR completion callback
    public delegate void OCRCompleteCallback(string fullText, List<string> wordList);
    
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
    /// Sets the Google Cloud Vision API key programmatically
    /// </summary>
    public void SetApiKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            Debug.LogError("Attempted to set empty API key");
            return;
        }
        
        apiKey = key;
        Debug.Log("Google Cloud Vision API key set successfully");
    }

    /// <summary>
    /// Public method to start the OCR process. Called via ContextMenu or from UI.
    /// </summary>
    [ContextMenu("Start OCR")]
    public void StartOCR()
    {
        StartOCR(null);
    }
    
    /// <summary>
    /// Overloaded method to start OCR with a callback handler
    /// </summary>
    /// <param name="resultHandler">Handler for OCR results</param>
    public void StartOCR(object resultHandler)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("API key not set. Please assign your Google Cloud Vision API key.");
            return;
        }
        if (sourceRenderTexture == null)
        {
            Debug.LogError("RenderTexture not set. Please assign a RenderTexture to analyze.");
            return;
        }

        // Cast the resultHandler to the correct type if it's not null
        SphereToggleScript.OCRResultHandler typedHandler = resultHandler as SphereToggleScript.OCRResultHandler;

        if (ocrMode == OCRMode.FullText)
            StartCoroutine(ProcessOCRFullTextRoutine(typedHandler));
        else if (ocrMode == OCRMode.BoundingBox)
            StartCoroutine(ProcessOCRBoundingBoxRoutine(typedHandler));
    }

    /// <summary>
    /// FullText mode: Uses TEXT_DETECTION and parses the simple text annotation.
    /// </summary>
    private IEnumerator ProcessOCRFullTextRoutine(SphereToggleScript.OCRResultHandler resultHandler = null)
    {
        // Validate API key
        if (string.IsNullOrEmpty(apiKey) || apiKey == "<YOUR_API_KEY>")
        {
            Debug.LogError("Google Cloud Vision API Key is not set. Please set a valid API key in the inspector.");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
            yield break;
        }
        
        // Validate render texture
        if (sourceRenderTexture == null)
        {
            Debug.LogError("Source RenderTexture is null. Please assign a valid RenderTexture.");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
            yield break;
        }
        
        string base64Image = ConvertRenderTextureToBase64(sourceRenderTexture);
        if (base64Image == null)
        {
            Debug.LogError("Failed to convert RenderTexture to base64 string.");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
            yield break;
        }

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
            Debug.LogError($"Vision API request failed: {webRequest.error}\nResponse code: {webRequest.responseCode}\nResponse body: {webRequest.downloadHandler.text}");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
            yield break;
        }

        string jsonResponse = webRequest.downloadHandler.text;
        // Debug.Log("Vision API response: " + jsonResponse);

        // Parse JSON to extract full text from textAnnotations
        AnnotateImageResponses responseData = JsonUtility.FromJson<AnnotateImageResponses>(jsonResponse);
        if (responseData.responses != null && responseData.responses.Length > 0)
        {
            AnnotateImageResponse firstResponse = responseData.responses[0];
            if (firstResponse.textAnnotations != null && firstResponse.textAnnotations.Length > 0)
            {
                string detectedText = firstResponse.textAnnotations[0].description;
                Debug.Log("OCR Recognized Text: " + detectedText);
                
                // Extract individual words
                List<string> wordList = new List<string>();
                if (detectedText != null)
                {
                    string[] words = detectedText.Split(new char[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    wordList.AddRange(words);
                }
                
                // Call the callback if provided
                if (resultHandler != null)
                {
                    resultHandler.HandleOCRResult(detectedText, wordList);
                }
            }
            else
            {
                Debug.LogWarning("No text detected in the image.");
                if (resultHandler != null)
                {
                    resultHandler.HandleOCRResult("", new List<string>());
                }
            }
        }
        else
        {
            Debug.LogWarning("Empty response from Vision API.");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
        }
    }

    /// <summary>
    /// BoundingBox mode: Uses DOCUMENT_TEXT_DETECTION and parses the hierarchical response,
    /// concatenating symbols in each word and outputting the bounding box.
    /// </summary>
    private IEnumerator ProcessOCRBoundingBoxRoutine(SphereToggleScript.OCRResultHandler resultHandler = null)
    {
        // Validate API key
        if (string.IsNullOrEmpty(apiKey) || apiKey == "<YOUR_API_KEY>")
        {
            Debug.LogError("Google Cloud Vision API Key is not set. Please set a valid API key in the inspector.");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
            yield break;
        }
        
        // Validate render texture
        if (sourceRenderTexture == null)
        {
            Debug.LogError("Source RenderTexture is null. Please assign a valid RenderTexture.");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
            yield break;
        }
        
        string base64Image = ConvertRenderTextureToBase64(sourceRenderTexture);
        if (base64Image == null)
        {
            Debug.LogError("Failed to convert RenderTexture to base64 string.");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
            yield break;
        }

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
            Debug.LogError($"Vision API request failed: {webRequest.error}\nResponse code: {webRequest.responseCode}\nResponse body: {webRequest.downloadHandler.text}");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
            yield break;
        }

        string jsonResponse = webRequest.downloadHandler.text;
        // Debug.Log("Vision API response: " + jsonResponse);

        // Parse the response to extract bounding boxes for each word
        VisionResponse responseData = JsonUtility.FromJson<VisionResponse>(jsonResponse);
        string fullDetectedText = "";
        List<string> wordList = new List<string>();
        Dictionary<string, SphereToggleScript.BoundingBox> wordBoundingBoxes = new Dictionary<string, SphereToggleScript.BoundingBox>();
        
        if (responseData.responses != null && responseData.responses.Count > 0)
        {
            // Log the full text from textAnnotations (similar to FullText mode)
            if (responseData.responses[0].textAnnotations != null && responseData.responses[0].textAnnotations.Length > 0)
            {
                fullDetectedText = responseData.responses[0].textAnnotations[0].description;
                // Debug.Log("OCR Recognized Text: " + fullDetectedText);
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
                                
                                // Add to word list
                                if (!string.IsNullOrEmpty(wordText))
                                {
                                    wordList.Add(wordText);
                                    
                                    // Extract bounding box information directly
                                    if (word.boundingBox != null && word.boundingBox.vertices != null && word.boundingBox.vertices.Count == 4)
                                    {
                                        // Calculate min/max coordinates to create a bounding box
                                        float minX = word.boundingBox.vertices.Min(v => v.x);
                                        float minY = word.boundingBox.vertices.Min(v => v.y);
                                        float maxX = word.boundingBox.vertices.Max(v => v.x);
                                        float maxY = word.boundingBox.vertices.Max(v => v.y);
                                        
                                        // Create and store the bounding box
                                        SphereToggleScript.BoundingBox box = new SphereToggleScript.BoundingBox(minX, minY, maxX, maxY);
                                        wordBoundingBoxes[wordText] = box;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                Debug.LogWarning("No fullTextAnnotation found in the response.");
            }
            
            // Call the callback if provided
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult(fullDetectedText, wordList, wordBoundingBoxes);
            }
        }
        else
        {
            Debug.LogWarning("Empty response from Vision API.");
            if (resultHandler != null)
            {
                resultHandler.HandleOCRResult("", new List<string>());
            }
        }
    }

    /// <summary>
    /// Helper to convert a RenderTexture to a Base64 PNG string.
    /// </summary>
    private string ConvertRenderTextureToBase64(RenderTexture rt)
    {
        try
        {
            if (rt == null)
            {
                Debug.LogError("RenderTexture is null");
                return null;
            }
            
            if (rt.width <= 0 || rt.height <= 0)
            {
                Debug.LogError($"Invalid RenderTexture dimensions: {rt.width}x{rt.height}");
                return null;
            }
            
            RenderTexture currentRT = RenderTexture.active;
            RenderTexture.active = rt;
            
            // Check if the render texture is actually created and ready
            if (!rt.IsCreated())
            {
                Debug.LogError("RenderTexture is not created. Make sure it's properly initialized.");
                RenderTexture.active = currentRT;
                return null;
            }
            
            Texture2D image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            image.Apply();
            RenderTexture.active = currentRT;
            
            byte[] imageBytes = image.EncodeToPNG();
            if (imageBytes == null || imageBytes.Length == 0)
            {
                Debug.LogError("Failed to encode texture to PNG");
                Destroy(image);
                return null;
            }
            
            Destroy(image);
            return Convert.ToBase64String(imageBytes);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error converting RenderTexture: {e.Message}\nStack trace: {e.StackTrace}");
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
