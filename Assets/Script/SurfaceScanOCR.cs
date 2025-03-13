using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI; // Add for UI components
using TMPro; // Add for TextMeshPro

/// <summary>
/// SurfaceScanOCR: Listens for surface drawing events and triggers OCR scanning
/// This class connects the DragSurface system with the CloudVisionOCRUnified system
/// to perform OCR scanning when a user completes drawing a full surface (all four points).
/// </summary>
public class SurfaceScanOCR : MonoBehaviour
{
    [Header("Dependencies")]
    [Tooltip("Reference to the CloudVisionOCR component")]
    public CloudVisionOCRUnified ocrComponent;

    [Header("OCR Settings")]
    [Tooltip("Optional delay before triggering OCR (seconds)")]
    public float ocrDelay = 0f;

    public RenderTexture cameraRenderTex;
    
    [Header("OCR Line Rendering")]
    [Tooltip("Prefab to use for OCR text lines")]
    public GameObject ocrLinePrefab;
    
    [Tooltip("Parent transform to hold all OCR text lines")]
    public Transform ocrLineContainer;
    
    [Tooltip("Z-offset for text from the surface (in meters)")]
    public float textZOffset = 0.001f;
    
    [Tooltip("Scale factor for text size")]
    public float textScaleFactor = 0.001f;
    
    [Tooltip("If true, destroy previous OCR lines before creating new ones")]
    public bool clearPreviousOCRLines = true;
    
    // Reference to the most recently scanned surface
    private GameObject lastScannedSurface;
    
    // References to the corners of the last scanned surface
    private Vector3 surfacePoint1;
    private Vector3 surfacePoint2;
    private Vector3 surfacePoint3;
    private Vector3 surfacePoint4;
    
    // Reference to the OCR lines created
    private List<GameObject> createdOCRLines = new List<GameObject>();
    
    // Reference to the cropped texture dimensions
    private int croppedTextureWidth;
    private int croppedTextureHeight;
    
    [Header("Camera Offset Settings")]
    [Tooltip("The offset node representing the virtual RGB camera position")]
    public Transform offsetNode;
    
    [Tooltip("Field of view for the virtual camera at the offset node")]
    public float offsetNodeFOV = 72.5f;
    
    [Tooltip("Aspect ratio for the virtual camera")]
    public float offsetNodeAspect = 16f / 9f;
    
    [Tooltip("Near plane for the virtual camera")]
    public float offsetNodeNear = 0.1f;
    
    [Tooltip("Far plane for the virtual camera")]
    public float offsetNodeFar = 100f;
    
    [Header("Preview Settings")]
    [Tooltip("UI RawImage to preview the cropped texture")]
    public RawImage previewImage;
    
    [Tooltip("UI panel to hold the preview image")]
    public GameObject previewPanel;
    
    [Tooltip("Whether to show the preview automatically")]
    public bool showPreviewAutomatically = true;
    
    [Tooltip("Preview display duration in seconds (0 = until manually closed)")]
    public float previewDuration = 5f;
    
    [Header("2D UI Visualization")]
    [Tooltip("Canvas used to display 2D crop area overlay")]
    public Canvas cropPreviewCanvas;
    
    [Tooltip("Color of the 2D crop area outline")]
    public Color cropAreaOutlineColor = Color.green;
    
    [Tooltip("Width of the 2D crop area outline")]
    public float cropAreaOutlineWidth = 4f;
    
    [Tooltip("Whether to show the 2D crop area overlay")]
    public bool showCropAreaOverlay = true;
    
    [Header("Debug Visualization")]
    [Tooltip("Enable visualization of camera frustum, rays, and projection")]
    public bool showDebugVisualizations = true;
    
    [Tooltip("Material to use for line renderers (optional)")]
    public Material debugLineMaterial;
    
    [Tooltip("Width of debug lines")]
    public float debugLineWidth = 0.002f;
    
    [Tooltip("Duration to show visualizations (0 = until next crop)")]
    public float visualizationDuration = 5f;
    
    // Reference to the last cropped texture
    private Texture2D lastCroppedTexture;
    
    // For storing debug visualization objects
    private List<GameObject> debugVisualizations = new List<GameObject>();
    
    // For visualizing the crop rectangle in world space
    private GameObject cropRectVisualizer;
    
    // For visualizing the crop rectangle on the canvas
    private RectTransform canvasCropAreaRect;
    private Image canvasCropAreaImage;

    // Start method to subscribe to events
    private void Start()
    {
        // Find OCR component if not assigned
        if (ocrComponent == null)
        {
            ocrComponent = FindAnyObjectByType<CloudVisionOCRUnified>();
            if (ocrComponent == null)
            {
                Debug.LogError("No CloudVisionOCRUnified component found in the scene!");
                enabled = false;
                return;
            }
        }

        // Find offset node if not assigned (try to find it by name)
        if (offsetNode == null)
        {
            GameObject offsetNodeObj = GameObject.Find("ManualOffsetNode");
            if (offsetNodeObj != null)
            {
                offsetNode = offsetNodeObj.transform;
                Debug.Log("Found ManualOffsetNode automatically: " + offsetNode.position);
            }
            else
            {
                Debug.LogWarning("No ManualOffsetNode found in the scene. Camera offset won't be applied.");
            }
        }
        
        if (cropPreviewCanvas == null)
        {
            Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach (Canvas canvas in canvases)
            {
                if (canvas.name == "CropPreview")
                {
                    cropPreviewCanvas = canvas;
                    Debug.Log("Found CropPreview canvas automatically.");
                    break;
                }
            }
        }
        
        // Create the canvas crop area outline object
        if (cropPreviewCanvas != null && canvasCropAreaRect == null)
        {
            CreateCanvasCropAreaVisualizer();
        }

        // Only subscribe to surface completion events
        DragSurface.OnSurfaceCompleted += HandleSurfaceCompleted;

        // Subscribe to OCR completed event
        ocrComponent.OnOCRComplete += HandleOCRComplete;
    }

