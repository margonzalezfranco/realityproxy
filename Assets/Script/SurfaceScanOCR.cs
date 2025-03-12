using UnityEngine;
using System.Collections;
using UnityEngine.UI; // Add for UI components

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
    
    // Reference to the last cropped texture
    private Texture2D lastCroppedTexture;
    
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

        // Only subscribe to surface completion events
        DragSurface.OnSurfaceCompleted += HandleSurfaceCompleted;
    }

    // Unsubscribe from events when this object is destroyed
    private void OnDestroy()
    {
        // Only unsubscribe from surface completion events
        DragSurface.OnSurfaceCompleted -= HandleSurfaceCompleted;
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
        // Check if we have an offset node to use
        if (offsetNode == null)
        {
            Debug.LogWarning("No offset node assigned. Using main camera coordinates without offset.");
            PerformSurfaceCropWithMainCamera(point1, point2, point3, point4);
            return;
        }
        
        // Try both projection methods and use the one that works better
        bool useAlternativeMethod = false; // Set to true to try the alternative method
        
        Vector2 screenPoint1, screenPoint2, screenPoint3, screenPoint4;
        
        if (useAlternativeMethod)
        {
            // Use ray-plane intersection method
            screenPoint1 = WorldToScreenPointAlternative(point1);
            screenPoint2 = WorldToScreenPointAlternative(point2);
            screenPoint3 = WorldToScreenPointAlternative(point3);
            screenPoint4 = WorldToScreenPointAlternative(point4);
            Debug.Log("Using alternative projection method");
        }
        else
        {
            // Use standard projection method
            screenPoint1 = WorldToScreenPointVirtualCamera(point1);
            screenPoint2 = WorldToScreenPointVirtualCamera(point2);
            screenPoint3 = WorldToScreenPointVirtualCamera(point3);
            screenPoint4 = WorldToScreenPointVirtualCamera(point4);
        }
        
        // Log the screen coordinates
        Debug.Log($"Virtual camera screen coordinates: ({screenPoint1}), ({screenPoint2}), ({screenPoint3}), ({screenPoint4})");

        // Find the bounds of the quadrilateral
        float minX = Mathf.Min(screenPoint1.x, screenPoint2.x, screenPoint3.x, screenPoint4.x);
        float minY = Mathf.Min(screenPoint1.y, screenPoint2.y, screenPoint3.y, screenPoint4.y);
        float maxX = Mathf.Max(screenPoint1.x, screenPoint2.x, screenPoint3.x, screenPoint4.x);
        float maxY = Mathf.Max(screenPoint1.y, screenPoint2.y, screenPoint3.y, screenPoint4.y);

        // Calculate crop dimensions, ensuring they're within the screen bounds
        minX = Mathf.Max(0, minX);
        minY = Mathf.Max(0, minY);
        maxX = Mathf.Min(cameraRenderTex.width, maxX);
        maxY = Mathf.Min(cameraRenderTex.height, maxY);
        
        int cropWidth = Mathf.FloorToInt(maxX - minX);
        int cropHeight = Mathf.FloorToInt(maxY - minY);
        
        // Ensure non-zero dimensions
        cropWidth = Mathf.Max(1, cropWidth);
        cropHeight = Mathf.Max(1, cropHeight);
        
        Debug.Log($"Crop dimensions: {cropWidth}x{cropHeight}, bounds: ({minX},{minY}) to ({maxX},{maxY})");

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
            
            // Extract pixel data directly from the full screen texture to the cropped texture
            Color[] pixels = fullScreenTexture.GetPixels(
                Mathf.FloorToInt(minX), 
                Mathf.FloorToInt(minY), 
                cropWidth, 
                cropHeight);
                
            // Set pixels in the cropped texture
            croppedTexture.SetPixels(pixels);
            croppedTexture.Apply();
            
            // Store the cropped texture and clean up the full screen texture
            lastCroppedTexture = croppedTexture;
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
    /// Transforms a world point to screen coordinates as if viewed by a virtual camera at the offset node
    /// </summary>
    private Vector2 WorldToScreenPointVirtualCamera(Vector3 worldPoint)
    {
        // The issue appears to be with the camera orientation - we need a simpler approach
        // Let's use the GeminiRaycast approach which is known to work

        // First, get the point in the offsetNode's local space
        Vector3 localPoint = offsetNode.InverseTransformPoint(worldPoint);
        
        // Convert to normalized screen coordinates using a simplified approach
        // The camera is assumed to be looking down the negative Z axis
        // We need to invert some coordinates because we're getting "behind camera" warnings
        
        // Calculate the distance to the projection plane
        float distance = -localPoint.z; // Negate z because we're looking down negative z
        
        if (distance <= 0)
        {
            // Point is behind the camera, use a very small positive value instead
            Debug.LogWarning($"Point {worldPoint} is behind or at the virtual camera plane. Using fallback projection.");
            distance = 0.001f; // Small positive value
        }
        
        // Calculate the field of view in radians
        float fovRadians = offsetNodeFOV * Mathf.Deg2Rad;
        float tangent = Mathf.Tan(fovRadians * 0.5f);
        
        // Calculate normalized device coordinates (NDC) in the range [-1, 1]
        float ndcX = localPoint.x / (distance * tangent * offsetNodeAspect);
        float ndcY = localPoint.y / (distance * tangent);
        
        // Convert NDC to screen coordinates
        float screenX = (ndcX + 1) * 0.5f * cameraRenderTex.width;
        float screenY = (1 - (ndcY + 1) * 0.5f) * cameraRenderTex.height; // Flip Y
        
        // On VisionOS the camera might be flipped differently - try flipping X and/or Y if needed
        // screenX = cameraRenderTex.width - screenX; // Uncomment if X needs to be flipped
        // screenY = cameraRenderTex.height - screenY; // Uncomment if Y needs to be flipped
        
        return new Vector2(screenX, screenY);
    }
    
    /// <summary>
    /// Alternative projection method that uses ray-plane intersection instead of projection matrix
    /// Can be more reliable for certain camera configurations
    /// </summary>
    private Vector2 WorldToScreenPointAlternative(Vector3 worldPoint)
    {
        // Create a plane perpendicular to the camera's forward direction
        Plane imagePlane = new Plane(offsetNode.forward, offsetNode.position + offsetNode.forward * offsetNodeNear);
        
        // Cast a ray from the camera position through the world point
        Ray ray = new Ray(offsetNode.position, (worldPoint - offsetNode.position).normalized);
        
        if (imagePlane.Raycast(ray, out float enter))
        {
            // Get the intersection point on the plane
            Vector3 intersectionPoint = ray.GetPoint(enter);
            
            // Convert the intersection point to the camera's local space
            Vector3 localPoint = offsetNode.InverseTransformPoint(intersectionPoint);
            
            // Scale based on FOV and aspect ratio
            float halfHeight = offsetNodeNear * Mathf.Tan(offsetNodeFOV * 0.5f * Mathf.Deg2Rad);
            float halfWidth = halfHeight * offsetNodeAspect;
            
            // Calculate normalized device coordinates (NDC)
            float ndcX = localPoint.x / halfWidth;
            float ndcY = localPoint.y / halfHeight;
            
            // Convert NDC to screen coordinates
            float screenX = (ndcX + 1) * 0.5f * cameraRenderTex.width;
            float screenY = (1 - (ndcY + 1) * 0.5f) * cameraRenderTex.height; // Flip Y
            
            return new Vector2(screenX, screenY);
        }
        
        // Fallback if ray doesn't intersect plane
        Debug.LogWarning($"Ray from camera to point {worldPoint} doesn't intersect the image plane");
        return Vector2.zero;
    }

    /// <summary>
    /// Fallback method to use the main camera for cropping if no offset node is available
    /// </summary>
    private void PerformSurfaceCropWithMainCamera(Vector3 point1, Vector3 point2, Vector3 point3, Vector3 point4)
    {
        // Get the main camera
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("Cannot crop surface: Main camera not found");
            return;
        }

        // Convert 3D world points to 2D screen points
        Vector2 screenPoint1 = mainCamera.WorldToScreenPoint(point1);
        Vector2 screenPoint2 = mainCamera.WorldToScreenPoint(point2);
        Vector2 screenPoint3 = mainCamera.WorldToScreenPoint(point3);
        Vector2 screenPoint4 = mainCamera.WorldToScreenPoint(point4);

        // Log the screen coordinates
        Debug.Log($"Main camera screen coordinates: ({screenPoint1}), ({screenPoint2}), ({screenPoint3}), ({screenPoint4})");

        // Find the bounds of the quadrilateral
        float minX = Mathf.Min(screenPoint1.x, screenPoint2.x, screenPoint3.x, screenPoint4.x);
        float minY = Mathf.Min(screenPoint1.y, screenPoint2.y, screenPoint3.y, screenPoint4.y);
        float maxX = Mathf.Max(screenPoint1.x, screenPoint2.x, screenPoint3.x, screenPoint4.x);
        float maxY = Mathf.Max(screenPoint1.y, screenPoint2.y, screenPoint3.y, screenPoint4.y);

        // Calculate crop dimensions, ensuring they're within the screen bounds
        minX = Mathf.Max(0, minX);
        minY = Mathf.Max(0, minY);
        maxX = Mathf.Min(cameraRenderTex.width, maxX);
        maxY = Mathf.Min(cameraRenderTex.height, maxY);
        
        int cropWidth = Mathf.FloorToInt(maxX - minX);
        int cropHeight = Mathf.FloorToInt(maxY - minY);
        
        // Ensure non-zero dimensions
        cropWidth = Mathf.Max(1, cropWidth);
        cropHeight = Mathf.Max(1, cropHeight);
        
        Debug.Log($"Crop dimensions: {cropWidth}x{cropHeight}, bounds: ({minX},{minY}) to ({maxX},{maxY})");

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
            
            // Extract pixel data directly from the full screen texture to the cropped texture
            Color[] pixels = fullScreenTexture.GetPixels(
                Mathf.FloorToInt(minX), 
                Mathf.FloorToInt(minY), 
                cropWidth, 
                cropHeight);
                
            // Set pixels in the cropped texture
            croppedTexture.SetPixels(pixels);
            croppedTexture.Apply();
            
            // Store the cropped texture and clean up the full screen texture
            lastCroppedTexture = croppedTexture;
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
} 