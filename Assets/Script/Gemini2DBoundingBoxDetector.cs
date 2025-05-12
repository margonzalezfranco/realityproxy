using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;
using TMPro;

/// <summary>
/// Specialized implementation for detecting 2D bounding boxes using Gemini API.
/// </summary>
public class Gemini2DBoundingBoxDetector : GeminiGeneral
{
    [Header("UI Settings")]
    [Tooltip("Canvas for drawing bounding box overlays (Screen Space - Overlay recommended).")]
    public Canvas overlayCanvas;

    [Tooltip("Prefab for bounding boxes (should have an Image + Text for label).")]
    public GameObject boundingBoxPrefab;

    [Tooltip("Coordinates might require scaling if Gemini boxes are in 1000-based coords, etc.")]
    public bool boxesAreInPixelCoords = true;
    
    [Tooltip("Reference to the scan button's TextMeshPro component to update during scanning.")]
    public TextMeshPro scanButtonText;

    public GeminiRaycast m_geminiRaycast;

    // Store camera pose at capture time
    private CameraPoseData capturedCameraPose;

    /// <summary>
    /// Data structure to store camera pose at capture time
    /// </summary>
    public struct CameraPoseData
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 offsetNodePosition;
        public Quaternion offsetNodeRotation;

        public CameraPoseData(Transform camera, Transform offsetNode)
        {
            position = camera.position;
            rotation = camera.rotation;
            
            if (offsetNode != null)
            {
                offsetNodePosition = offsetNode.position;
                offsetNodeRotation = offsetNode.rotation;
            }
            else
            {
                offsetNodePosition = Vector3.zero;
                offsetNodeRotation = Quaternion.identity;
            }
        }
    }

    /// <summary>
    /// Example call to detect bounding boxes on the current camera frame.
    /// You can hook this to a UI button or call it automatically.
    /// </summary>
    public void Request2DBoundingBoxes()
    {
        if (scanButtonText != null)
        {
            scanButtonText.text = "Scanning";
        }
        
        // Capture the camera pose at the time the button is pressed
        if (m_geminiRaycast != null && m_geminiRaycast.xrCamera != null)
        {
            capturedCameraPose = new CameraPoseData(
                m_geminiRaycast.xrCamera.transform, 
                m_geminiRaycast.offsetNode
            );
            
            Debug.Log("Captured camera pose: " + 
                      capturedCameraPose.position + ", " + 
                      capturedCameraPose.rotation.eulerAngles);
        }
        else
        {
            Debug.LogWarning("Cannot capture camera pose - missing GeminiRaycast or xrCamera reference");
        }
        
        StartCoroutine(DetectBoxesRoutine());
    }

    private System.Collections.IEnumerator DetectBoxesRoutine()
    {
        Debug.Log("Detecting bounding boxes...");
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);

        // 2) Convert to base64 (PNG)
        string base64Image = ConvertTextureToBase64(frameTex);

        // 3) Build prompt specific to bounding box detection. [need to refine]
        string prompt = "Detect SKU items, with no more than 20 items. " +
            "Output a json list where each entry contains the 2D bounding box in \"box_2d\" " +
            "and a text label of their name indicating exactly what the item is (the product name) in \"label\".";

        // 4) Call Gemini
        // This now uses the new RequestStatus system which supports concurrent API calls
        // from multiple components without interfering with each other
        var request = MakeGeminiRequest(prompt, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        string response = request.Result;

        // Debug.Log(response);

        // 5) Parse JSON
        List<Box2DResult> boxResults = ParseBoundingBoxResponse(response);
        if (boxResults == null)
        {
            Debug.LogError("No valid boxes found or parsing error.");
        }
        else
        {
            Debug.Log($"Got {boxResults.Count} boxes from Gemini!");
            if (m_geminiRaycast != null && boxResults.Count > 0)
            {
                // Pass the captured camera pose with the boxes
                m_geminiRaycast.OnBoxesUpdatedWithCameraPose(boxResults, capturedCameraPose);
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
        
        // Update button text back to "Scan"
        if (scanButtonText != null)
        {
            scanButtonText.text = "Scan";
        }
    }

    // Keep all the bounding box specific methods
    private List<Box2DResult> ParseBoundingBoxResponse(string response)
    {
        try
        {
            string jsonText = ParseGeminiRawResponse(response);
            if (string.IsNullOrEmpty(jsonText)) return null;
            
            return JsonConvert.DeserializeObject<List<Box2DResult>>(jsonText);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error parsing bounding box JSON: {ex}");
            return null;
        }
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