    // Unsubscribe from events when this object is destroyed
    private void OnDestroy()
    {
        // Only unsubscribe from surface completion events
        DragSurface.OnSurfaceCompleted -= HandleSurfaceCompleted;
        
        // Unsubscribe from OCR events
        if (ocrComponent != null)
        {
            ocrComponent.OnOCRComplete -= HandleOCRComplete;
        }
        
        // Clean up any remaining visualizations
        ClearDebugVisualizations();
        
        // Clean up canvas crop area visualizer
        if (canvasCropAreaRect != null)
        {
            Destroy(canvasCropAreaRect.gameObject);
        }
    }

    /// <summary>
    /// Creates a UI element to visualize the crop area on the canvas
    /// </summary>
    private void CreateCanvasCropAreaVisualizer()
    {
        GameObject cropAreaObj = new GameObject("CanvasCropArea");
        cropAreaObj.transform.SetParent(cropPreviewCanvas.transform, false);
        
        // Add rect transform and configure it
        canvasCropAreaRect = cropAreaObj.AddComponent<RectTransform>();
        canvasCropAreaRect.anchorMin = Vector2.zero;
        canvasCropAreaRect.anchorMax = Vector2.zero;
        canvasCropAreaRect.pivot = Vector2.zero;
        
        // Add an image component
        canvasCropAreaImage = cropAreaObj.AddComponent<Image>();
        canvasCropAreaImage.color = cropAreaOutlineColor;
        
        // Make it an outline by setting fillCenter to false
        canvasCropAreaImage.fillCenter = false;
        canvasCropAreaImage.raycastTarget = false;
        
        // Set the thickness by adding an outline effect component
        // We'll adjust the image thickness by modifying the canvas size
        // The thickness will be controlled in the UpdateCanvasCropAreaVisualizer method
        
        // Hide it initially
        canvasCropAreaRect.gameObject.SetActive(false);
    }

    /// <summary>
    /// Updates the canvas crop area visualizer with the current crop bounds
    /// </summary>
    private void UpdateCanvasCropAreaVisualizer(float minX, float minY, float maxX, float maxY)
    {
        if (canvasCropAreaRect == null || cropPreviewCanvas == null)
            return;
            
        // Calculate width and height
        float width = maxX - minX;
        float height = maxY - minY;
        
        // Ensure non-zero dimensions for visualization
        width = Mathf.Max(2f, width);
        height = Mathf.Max(2f, height);
        
        // For UI canvas display, we need to invert the Y coordinate system
        // since Unity UI has (0,0) at top-left, but our texture coordinates are from bottom-left
        float displayMinY = cameraRenderTex.height - maxY;
        float displayMaxY = cameraRenderTex.height - minY;
        
        Debug.Log($"Canvas display coords: Y range from {displayMinY} to {displayMaxY}");
        
        // Set position and size (using the inverted coordinates for display)
        canvasCropAreaRect.anchoredPosition = new Vector2(minX, displayMinY);
        canvasCropAreaRect.sizeDelta = new Vector2(width, displayMaxY - displayMinY);
        
        // Apply the outline thickness by setting the border size
        if (canvasCropAreaImage != null)
        {
            // Set the thickness through the sprite's border
            // We want a thin outline, so we'll make sure the image is not filled
            canvasCropAreaImage.fillCenter = false;
            canvasCropAreaImage.pixelsPerUnitMultiplier = cropAreaOutlineWidth;
            canvasCropAreaImage.type = Image.Type.Sliced;
        }
        
        // Show the visualizer
        canvasCropAreaRect.gameObject.SetActive(showCropAreaOverlay);
        
        // Add visual information about bounds
        Debug.Log($"Canvas Crop Area: Position=({minX}, {minY}), Size=({width} x {height})");
        
        // Draw diagonal lines across the crop area for better visualization
        if (showCropAreaOverlay && showDebugVisualizations)
        {
            // Create diagonal line objects if needed
            GameObject diagonal1 = new GameObject("Diagonal1");
            GameObject diagonal2 = new GameObject("Diagonal2");
            
            // Add to visualizations for cleanup
            debugVisualizations.Add(diagonal1);
            debugVisualizations.Add(diagonal2);
            
            // Add as children of the canvas
            diagonal1.transform.SetParent(cropPreviewCanvas.transform, false);
            diagonal2.transform.SetParent(cropPreviewCanvas.transform, false);
            
            // Add line renderers
            RectTransform rt1 = diagonal1.AddComponent<RectTransform>();
            RectTransform rt2 = diagonal2.AddComponent<RectTransform>();
            
            // Add image components
            Image img1 = diagonal1.AddComponent<Image>();
            Image img2 = diagonal2.AddComponent<Image>();
            
            // Set colors
            img1.color = new Color(1f, 1f, 0f, 0.7f); // Yellow
            img2.color = new Color(1f, 0f, 1f, 0.7f); // Magenta
            
            // Position and size diagonals
            rt1.anchorMin = Vector2.zero;
            rt1.anchorMax = Vector2.zero;
            rt1.pivot = Vector2.zero;
            rt1.anchoredPosition = new Vector2(minX, minY);
            rt1.sizeDelta = new Vector2(width, height);
            
            rt2.anchorMin = Vector2.zero;
            rt2.anchorMax = Vector2.zero;
            rt2.pivot = Vector2.zero;
            rt2.anchoredPosition = new Vector2(minX, minY);
            rt2.sizeDelta = new Vector2(width, height);
            
            // Add line components to visualize diagonals
            LineRenderer line1 = diagonal1.AddComponent<LineRenderer>();
            LineRenderer line2 = diagonal2.AddComponent<LineRenderer>();
            
            // Configure line renderers for UI visualization
            line1.positionCount = 2;
            line2.positionCount = 2;
            
            // Set positions in screen space
            // Diagonal 1: top-left to bottom-right
            Vector2 topLeft = new Vector2(minX, minY + height);
            Vector2 bottomRight = new Vector2(minX + width, minY);
            
            // Diagonal 2: bottom-left to top-right
            Vector2 bottomLeft = new Vector2(minX, minY);
            Vector2 topRight = new Vector2(minX + width, minY + height);
            
            // Convert to world positions for line renderer
            Camera canvasCamera = cropPreviewCanvas.worldCamera;
            Vector3 worldTopLeft = canvasCamera.ScreenToWorldPoint(new Vector3(topLeft.x, topLeft.y, 10));
            Vector3 worldBottomRight = canvasCamera.ScreenToWorldPoint(new Vector3(bottomRight.x, bottomRight.y, 10));
            Vector3 worldBottomLeft = canvasCamera.ScreenToWorldPoint(new Vector3(bottomLeft.x, bottomLeft.y, 10));
            Vector3 worldTopRight = canvasCamera.ScreenToWorldPoint(new Vector3(topRight.x, topRight.y, 10));
            
            // Set line positions
            line1.SetPosition(0, worldTopLeft);
            line1.SetPosition(1, worldBottomRight);
            line2.SetPosition(0, worldBottomLeft);
            line2.SetPosition(1, worldTopRight);
            
            // Set line properties
            line1.startWidth = line2.startWidth = 0.001f;
            line1.endWidth = line2.endWidth = 0.001f;
            
            // Apply material
            if (debugLineMaterial != null)
            {
                line1.material = line2.material = debugLineMaterial;
            }
            else
            {
                line1.material = line2.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            }
        }
    }

