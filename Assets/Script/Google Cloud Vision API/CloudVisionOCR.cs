using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Text;

public class CloudVisionOCR : MonoBehaviour
{
    [Header("Google Cloud Vision Settings")]
    [Tooltip("Your Google Cloud Vision API Key")]
    [SerializeField] private string apiKey = "<YOUR_API_KEY>";
    [Tooltip("RenderTexture to capture and analyze")]
    [SerializeField] private RenderTexture sourceRenderTexture;
    
    // API endpoint URL (for OCR using images:annotate)
    private string visionEndpoint = "https://vision.googleapis.com/v1/images:annotate";

    // JSON data classes for request
    [System.Serializable] private class Image { public string content; }
    [System.Serializable] private class Feature { public string type; public int maxResults = 1; }
    [System.Serializable] private class AnnotateImageRequest 
    { 
        public Image image; 
        public List<Feature> features; 
    }
    [System.Serializable] private class AnnotateImageRequests 
    { 
        public List<AnnotateImageRequest> requests; 
    }

    // JSON data classes for response (to parse the relevant part)
    [System.Serializable] private class TextAnnotation { public string description; }
    [System.Serializable] private class AnnotateImageResponse { public TextAnnotation[] textAnnotations; }
    [System.Serializable] private class AnnotateImageResponses { public AnnotateImageResponse[] responses; }

    /// <summary>
    /// Public method to start the OCR process (can be called from UI or other scripts).
    /// </summary>
    [ContextMenu("Start OCR")]
    public void StartOCR()
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
        StartCoroutine(ProcessOCRRoutine());
    }

    /// <summary>
    /// Coroutine that captures the RenderTexture, sends the Vision API request, and handles the response.
    /// </summary>
    private IEnumerator ProcessOCRRoutine()
    {
        // 1. Convert RenderTexture to a Base64-encoded image string
        string base64Image = ConvertRenderTextureToBase64(sourceRenderTexture);
        if (base64Image == null)
        {
            yield break; // conversion failed (error already logged)
        }

        // 2. Create the Vision API JSON request payload
        AnnotateImageRequests requestData = new AnnotateImageRequests();
        requestData.requests = new List<AnnotateImageRequest>();

        AnnotateImageRequest request = new AnnotateImageRequest();
        request.image = new Image { content = base64Image };
        request.features = new List<Feature> {
            new Feature { type = "TEXT_DETECTION", maxResults = 1 }
        };
        requestData.requests.Add(request);

        string jsonRequest = JsonUtility.ToJson(requestData);
        
        // 3. Send the POST request to the Vision API
        string requestUrl = $"{visionEndpoint}?key={apiKey}";
        UnityWebRequest webRequest = new UnityWebRequest(requestUrl, "POST");
        byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonRequest);
        webRequest.uploadHandler = new UploadHandlerRaw(jsonBytes);
        webRequest.downloadHandler = new DownloadHandlerBuffer();
        webRequest.SetRequestHeader("Content-Type", "application/json");

        // Send request and wait for response
        yield return webRequest.SendWebRequest();

        // 4. Handle the response
        if (webRequest.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"Vision API request failed: {webRequest.error}");
        }
        else
        {
            string jsonResponse = webRequest.downloadHandler.text;
            // Parse JSON to extract text
            if (!string.IsNullOrEmpty(jsonResponse))
            {
                // Use Unity's JsonUtility to parse the response
                AnnotateImageResponses responseData = JsonUtility.FromJson<AnnotateImageResponses>(jsonResponse);
                if (responseData.responses != null && responseData.responses.Length > 0)
                {
                    AnnotateImageResponse firstResponse = responseData.responses[0];
                    if (firstResponse.textAnnotations != null && firstResponse.textAnnotations.Length > 0)
                    {
                        string detectedText = firstResponse.textAnnotations[0].description;
                        Debug.Log("OCR Recognized Text: " + detectedText);
                        // (Optionally, do something with detectedText, e.g., display on UI)
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
            else
            {
                Debug.LogWarning("Received empty JSON response from Vision API.");
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
            RenderTexture currentRT = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            image.Apply();
            RenderTexture.active = currentRT;
            byte[] imageBytes = image.EncodeToPNG();
            Destroy(image);  // free the Texture2D as we no longer need it
            return System.Convert.ToBase64String(imageBytes);
        }
        catch (System.Exception e)
        {
            Debug.LogError("Failed to convert RenderTexture to Base64: " + e.Message);
            return null;
        }
    }
}