    /// <summary>
    /// Handles the event when a surface is fully completed with all four points
    /// </summary>
    /// <param name="point1">First corner point</param>
    /// <param name="point2">Second corner point</param>
    /// <param name="point3">Third corner point</param>
    /// <param name="point4">Fourth corner point</param>
    private void HandleSurfaceCompleted(Vector3 point1, Vector3 point2, Vector3 point3, Vector3 point4)
    {
        Debug.Log($"Surface fully completed with corners at {point1}, {point2}, {point3}, {point4} - Preparing to scan with OCR");
        
        // Store the surface corners for later use
        surfacePoint1 = point1;
        surfacePoint2 = point2;
        surfacePoint3 = point3;
        surfacePoint4 = point4;
        
        // Get a reference to the surface object
        lastScannedSurface = GameObject.Find("DragSurface");
        
        // Clean up any previous visualizations
        ClearDebugVisualizations();
        
        // Clear previous OCR lines if enabled
        if (clearPreviousOCRLines)
        {
            ClearOCRLines();
        }
        
        // Process the surface crop and perform OCR
        if (ocrDelay > 0)
        {
            StartCoroutine(DelayedSurfaceCrop(point1, point2, point3, point4));
        }
        else
        {
            PerformSurfaceCrop(point1, point2, point3, point4);
        }
    }

    /// <summary>
    /// Coroutine to delay surface cropping and OCR
    /// </summary>
    private IEnumerator DelayedSurfaceCrop(Vector3 point1, Vector3 point2, Vector3 point3, Vector3 point4)
    {
        yield return new WaitForSeconds(ocrDelay);
        PerformSurfaceCrop(point1, point2, point3, point4);
    }

    private void PerformSurfaceCrop(Vector3 point1, Vector3 point2, Vector3 point3, Vector3 point4)
    {
        // Visualize the camera frustum first (before any projections)
        if (showDebugVisualizations)
        {
            VisualizeVirtualCameraFrustum();
        }
        
        // Surface corners in world space
        Vector3[] worldCorners = new Vector3[] { point1, point2, point3, point4 };
        
        // Visualize rays
        if (showDebugVisualizations)
        {
            for (int i = 0; i < 4; i++)
            {
                // Draw rays from camera to surface points
                VisualizeSurfaceRay(offsetNode.position, worldCorners[i], Color.yellow);
            }
        }
        
        // Calculate the frustum parameters for the near plane
        float halfHeightNear = offsetNodeNear * Mathf.Tan(offsetNodeFOV * 0.5f * Mathf.Deg2Rad);
        float halfWidthNear = halfHeightNear * offsetNodeAspect;
        
        // Define the corners of the near plane in local space
        Vector3 nearPlaneTopLeft = new Vector3(-halfWidthNear, halfHeightNear, offsetNodeNear);
        Vector3 nearPlaneTopRight = new Vector3(halfWidthNear, halfHeightNear, offsetNodeNear);
        Vector3 nearPlaneBottomLeft = new Vector3(-halfWidthNear, -halfHeightNear, offsetNodeNear);
        Vector3 nearPlaneBottomRight = new Vector3(halfWidthNear, -halfHeightNear, offsetNodeNear);
        
        // Convert to world space to visualize the near plane corners if needed
        Vector3 worldTopLeft = offsetNode.TransformPoint(nearPlaneTopLeft);
        Vector3 worldTopRight = offsetNode.TransformPoint(nearPlaneTopRight);
        Vector3 worldBottomLeft = offsetNode.TransformPoint(nearPlaneBottomLeft);
        Vector3 worldBottomRight = offsetNode.TransformPoint(nearPlaneBottomRight);
        
        // Create a plane at the near clip distance
        Plane nearPlane = new Plane(offsetNode.forward, offsetNode.position + offsetNode.forward * offsetNodeNear);
        
        // Find where our rays intersect the near plane
        Vector3[] nearPlaneIntersections = new Vector3[4];
        bool[] validIntersections = new bool[4];
        int validCount = 0;
        
        for (int i = 0; i < 4; i++)
        {
            // Cast a ray from camera to the corner point
            Ray ray = new Ray(offsetNode.position, (worldCorners[i] - offsetNode.position).normalized);
            
            if (nearPlane.Raycast(ray, out float enter))
            {
                // Get intersection point on near plane
                nearPlaneIntersections[i] = ray.GetPoint(enter);
                validIntersections[i] = true;
                validCount++;
                
                // Visualize the intersection point
                if (showDebugVisualizations)
                {
                    VisualizePoint(nearPlaneIntersections[i], 0.01f, Color.cyan);
                }
            }
            else
            {
                Debug.LogWarning($"Ray to point {i} doesn't intersect near plane.");
                validIntersections[i] = false;
            }
        }
        
        // If we don't have at least 3 valid intersections, we can't reliably crop
        if (validCount < 3)
        {
            Debug.LogError("Not enough valid intersections with the near plane to perform crop.");
            return;
        }
        
        // Calculate UV coordinates on the near plane for each intersection point
        Vector2[] uvCoordinates = new Vector2[4];
        
        for (int i = 0; i < 4; i++)
        {
            if (!validIntersections[i]) continue;
            
            // Convert to local space relative to the camera
            Vector3 localPoint = offsetNode.InverseTransformPoint(nearPlaneIntersections[i]);
            
            // Calculate UV coordinates on the near plane (0,0 at bottom-left, 1,1 at top-right)
            float u = (localPoint.x + halfWidthNear) / (2 * halfWidthNear);
            float v = (localPoint.y + halfHeightNear) / (2 * halfHeightNear);
            
            uvCoordinates[i] = new Vector2(u, v);
        }
        
        // Find min/max of UV coordinates (only using valid intersections)
        float minU = 1f, minV = 1f;
        float maxU = 0f, maxV = 0f;
        
        for (int i = 0; i < 4; i++)
        {
            if (!validIntersections[i]) continue;
            
            minU = Mathf.Min(minU, uvCoordinates[i].x);
            minV = Mathf.Min(minV, uvCoordinates[i].y);
            maxU = Mathf.Max(maxU, uvCoordinates[i].x);
            maxV = Mathf.Max(maxV, uvCoordinates[i].y);
        }
        
        // Clamp UV coordinates to the valid range [0,1]
        minU = Mathf.Clamp01(minU);
        minV = Mathf.Clamp01(minV);
        maxU = Mathf.Clamp01(maxU);
        maxV = Mathf.Clamp01(maxV);
        
        // Convert UV coordinates to pixel coordinates in the render texture
        float minX = minU * cameraRenderTex.width;
        float minY = minV * cameraRenderTex.height;
        float maxX = maxU * cameraRenderTex.width;
        float maxY = maxV * cameraRenderTex.height;
        
        // Adjust Y coordinates - this is now unnecessary, as the UV coordinates are 
        // already in the correct orientation for GetPixels
        // (0,0) at bottom left matches Unity's internal texture coordinate system
        
        // Log original pixel coordinates before any transformation
        Debug.Log($"Original pixel coordinates: ({minX},{minY}) to ({maxX},{maxY})");
        Debug.Log($"These coordinates match UV system: (0,0) at bottom-left, (1,1) at top-right");
        
        // Calculate final crop dimensions, ensuring they're within the texture bounds
        minX = Mathf.Max(0, minX);
        minY = Mathf.Max(0, minY);
        maxX = Mathf.Min(cameraRenderTex.width, maxX);
        maxY = Mathf.Min(cameraRenderTex.height, maxY);
        
        int cropWidth = Mathf.FloorToInt(maxX - minX);
        int cropHeight = Mathf.FloorToInt(maxY - minY);
        
        // Ensure non-zero dimensions with a minimum reasonable size
        cropWidth = Mathf.Max(10, cropWidth);
        cropHeight = Mathf.Max(10, cropHeight);
        
        Debug.Log($"UV coordinates on near plane: ({minU},{minV}) to ({maxU},{maxV})");
        Debug.Log($"Crop dimensions: {cropWidth}x{cropHeight}, bounds: ({minX},{minY}) to ({maxX},{maxY})");
        
        // Visualize the crop rectangle in world space
        if (showDebugVisualizations)
        {
            VisualizeCropRectangleFromUV(minU, minV, maxU, maxV);
        }
        
        // Update the canvas crop area visualizer
        UpdateCanvasCropAreaVisualizer(minX, minY, maxX, maxY);
        
        // Remember current RenderTexture
        RenderTexture prevRT = RenderTexture.active;
        
        // Create a temporary Texture2D to read from the render texture
        Texture2D fullScreenTexture = new Texture2D(cameraRenderTex.width, cameraRenderTex.height, TextureFormat.RGBA32, false);
        
        try
        {
            // Set the camera render texture as active
            RenderTexture.active = cameraRenderTex;
            
            // Read the entire screen
            fullScreenTexture.ReadPixels(new Rect(0, 0, cameraRenderTex.width, cameraRenderTex.height), 0, 0);
            fullScreenTexture.Apply();
            
            // Create the cropped texture with exact dimensions
            Texture2D croppedTexture = new Texture2D(cropWidth, cropHeight, TextureFormat.RGBA32, false);
            
            // Make sure we have valid x,y starting points
            int startX = Mathf.FloorToInt(minX);
            
            // The key issue: we need to get pixels from the correct Y position in the texture
            // Unity textures have the origin (0,0) at the bottom-left, while UV coordinates 
            // also have (0,0) at the bottom-left, but our display and visualization use top-left as (0,0)
            
            // Convert both min and max to the texture coordinate system 
            int startY = Mathf.FloorToInt(minY);  // minY is already in the right orientation for GetPixels
            
            // Log more debugging for clarity
            Debug.Log($"UV in original space: minV={minV}, maxV={maxV}");
            Debug.Log($"Pixel coords in original space: minY={minY}, maxY={maxY}");
            Debug.Log($"Using startY={startY} for GetPixels (origin at bottom)");
            
            // Double-check dimensions to ensure they don't exceed texture bounds
            if (startX + cropWidth > fullScreenTexture.width)
                cropWidth = fullScreenTexture.width - startX;
                
            if (startY + cropHeight > fullScreenTexture.height)
                cropHeight = fullScreenTexture.height - startY;
            
            // Make sure we don't have negative starting points
            if (startX < 0) {
                cropWidth += startX; // Reduce width by the negative amount
                startX = 0;
            }
            
            if (startY < 0) {
                cropHeight += startY; // Reduce height by the negative amount
                startY = 0;
            }
                
            // Debug current values
            Debug.Log($"Extracting crop at ({startX}, {startY}) with dimensions {cropWidth}x{cropHeight}");
            Debug.Log($"Source texture dimensions: {fullScreenTexture.width}x{fullScreenTexture.height}");
            
            try {
                // Extract pixel data directly from the full screen texture to the cropped texture
                Color[] pixels = fullScreenTexture.GetPixels(
                    startX, 
                    startY, 
                    cropWidth, 
                    cropHeight);
                    
                // Set pixels in the cropped texture
                croppedTexture.SetPixels(pixels);
                croppedTexture.Apply();
                
                // Store the cropped texture and clean up the full screen texture
                lastCroppedTexture = croppedTexture;
            }
            catch (System.Exception e) {
                Debug.LogError($"Error during pixel extraction: {e.Message}");
                // Create a fallback texture for debugging
                lastCroppedTexture = new Texture2D(2, 2);
                lastCroppedTexture.SetPixel(0, 0, Color.red);
                lastCroppedTexture.SetPixel(1, 0, Color.blue);
                lastCroppedTexture.SetPixel(0, 1, Color.green);
                lastCroppedTexture.SetPixel(1, 1, Color.yellow);
                lastCroppedTexture.Apply();
            }
            
            Destroy(fullScreenTexture);
            
            // Add visual debugging (draw red outline on the preview)
            AddDebugOutline(lastCroppedTexture, Color.red);
            
            // Display preview if enabled
            if (showPreviewAutomatically && previewPanel != null && previewImage != null)
            {
                ShowPreview(previewDuration);
            }
            
            // Use for OCR
            if (ocrComponent != null)
            {
                ocrComponent.SetSourceTexture(lastCroppedTexture);
                Debug.Log($"Cropped surface image with dimensions {cropWidth}x{cropHeight}");
                PerformOCRScan();
            }
            
            // Auto-hide visualizations after a delay if configured
            if (showDebugVisualizations && visualizationDuration > 0)
            {
                StartCoroutine(ClearVisualizationsAfterDelay(visualizationDuration));
            }

            // Store crop dimensions for later use in text positioning
            croppedTextureWidth = cropWidth;
            croppedTextureHeight = cropHeight;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error during surface crop: {e.Message}\n{e.StackTrace}");
            Destroy(fullScreenTexture);
        }
        finally
        {
            // Restore previous render texture
            RenderTexture.active = prevRT;
        }
    }
    
    /// <summary>
    /// Visualizes the virtual camera frustum using lines
    /// </summary>
    private void VisualizeVirtualCameraFrustum()
    {
        if (offsetNode == null) return;
        
        // Calculate frustum corners
        float halfHeightNear = offsetNodeNear * Mathf.Tan(offsetNodeFOV * 0.5f * Mathf.Deg2Rad);
        float halfWidthNear = halfHeightNear * offsetNodeAspect;
        
        float halfHeightFar = offsetNodeFar * Mathf.Tan(offsetNodeFOV * 0.5f * Mathf.Deg2Rad);
        float halfWidthFar = halfHeightFar * offsetNodeAspect;
        
        // Define corner points in local space
        Vector3 nearBottomLeft = new Vector3(-halfWidthNear, -halfHeightNear, offsetNodeNear);
        Vector3 nearBottomRight = new Vector3(halfWidthNear, -halfHeightNear, offsetNodeNear);
        Vector3 nearTopRight = new Vector3(halfWidthNear, halfHeightNear, offsetNodeNear);
        Vector3 nearTopLeft = new Vector3(-halfWidthNear, halfHeightNear, offsetNodeNear);
        
        Vector3 farBottomLeft = new Vector3(-halfWidthFar, -halfHeightFar, offsetNodeFar);
        Vector3 farBottomRight = new Vector3(halfWidthFar, -halfHeightFar, offsetNodeFar);
        Vector3 farTopRight = new Vector3(halfWidthFar, halfHeightFar, offsetNodeFar);
        Vector3 farTopLeft = new Vector3(-halfWidthFar, halfHeightFar, offsetNodeFar);
        
        // Transform to world space
        nearBottomLeft = offsetNode.TransformPoint(nearBottomLeft);
        nearBottomRight = offsetNode.TransformPoint(nearBottomRight);
        nearTopRight = offsetNode.TransformPoint(nearTopRight);
        nearTopLeft = offsetNode.TransformPoint(nearTopLeft);
        
        farBottomLeft = offsetNode.TransformPoint(farBottomLeft);
        farBottomRight = offsetNode.TransformPoint(farBottomRight);
        farTopRight = offsetNode.TransformPoint(farTopRight);
        farTopLeft = offsetNode.TransformPoint(farTopLeft);
        
        // Draw near rectangle
        CreateDebugLine(nearBottomLeft, nearBottomRight, Color.magenta);
        CreateDebugLine(nearBottomRight, nearTopRight, Color.magenta);
        CreateDebugLine(nearTopRight, nearTopLeft, Color.magenta);
        CreateDebugLine(nearTopLeft, nearBottomLeft, Color.magenta);
        
        // Draw far rectangle
        CreateDebugLine(farBottomLeft, farBottomRight, Color.magenta);
        CreateDebugLine(farBottomRight, farTopRight, Color.magenta);
        CreateDebugLine(farTopRight, farTopLeft, Color.magenta);
        CreateDebugLine(farTopLeft, farBottomLeft, Color.magenta);
        
        // Connect near and far rectangles
        CreateDebugLine(nearBottomLeft, farBottomLeft, Color.magenta);
        CreateDebugLine(nearBottomRight, farBottomRight, Color.magenta);
        CreateDebugLine(nearTopRight, farTopRight, Color.magenta);
        CreateDebugLine(nearTopLeft, farTopLeft, Color.magenta);
        
        // Draw a direction line from the camera forward
        CreateDebugLine(offsetNode.position, offsetNode.position + offsetNode.forward * offsetNodeFar * 0.5f, Color.red);
    }
    
    /// <summary>
    /// Visualizes a ray from the camera to a surface point
    /// </summary>
    private void VisualizeSurfaceRay(Vector3 origin, Vector3 target, Color color)
    {
        CreateDebugLine(origin, target, color);
    }
    
    /// <summary>
    /// Visualizes the crop rectangle in world space based on UV coordinates
    /// </summary>
    private void VisualizeCropRectangleFromUV(float minU, float minV, float maxU, float maxV)
    {
        // Calculate the frustum parameters for the near plane
        float halfHeightNear = offsetNodeNear * Mathf.Tan(offsetNodeFOV * 0.5f * Mathf.Deg2Rad);
        float halfWidthNear = halfHeightNear * offsetNodeAspect;
        
        // Convert UV coordinates to positions on the near plane in local space
        float leftX = Mathf.Lerp(-halfWidthNear, halfWidthNear, minU);
        float rightX = Mathf.Lerp(-halfWidthNear, halfWidthNear, maxU);
        float bottomY = Mathf.Lerp(-halfHeightNear, halfHeightNear, minV);
        float topY = Mathf.Lerp(-halfHeightNear, halfHeightNear, maxV);
        
        // Create corner points in local space
        Vector3 localBottomLeft = new Vector3(leftX, bottomY, offsetNodeNear);
        Vector3 localBottomRight = new Vector3(rightX, bottomY, offsetNodeNear);
        Vector3 localTopRight = new Vector3(rightX, topY, offsetNodeNear);
        Vector3 localTopLeft = new Vector3(leftX, topY, offsetNodeNear);
        
        // Transform to world space
        Vector3 bottomLeft = offsetNode.TransformPoint(localBottomLeft);
        Vector3 bottomRight = offsetNode.TransformPoint(localBottomRight);
        Vector3 topRight = offsetNode.TransformPoint(localTopRight);
        Vector3 topLeft = offsetNode.TransformPoint(localTopLeft);
        
        // Draw the crop rectangle
        CreateDebugLine(bottomLeft, bottomRight, Color.green);
        CreateDebugLine(bottomRight, topRight, Color.green);
        CreateDebugLine(topRight, topLeft, Color.green);
        CreateDebugLine(topLeft, bottomLeft, Color.green);
        
        // Create the rectangle visualizer if it doesn't exist
        if (cropRectVisualizer == null)
        {
            cropRectVisualizer = new GameObject("CropRectVisualizer");
            debugVisualizations.Add(cropRectVisualizer);
            
            // Add a mesh filter, renderer and material
            MeshFilter meshFilter = cropRectVisualizer.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = cropRectVisualizer.AddComponent<MeshRenderer>();
            
            // Create the mesh
            Mesh mesh = new Mesh();
            Vector3[] vertices = new Vector3[] { bottomLeft, bottomRight, topRight, topLeft };
            int[] triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            
            meshFilter.mesh = mesh;
            
            // Set material
            if (debugLineMaterial != null)
            {
                meshRenderer.material = new Material(debugLineMaterial);
            }
            else
            {
                meshRenderer.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            }
            
            // Set color with transparency
            meshRenderer.material.color = new Color(0, 1, 0, 0.2f); // Semi-transparent green
        }
    }
    
    /// <summary>
    /// Visualizes a point in 3D space
    /// </summary>
    private void VisualizePoint(Vector3 position, float size, Color color)
    {
        GameObject point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        point.name = "DebugPoint";
        point.transform.position = position;
        point.transform.localScale = Vector3.one * size;
        
        Renderer renderer = point.GetComponent<Renderer>();
        
        // Set material and color
        if (debugLineMaterial != null)
        {
            renderer.material = new Material(debugLineMaterial);
        }
        else
        {
            renderer.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        }
        
        renderer.material.color = color;
        
        // Add to list for cleanup
        debugVisualizations.Add(point);
    }
    
    /// <summary>
    /// Creates a debug line between two points
    /// </summary>
    private void CreateDebugLine(Vector3 start, Vector3 end, Color color)
    {
        GameObject lineObj = new GameObject("DebugLine");
        debugVisualizations.Add(lineObj);
        
        LineRenderer lineRenderer = lineObj.AddComponent<LineRenderer>();
        lineRenderer.positionCount = 2;
        lineRenderer.SetPosition(0, start);
        lineRenderer.SetPosition(1, end);
        
        lineRenderer.startWidth = debugLineWidth;
        lineRenderer.endWidth = debugLineWidth;
        
        // Set material
        if (debugLineMaterial != null)
        {
            lineRenderer.material = debugLineMaterial;
        }
        else
        {
            lineRenderer.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        }
        
        lineRenderer.material.color = color;
    }
    
    /// <summary>
    /// Clears all debug visualizations
    /// </summary>
    public void ClearDebugVisualizations()
    {
        foreach (GameObject obj in debugVisualizations)
        {
            if (obj != null)
            {
                Destroy(obj);
            }
        }
        
        debugVisualizations.Clear();
        cropRectVisualizer = null;
    }
    
    /// <summary>
    /// Coroutine to clear visualizations after a delay
    /// </summary>
    private IEnumerator ClearVisualizationsAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        ClearDebugVisualizations();
    }
    
    /// <summary>
    /// Adds a colored outline to the texture for debugging
    /// </summary>
    private void AddDebugOutline(Texture2D texture, Color color)
    {
        if (texture == null) return;
        
        int width = texture.width;
        int height = texture.height;
        
        // Add a 1-pixel border around the edge
        for (int x = 0; x < width; x++)
        {
            // Top and bottom edges
            texture.SetPixel(x, 0, color);
            texture.SetPixel(x, height - 1, color);
        }
        
        for (int y = 0; y < height; y++)
        {
            // Left and right edges
            texture.SetPixel(0, y, color);
            texture.SetPixel(width - 1, y, color);
        }
        
        texture.Apply();
    }

    /// <summary>
    /// Performs the actual OCR scan
    /// </summary>
    private void PerformOCRScan()
    {
        if (ocrComponent != null)
        {
            Debug.Log("Starting OCR scan...");
            ocrComponent.StartOCR();
        }
        else
        {
            Debug.LogError("Cannot perform OCR scan: OCR component is missing");
        }
    }

    /// <summary>
    /// Public method to manually trigger OCR scan
    /// </summary>
    [ContextMenu("Trigger OCR Scan")]
    public void TriggerOCRScan()
    {
        PerformOCRScan();
    }
    
    /// <summary>
    /// Shows the preview of the last cropped texture
    /// </summary>
    /// <param name="duration">How long to show the preview (0 = indefinite)</param>
    public void ShowPreview(float duration = 0f)
    {
        if (previewPanel == null || previewImage == null || lastCroppedTexture == null)
        {
            Debug.LogWarning("Cannot show preview: missing panel, image component, or no cropped texture available");
            return;
        }
        
        // Set the cropped texture to the preview image
        previewImage.texture = lastCroppedTexture;
        
        // Show the preview panel
        previewPanel.SetActive(true);
        
        // If duration is set, hide after delay
        if (duration > 0)
        {
            StartCoroutine(HidePreviewAfterDelay(duration));
        }
    }
    
    /// <summary>
    /// Hides the preview panel
    /// </summary>
    public void HidePreview()
    {
        if (previewPanel != null)
        {
            previewPanel.SetActive(false);
        }
    }
    
    /// <summary>
    /// Coroutine to hide the preview after a delay
    /// </summary>
    private IEnumerator HidePreviewAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        HidePreview();
    }
    
    /// <summary>
    /// Save the last cropped texture to a PNG file (useful for debugging)
    /// </summary>
    [ContextMenu("Save Cropped Texture")]
    public void SaveCroppedTexture()
    {
        if (lastCroppedTexture == null)
        {
            Debug.LogWarning("No cropped texture available to save");
            return;
        }
        
        try
        {
            byte[] bytes = lastCroppedTexture.EncodeToPNG();
            string fileName = $"CroppedSurface_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
            string path = System.IO.Path.Combine(Application.persistentDataPath, fileName);
            System.IO.File.WriteAllBytes(path, bytes);
            Debug.Log($"Saved cropped texture to: {path}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error saving cropped texture: {e.Message}");
        }
    }

    /// <summary>
    /// Handles OCR results and places text on the surface
    /// </summary>
    private void HandleOCRComplete(string fullText, List<CloudVisionOCRUnified.LineData> lines)
    {
        Debug.Log($"OCR completed with {lines.Count} lines of text");
        
        // Check if we have a valid surface to place text on
        if (lastScannedSurface == null)
        {
            Debug.LogWarning("No valid surface found to place OCR text. Make sure the surface exists.");
            return;
        }
        
        // Check if we have a valid OCR line prefab
        if (ocrLinePrefab == null)
        {
            Debug.LogError("OCRLine prefab not assigned. Can't create OCR text.");
            return;
        }
        
        // Create a container for the OCR lines if not assigned
        if (ocrLineContainer == null)
        {
            GameObject container = new GameObject("OCRLineContainer");
            ocrLineContainer = container.transform;
        }
        
        // Calculate surface dimensions and orientation
        Vector3 surfaceWidth = surfacePoint2 - surfacePoint1;
        Vector3 surfaceHeight = surfacePoint3 - surfacePoint2;
        Vector3 surfaceNormal = Vector3.Cross(surfaceWidth, surfaceHeight).normalized;
        
        // Calculate surface center
        Vector3 surfaceCenter = (surfacePoint1 + surfacePoint2 + surfacePoint3 + surfacePoint4) / 4f;
        
        // Calculate surface dimensions
        float surfaceWidthMagnitude = surfaceWidth.magnitude;
        float surfaceHeightMagnitude = surfaceHeight.magnitude;
        
        // Create right and up vectors for the surface
        Vector3 surfaceRight = surfaceWidth.normalized;
        Vector3 surfaceUp = Vector3.Cross(surfaceNormal, surfaceRight).normalized;
        
        Debug.Log($"Surface dimensions: {surfaceWidthMagnitude} x {surfaceHeightMagnitude}");
        Debug.Log($"Cropped texture dimensions: {croppedTextureWidth} x {croppedTextureHeight}");
        
        // Create a rotation for the surface plane
        Quaternion surfaceRotation = Quaternion.LookRotation(surfaceNormal, surfaceUp);
        
        // Process each OCR line
        foreach (CloudVisionOCRUnified.LineData line in lines)
        {
            // Skip lines with no bounding box or empty text
            if (line.boundingBox.width <= 0 || line.boundingBox.height <= 0 || string.IsNullOrEmpty(line.text))
            {
                continue;
            }
            
            // Calculate position on the surface based on the bounding box
            float centerX = line.boundingBox.x + (line.boundingBox.width / 2f);
            float centerY = line.boundingBox.y + (line.boundingBox.height / 2f);
            
            float normalizedX = centerX / croppedTextureWidth;
            float normalizedY = centerY / croppedTextureHeight;
            
            // Calculate position in world space on the surface
            Vector3 linePosition = surfacePoint1 + 
                                  surfaceRight * (normalizedX * surfaceWidthMagnitude) + 
                                  surfaceUp * (normalizedY * surfaceHeightMagnitude);
            
            // Add slight offset to prevent z-fighting
            linePosition += surfaceNormal * textZOffset;
            
            // Calculate scale based on bounding box and surface dimensions
            float lineWidth = line.boundingBox.width / croppedTextureWidth * surfaceWidthMagnitude;
            float lineHeight = line.boundingBox.height / croppedTextureHeight * surfaceHeightMagnitude;
            
            // Create the OCR line object
            GameObject ocrLine = Instantiate(ocrLinePrefab, linePosition, surfaceRotation, ocrLineContainer);
            ocrLine.name = $"OCRLine_{line.text}";
            
            // Get the text component
            Transform textTransform = ocrLine.transform.Find("Text (TMP)");
            Transform backgroundTransform = ocrLine.transform.Find("Background");
            
            if (textTransform != null && backgroundTransform != null)
            {
                // Set the text content
                TextMeshPro textMesh = textTransform.GetComponent<TextMeshPro>();
                if (textMesh != null)
                {
                    textMesh.text = line.text;
                    
                    // Scale the text based on bounding box and adjust for readability
                    float textScale = Mathf.Min(lineWidth, lineHeight) * textScaleFactor;
                    textMesh.rectTransform.localScale = new Vector3(textScale, textScale, textScale);
                    
                    // Center the text
                    textMesh.alignment = TextAlignmentOptions.Center;
                }
                
                // Scale and position the background to match the text bounding box
                backgroundTransform.localScale = new Vector3(lineWidth, lineHeight, 0.001f);
                
                // Position background to align with the text bounding box's center
                backgroundTransform.localPosition = Vector3.zero; // Center at the spawned position
                
                // Adjust the spawn position instead
                linePosition = surfacePoint1 + 
                             surfaceRight * (normalizedX * surfaceWidthMagnitude) + 
                             surfaceUp * (normalizedY * surfaceHeightMagnitude);
                
                Debug.Log($"Created OCR line '{line.text}' at position {linePosition}, " + 
                          $"with size {lineWidth} x {lineHeight}");
            }
            else
            {
                Debug.LogWarning($"OCRLine prefab doesn't have expected children: 'Text (TMP)' and 'Background'");
            }
            
            // Add to created lines list for cleanup
            createdOCRLines.Add(ocrLine);
        }
    }
    
    /// <summary>
    /// Clears all OCR lines created previously
    /// </summary>
    public void ClearOCRLines()
    {
        foreach (GameObject ocrLine in createdOCRLines)
        {
            if (ocrLine != null)
            {
                Destroy(ocrLine);
            }
        }
        
        createdOCRLines.Clear();
    }
} 