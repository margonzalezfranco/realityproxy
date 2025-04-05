using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;
using TMPro;
using UnityEngine.XR.Interaction.Toolkit.UI;
using PolySpatial.Template;
using System;
using Newtonsoft.Json;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;

/// <summary>
/// SurfaceScanOCR: Listens for surface drawing events and triggers OCR scanning
/// This class connects the DragSurface system with the CloudVisionOCRUnified system
/// to perform OCR scanning when a user completes drawing a full surface (all four points).
/// </summary>
public class SurfaceScanOCR : GeminiGeneral
{
    [Header("Dependencies")]
    [Tooltip("Reference to the CloudVisionOCR component")]
    public CloudVisionOCRUnified ocrComponent;

    [Header("OCR Settings")]
    [Tooltip("Optional delay before triggering OCR (seconds)")]
    public float ocrDelay = 0f;

    [Tooltip("Whether to automatically clear the surface after OCR completes")]
    public bool clearOCRSurfaceAfterGetOCR = true;
    
    // Line data structures for OCR and Semantic results
    
    /// <summary>
    /// Represents a semantic line with text and bounding box from Gemini's processing
    /// </summary>
    [System.Serializable]
    public class SemanticLineData
    {
        public string text;
        public Rect boundingBox;
        
        public SemanticLineData(string text, Rect boundingBox)
        {
            this.text = text;
            this.boundingBox = boundingBox;
        }
        
        public override string ToString()
        {
            return $"{text} => Semantic BBox: {boundingBox}";
        }
    }
    
    // Reference to the OCR lines created
    private List<GameObject> createdOCRLines = new List<GameObject>();
    
    // Reference to the semantic lines created
    private List<GameObject> createdSemanticLines = new List<GameObject>();
    
    // Store the most recent OCR results for Gemini processing
    private string lastOcrFullText;
    private List<CloudVisionOCRUnified.LineData> lastOcrLines;
    
    [Header("OCR Line Rendering")]
    [Tooltip("Prefab to use for OCR text lines")]
    public GameObject ocrLinePrefab;
    
    [Header("Semantic Line Rendering")]
    [Tooltip("Prefab to use for semantic text lines")]
    public GameObject semanticLinePrefab;
    
    [Tooltip("Parent transform to hold all OCR text lines")]
    public Transform ocrLineContainer;
    
    [Tooltip("Parent transform to hold all semantic text lines")]
    public Transform semanticLineContainer;
    
    [Tooltip("Z-offset for text from the surface (in meters)")]
    public float textZOffset = 0.001f;
    
    [Tooltip("Scale factor for text size")]
    public float textScaleFactor = 0.1f;
    
    [Tooltip("If true, destroy previous OCR lines before creating new ones")]
    public bool clearPreviousOCRLines = true;
    
    [Tooltip("If true, destroy previous semantic lines before creating new ones")]
    public bool clearPreviousSemanticLines = true;
    
    // Reference to the most recently scanned surface
    private GameObject lastScannedSurface;
    
    // References to the corners of the last scanned surface
    private Vector3 surfacePoint1;
    private Vector3 surfacePoint2;
    private Vector3 surfacePoint3;
    private Vector3 surfacePoint4;
    
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

    [Header("Gemini Integration")]
    [Tooltip("Reference to GeminiDefaultPrompter component for processing OCR text")]
    public GeminiDefaultPrompter geminiPrompter;

    [Tooltip("Panel to display Gemini's response")]
    public GameObject responsePanel;
    
    [Tooltip("TextMeshPro component in the response panel")]
    private TextMeshPro responseText;
    
    [Tooltip("Custom prompt template for OCR text analysis")]
    [TextArea(3, 6)]
    public string ocrTextPromptTemplate = "This is text I scanned from my environment with OCR: \"{0}\". Please explain what this text means, what it's from, and why it might be important. Keep the response concise.";
    
    [Tooltip("Prompt template for semantic line extraction")]
    [TextArea(3, 10)]
    private string semanticLinePromptTemplate = @"
    You're analyzing a scanned image with OCR results **and** additional visual elements. 
    Your task is to identify FUNCTIONAL AREAS in the image—not just textual lines—and 
    return them in a JSON array, where each entry has:
    1) A concise label (string) that describes the overall function or meaning of that region 
    (for example, “Nutrition Facts Section” or “USB Ports and Cable Connections”).
    2) A bounding box (x, y, width, height) that fully covers all relevant text **and** any 
    associated non-text elements (icons, ports, switches, etc.) belonging to that region. 
    Image Dimensions and Coordinate System:
    - The image is {1} pixels wide and {2} pixels high
    - IMPORTANT: Use a coordinate system where (0,0) is at the BOTTOM-LEFT corner of the image
    - The TOP-RIGHT corner of the image is at ({1}, {2})
    - All coordinates must be within these bounds
    - Even if there are no text elements detected, please identify visually distinct regions

    ### CRITICAL - Bounding Box Rules:
    - Bounding boxes MUST NOT overlap with each other
    - One functional area MUST NOT completely contain another.
    - MUST NOT include the biggest bounding box in the response, which is {1} pixels wide and {2} pixels high. Only consider the contents inside the object.
    - If you identify overlapping functional areas, divide them into separate non-overlapping regions
    - Each region should be visually and functionally distinct
    - Prefer fewer, well-defined regions over many small fragmented ones
    - Keep adequate spacing between bounding boxes (at least 5-10 pixels)

    ### Instructions:

    1. **Group** any nearby OCR lines that serve the **same functional purpose** into one 
    bounding box, instead of returning many small lines. For instance, multiple lines 
    describing “Nutrition Facts” or “Settings Panel” should be merged into a single 
    labeled functional area. 
    
    2. **Include non-text elements** that have a clear function (e.g., ports, switches, 
    controls, icons) in the same bounding box if they belong together. Even if 
    something has no text, label its purpose or function (e.g., “LAN port,” 
    “Power button,” “USB icon area,” etc.). 

    3. **Return a bounding box** in the same coordinate system as the OCR lines, 
    but make it large enough to encompass the entire functional area 
    (text + any relevant graphics). 
    - The coordinate system has (0, 0) at the bottom-left, 
        with `width` and `height` in pixels. 
    - If you must approximate bounding boxes for non-text elements, do your best 
        to place them accurately around those areas.

    4. **Be concise** in your labeling. Provide short functional names rather than 
    large blocks of text. 

    5. **Only provide 3 - 8 functional areas.**

    6. **Output format**: A strict JSON array, like:
    ```
    [
    {
        ""text"": ""[FUNCTIONAL LABEL]"",
        ""boundingBox"": {""x"": 10, ""y"": 20, ""width"": 100, ""height"": 30}
    },
    ...
    ]
    ```
    Do not include any extra keys or commentary outside this array.

    ### Important:
    - Do **not** merely combine adjacent text into one sentence; 
    **combine** or **group** based on **function** or usage (e.g., “Nutrition Facts,” 
    “Router Ports,” “Safety Warnings,” etc.).
    - If the image depicts machinery, panels, or ports without text, you should 
    still produce an entry with a bounding box and a functional label.
    - If no OCR lines were detected, focus on identifying visual regions based on color, 
    shape, and apparent functionality.
    - Remember: NO OVERLAPPING OR NESTED BOUNDING BOXES - each area must be completely separate.

    Below are the raw OCR lines (with bounding boxes) you can reference. 
    However, your output **should** merge or supersede these smaller OCR bounding 
    boxes if they belong to one larger functional area:

    Original OCR lines:
    {0}
    ";

    // Add a method to update the prompt with the dimensions of the cropped texture
    private string FormatSemanticPromptWithDimensions(string basePrompt, int width, int height, string ocrLinesText)
    {
        string prompt = basePrompt.Replace("{0}", ocrLinesText)
                                  .Replace("{1}", width.ToString())
                                  .Replace("{2}", height.ToString());
        return prompt;
    }
    
    [Tooltip("Color for semantic line visualization")]
    public Color semanticLineColor = new Color(0.2f, 0.8f, 0.2f, 0.8f);
    
    [Tooltip("Vertical offset for the response panel above the OCR line")]
    public float responseVerticalOffset = 0.03f; // 3cm above the OCR line
    
    [Tooltip("Whether to automatically process semantic lines after OCR completes")]
    public bool autoProcessSemanticLines = true;
    
    // Reference to the currently active OCR line that triggered a response
    private GameObject activeResponseLine;

    [Header("UI Integration")]
    [Tooltip("The parent menu canvas containing the InfoPanel and other UI elements")]
    public Transform menuCanvas;

    [Tooltip("The transform that will hold the questions (typically the InfoPanel)")]
    public Transform questionsParent;

    [Tooltip("Prefab to use for question buttons")]
    public GameObject questionPrefab;

    [Tooltip("Answer panel to display responses to questions")]
    public GameObject answerPanel;

    [Tooltip("Reference to the GeminiQuestionAnswerer component")]
    public GeminiQuestionAnswerer questionAnswerer;

    // Add this field for tracking created questions
    private List<GameObject> createdQuestions = new List<GameObject>();

    [Header("User Study Logging")]
    [SerializeField] private bool enableUserStudyLogging = true;

    // Override the Awake method from GeminiGeneral
    protected override void Awake()
    {
        // Call the base class Awake to initialize Gemini client
        base.Awake();
        
        // Additional initialization specific to SurfaceScanOCR
    }

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

        // Subscribe to surface clearing event
        DragSurface.OnSurfaceCleared += HandleSurfaceCleared;

        // Find the TextMeshPro component in the response panel
        if (responsePanel != null)
        {
            responseText = responsePanel.GetComponentInChildren<TextMeshPro>();
            if (responseText == null)
            {
                Debug.LogWarning("No TextMeshPro component found in responsePanel!");
            }
            
            // Initially hide the response panel
            responsePanel.SetActive(false);
        }
        
        // Create the semantic line container if not already assigned
        if (semanticLineContainer == null)
        {
            GameObject container = new GameObject("SemanticLineContainer");
            semanticLineContainer = container.transform;
        }
        
        // If semantic line prefab is not assigned, use the OCR line prefab
        if (semanticLinePrefab == null && ocrLinePrefab != null)
        {
            semanticLinePrefab = ocrLinePrefab;
            Debug.Log("Using OCR line prefab for semantic lines.");
        }
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
        
        // Unsubscribe from surface clearing event
        DragSurface.OnSurfaceCleared -= HandleSurfaceCleared;
        
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
        float displayMinY = base.cameraRenderTex.height - maxY;
        float displayMaxY = base.cameraRenderTex.height - minY;
        
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
        
        // Log surface completion for user study
        Vector3 surfaceCenter = (point1 + point2 + point3 + point4) / 4f;
        Vector3 dimensions = new Vector3(
            Vector3.Distance(point1, point2),
            Vector3.Distance(point2, point3),
            Vector3.Distance(point3, point4)
        );
        LogUserStudy($"[SURFACE_SCAN] SURFACE_COMPLETED: Center=\"{surfaceCenter}\", Dimensions=\"{dimensions}\", Points=4");
        
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
            ClearSemanticLines();
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
        float minX = minU * base.cameraRenderTex.width;
        float minY = minV * base.cameraRenderTex.height;
        float maxX = maxU * base.cameraRenderTex.width;
        float maxY = maxV * base.cameraRenderTex.height;
        
        // Adjust Y coordinates - this is now unnecessary, as the UV coordinates are 
        // already in the correct orientation for GetPixels
        // (0,0) at bottom left matches Unity's internal texture coordinate system
        
        // Log original pixel coordinates before any transformation
        Debug.Log($"Original pixel coordinates: ({minX},{minY}) to ({maxX},{maxY})");
        Debug.Log($"These coordinates match UV system: (0,0) at bottom-left, (1,1) at top-right");
        
        // Calculate final crop dimensions, ensuring they're within the texture bounds
        minX = Mathf.Max(0, minX);
        minY = Mathf.Max(0, minY);
        maxX = Mathf.Min(base.cameraRenderTex.width, maxX);
        maxY = Mathf.Min(base.cameraRenderTex.height, maxY);
        
        int cropWidth = Mathf.FloorToInt(maxX - minX);
        int cropHeight = Mathf.FloorToInt(maxY - minY);
        
        // Log the crop operation for user study
        LogUserStudy($"[SURFACE_SCAN] SURFACE_CROP: Size=\"{cropWidth}x{cropHeight}\", Position=\"({Mathf.FloorToInt(minX)},{Mathf.FloorToInt(minY)})\"");
        
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
        Texture2D fullScreenTexture = new Texture2D(base.cameraRenderTex.width, base.cameraRenderTex.height, TextureFormat.RGBA32, false);
        
        try
        {
            // Set the camera render texture as active
            RenderTexture.active = base.cameraRenderTex;
            
            // Read the entire screen
            fullScreenTexture.ReadPixels(new Rect(0, 0, base.cameraRenderTex.width, base.cameraRenderTex.height), 0, 0);
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
        
        // Log OCR completion for user study
        LogUserStudy($"[SURFACE_SCAN] OCR_COMPLETE: LinesDetected={lines.Count}, TextLength={fullText?.Length ?? 0}");
        
        // Store the OCR results for potential Gemini processing
        lastOcrFullText = fullText;
        lastOcrLines = lines;
        
        // Check if we have a valid surface to place text on
        if (lastScannedSurface == null)
        {
            Debug.LogWarning("No valid surface found to place OCR text. Make sure the surface exists.");
            return;
        }
        
        // Only create OCR lines if we have actual text results
        if (lines != null && lines.Count > 0)
        {
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
            Quaternion surfaceRotation = Quaternion.LookRotation(-surfaceNormal, -surfaceUp);
            
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
                
                // Get the text component and background as direct children of the OCR line
                TextMeshPro textMesh = ocrLine.GetComponentInChildren<TextMeshPro>();
                Transform backgroundTransform = ocrLine.transform.Find("Background");
                
                if (textMesh != null && backgroundTransform != null)
                {
                    // Set the text content
                    textMesh.text = line.text;
                    
                    // Use a fixed font size instead of scaling based on dimensions
                    textMesh.fontSize = 4f * 0.01f; // Fixed font size - adjust this value as needed
                    
                    // Center the text
                    textMesh.alignment = TextAlignmentOptions.Center;
                    textMesh.transform.localPosition = surfaceNormal * 0.001f;
                    textMesh.transform.localRotation = Quaternion.identity;
                    
                    // Scale and position the background to match the bounding box
                    backgroundTransform.localScale = new Vector3(lineWidth, lineHeight, 0.001f);
                    backgroundTransform.localPosition = Vector3.zero;
                    backgroundTransform.localRotation = Quaternion.identity;
                    
                    // Get the SpatialUIButton component from the background
                    var button = backgroundTransform.gameObject.GetComponent<PolySpatial.Template.SpatialUIButton>();
                    if (button != null)
                    {
                        // Update the reference scale after manually setting it
                        button.UpdateReferenceScale();
                        
                        // Store the text for the closure
                        string ocrText = line.text;
                        
                        // Add a listener to the WasPressed event
                        button.WasPressed += (buttonText, renderer, index) => 
                        {
                            if (geminiPrompter != null)
                            {
                                // Store a reference to the OCR line that triggered the response
                                activeResponseLine = ocrLine;
                                
                                // Call RequestResponse with the custom template and the OCR text
                                // Use the callback to position the response panel and set its text
                                geminiPrompter.RequestResponseWithCallback(
                                    ocrTextPromptTemplate, 
                                    ocrText, 
                                    (response) => PositionResponsePanel(response, ocrLine, surfaceNormal)
                                );
                            }
                            else
                            {
                                Debug.LogWarning("No GeminiDefaultPrompter reference set.");
                            }
                        };
                    }
                    else
                    {
                        Debug.LogWarning("Background doesn't have a SpatialUIButton component.");
                    }
                    
                    Debug.Log($"Created OCR line '{line.text}' at position {linePosition}, " + 
                              $"with size {lineWidth} x {lineHeight}");
                }
                else
                {
                    Debug.LogWarning($"OCRLine prefab doesn't have expected components: TextMeshPro or 'Background'");
                }
                
                // Add to created lines list for cleanup
                createdOCRLines.Add(ocrLine);
            }
            
            // After creating OCR lines
            LogUserStudy($"[SURFACE_SCAN] OCR_LINES_CREATED: Count={createdOCRLines.Count}");
        }
        else
        {
            Debug.Log("OCR did not detect any text lines. Will proceed with semantic analysis to identify visual elements.");
            LogUserStudy("[SURFACE_SCAN] OCR_NO_TEXT_DETECTED");
        }
        
        // Modified approach: If we need to process semantic lines AND clear the surface,
        // we'll do it in sequence using a coroutine
        if (autoProcessSemanticLines && clearOCRSurfaceAfterGetOCR)
        {
            StartCoroutine(ProcessSemanticLinesThenClearSurface());
        }
        else
        {
            // Handle each option independently
            if (autoProcessSemanticLines)
            {
                ProcessSemanticLines();
            }
            
            if (clearOCRSurfaceAfterGetOCR)
            {
                ClearSurface();
            }
        }
    }
    
    /// <summary>
    /// Coroutine to process semantic lines and then clear the surface once complete
    /// </summary>
    private IEnumerator ProcessSemanticLinesThenClearSurface()
    {
        // First process semantic lines
        if (lastCroppedTexture != null)
        {
            // Initialize empty OCR lines list if none exist or it's empty
            if (lastOcrLines == null || lastOcrLines.Count == 0)
            {
                Debug.Log("No OCR lines detected in ProcessSemanticLinesThenClearSurface, but proceeding with semantic analysis to identify non-text elements");
                lastOcrLines = new List<CloudVisionOCRUnified.LineData>();
            }
            else
            {
                Debug.Log($"Processing semantic lines before clearing surface with {lastOcrLines.Count} OCR lines");
            }
            
            // Format OCR lines for the prompt
            string ocrLinesText = FormatOCRLinesForPrompt(lastOcrLines);
            
            // Create prompt for semantic lines with dimensions
            string prompt = FormatSemanticPromptWithDimensions(
                semanticLinePromptTemplate,
                lastCroppedTexture.width,
                lastCroppedTexture.height,
                ocrLinesText
            );
            
            // Convert image to base64
            string base64Image = ConvertTextureToBase64(lastCroppedTexture);
            
            // Make the request
            var request = MakeGeminiRequest(prompt, base64Image);
            
            // Wait for the request to complete
            float timeout = 30f;
            float elapsed = 0f;
            
            Debug.Log("Waiting for semantic line processing to complete before clearing surface...");
            
            while (!request.IsCompleted && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
            
            if (elapsed >= timeout)
            {
                Debug.LogError("Semantic line processing timed out");
                LogUserStudy("[SURFACE_SCAN] SEMANTIC_PROCESSING_TIMEOUT");
            }
            else if (request.Error != null)
            {
                Debug.LogError($"Semantic line processing failed: {request.Error}");
                LogUserStudy($"[SURFACE_SCAN] SEMANTIC_PROCESSING_ERROR: Error=\"{request.Error.Message}\"");
            }
            else
            {
                // Process the response
                string response = request.Result;
                if (!string.IsNullOrEmpty(response))
                {
                    string parsedResponse = ParseGeminiRawResponse(response);
                    List<SemanticLineData> semanticLines = ExtractSemanticLinesFromResponse(parsedResponse);
                    
                    if (semanticLines.Count > 0)
                    {
                        CreateSemanticLines(semanticLines);
                        Debug.Log($"Created {semanticLines.Count} semantic lines before clearing surface");
                        // LogUserStudy($"SEMANTIC_LINES_CREATED: Count={semanticLines.Count}");
                    }
                    else
                    {
                        Debug.Log("No semantic lines extracted from Gemini response");
                        LogUserStudy("[SURFACE_SCAN] SEMANTIC_NO_LINES_EXTRACTED");
                    }
                }
                else
                {
                    Debug.LogWarning("Empty response from Gemini for semantic line processing");
                    LogUserStudy("[SURFACE_SCAN] SEMANTIC_EMPTY_RESPONSE");
                }
            }
        }
        else
        {
            Debug.LogError("Cannot process semantic lines: No cropped texture available");
            LogUserStudy("[SURFACE_SCAN] SEMANTIC_NO_TEXTURE_AVAILABLE");
        }
        
        // Now clear the surface
        ClearOCRLines();
        ClearSurface();
    }
    
    /// <summary>
    /// Clear the current surface
    /// </summary>
    private void ClearSurface()
    {
        // Get the DragSurface component and clear the surface
        DragSurface dragSurface = FindAnyObjectByType<DragSurface>();
        if (dragSurface != null)
        {
            Debug.Log("Clearing surface after OCR/semantic processing");
            dragSurface.ClearCurrentSurface();
        }
        else
        {
            Debug.LogWarning("Could not find DragSurface component to clear the surface");
        }
    }
    
    /// <summary>
    /// Process OCR results with Gemini to extract semantic lines
    /// </summary>
    [ContextMenu("Process Semantic Lines")]
    public void ProcessSemanticLines()
    {
        Debug.Log("ProcessSemanticLines - Starting");
        
        if (lastCroppedTexture == null)
        {
            Debug.LogError("No cropped texture available for semantic line processing");
            LogUserStudy("[SURFACE_SCAN] SEMANTIC_PROCESSING_FAILED: Reason=\"No cropped texture available\"");
            return;
        }
        
        // MODIFIED: Still proceed with semantic processing even without OCR lines
        // This allows detection of non-text elements like ports, buttons, etc.
        if (lastOcrLines == null || lastOcrLines.Count == 0)
        {
            Debug.Log("No OCR lines detected, but proceeding with semantic analysis to identify non-text elements");
            lastOcrLines = new List<CloudVisionOCRUnified.LineData>(); // Initialize empty list
        }
        else
        {
            Debug.Log($"ProcessSemanticLines - OCR lines count: {lastOcrLines.Count}");
        }
        
        // Log semantic line processing initiation for user study
        LogUserStudy($"[SURFACE_SCAN] SEMANTIC_PROCESSING_STARTED: OCRLineCount={lastOcrLines.Count}, TextureSize=\"{lastCroppedTexture.width}x{lastCroppedTexture.height}\"");
        
        try {
            // Convert the OCR lines to a string format for the prompt
            string ocrLinesText = FormatOCRLinesForPrompt(lastOcrLines);
            
            Debug.Log("Formatted OCR lines (truncated if long): " + 
                     (ocrLinesText.Length > 200 ? ocrLinesText.Substring(0, 200) + "..." : ocrLinesText));
            
            // Use the new format method to include dimensions of the cropped texture
            string prompt = FormatSemanticPromptWithDimensions(
                semanticLinePromptTemplate, 
                lastCroppedTexture.width, 
                lastCroppedTexture.height, 
                ocrLinesText
            );
            
            Debug.Log("Successfully created prompt with image dimensions");
            
            Debug.Log("Sending image and OCR lines to Gemini for semantic processing...");
            
            // Convert the image to base64
            string base64Image = ConvertTextureToBase64(lastCroppedTexture);
            
            // Send request to Gemini with both image and OCR text
            StartCoroutine(RequestSemanticLines(prompt, base64Image));
        }
        catch (System.Exception ex) {
            Debug.LogError($"Exception in ProcessSemanticLines: {ex.GetType().Name} - {ex.Message}");
            Debug.LogError($"Stack trace: {ex.StackTrace}");
            LogUserStudy($"[SURFACE_SCAN] SEMANTIC_PROCESSING_EXCEPTION: Exception=\"{ex.GetType().Name}\", Message=\"{ex.Message}\"");
        }
    }
    
    /// <summary>
    /// Format OCR lines for the Gemini prompt
    /// </summary>
    private string FormatOCRLinesForPrompt(List<CloudVisionOCRUnified.LineData> lines)
    {
        try {
            // Handle empty or null lines
            if (lines == null || lines.Count == 0)
            {
                Debug.Log("FormatOCRLinesForPrompt - No OCR lines to format, returning empty array");
                return "[]";  // Return empty JSON array
            }
            
            Debug.Log("FormatOCRLinesForPrompt - Starting with " + lines.Count + " lines");
            
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[");
            
            for (int i = 0; i < lines.Count; i++)
            {
                CloudVisionOCRUnified.LineData line = lines[i];
                
                Debug.Log($"Processing line {i}: Text = '{line.text}', Bounding box = {line.boundingBox}");
                
                sb.Append("{\"text\":\"");
                sb.Append(line.text.Replace("\"", "\\\""));  // Escape quotes
                sb.Append("\",\"boundingBox\":{");
                sb.Append($"\"x\":{line.boundingBox.x},");
                sb.Append($"\"y\":{line.boundingBox.y},");
                sb.Append($"\"width\":{line.boundingBox.width},");
                sb.Append($"\"height\":{line.boundingBox.height}");
                sb.Append("}}");
                
                if (i < lines.Count - 1)
                {
                    sb.Append(",");
                }
            }
            
            sb.Append("]");
            
            Debug.Log("FormatOCRLinesForPrompt - Successfully completed");
            return sb.ToString();
        }
        catch (System.Exception ex) {
            Debug.LogError($"Exception in FormatOCRLinesForPrompt: {ex.GetType().Name} - {ex.Message}");
            return "[]"; // Return empty array as fallback
        }
    }
    
    /// <summary>
    /// Send a request to Gemini for semantic line processing
    /// </summary>
    private IEnumerator RequestSemanticLines(string prompt, string base64Image)
    {
        Debug.Log("RequestSemanticLines - Starting Gemini request");
        
        if (clearPreviousSemanticLines)
        {
            ClearSemanticLines();
        }
        
        // Make the API request outside try-catch
        Debug.Log("Making Gemini API request...");
        var request = MakeGeminiRequest(prompt, base64Image);
        
        // Wait for the request to complete (also outside try-catch)
        Debug.Log("Waiting for Gemini API request to complete...");
        float timeout = 30f; // 30 second timeout
        float elapsed = 0f;
        
        while (!request.IsCompleted && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }
        
        if (elapsed >= timeout)
        {
            Debug.LogError("Gemini request timed out after 30 seconds");
            LogUserStudy("[SURFACE_SCAN] SEMANTIC_GEMINI_TIMEOUT: Duration=30s");
            yield break;
        }
        
        Debug.Log($"Gemini request completed. IsCompleted: {request.IsCompleted}, HasError: {(request.Error != null)}");
        
        // Process results in try-catch after waiting is complete
        try {
            // Check for errors
            if (request.Error != null)
            {
                Debug.LogError($"Gemini request failed: {request.Error}");
                LogUserStudy($"[SURFACE_SCAN] SEMANTIC_GEMINI_ERROR: Error=\"{request.Error.Message}\"");
                yield break;
            }
            
            // Parse the response
            string response = request.Result;
            Debug.Log($"Received raw response from Gemini ({response.Length} chars): {(response.Length > 100 ? response.Substring(0, 100) + "..." : response)}");
            
            if (string.IsNullOrEmpty(response))
            {
                Debug.LogError("Received empty response from Gemini");
                LogUserStudy("[SURFACE_SCAN] SEMANTIC_GEMINI_EMPTY_RESPONSE");
                yield break;
            }
            
            string parsedResponse = ParseGeminiRawResponse(response);
            
            if (string.IsNullOrEmpty(parsedResponse))
            {
                Debug.LogError("Failed to parse Gemini response");
                LogUserStudy("[SURFACE_SCAN] SEMANTIC_GEMINI_PARSING_FAILED");
                yield break;
            }
            
            Debug.Log($"Parsed Gemini response: {parsedResponse}");
            
            // Extract semantic lines from the response
            List<SemanticLineData> semanticLines = ExtractSemanticLinesFromResponse(parsedResponse);
            
            Debug.Log($"Extracted {semanticLines.Count} semantic lines from Gemini response");
            LogUserStudy($"[SURFACE_SCAN] SEMANTIC_LINES_EXTRACTED: Count={semanticLines.Count}");
            
            // Process semantic lines
            if (semanticLines.Count > 0)
            {
                Debug.Log("Creating semantic lines in 3D space");
                CreateSemanticLines(semanticLines);
            }
            else
            {
                Debug.LogWarning("No semantic lines extracted from Gemini response");
                LogUserStudy("[SURFACE_SCAN] SEMANTIC_NO_LINES_EXTRACTED");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error processing semantic lines: {e.GetType().Name} - {e.Message}");
            Debug.LogError($"Stack trace: {e.StackTrace}");
            LogUserStudy($"[SURFACE_SCAN] SEMANTIC_PROCESSING_EXCEPTION: Exception=\"{e.GetType().Name}\", Message=\"{e.Message}\"");
        }
    }
    
    /// <summary>
    /// Extract semantic lines from the Gemini response
    /// </summary>
    private List<SemanticLineData> ExtractSemanticLinesFromResponse(string response)
    {
        List<SemanticLineData> semanticLines = new List<SemanticLineData>();
        
        try
        {
            Debug.Log("Extracting semantic lines from response...");
            // Try to parse the JSON from the response
            // First, find JSON array in the response if it's wrapped in text
            int startIndex = response.IndexOf('[');
            int endIndex = response.LastIndexOf(']');
            
            Debug.Log($"JSON markers: startIndex={startIndex}, endIndex={endIndex}");
            
            if (startIndex >= 0 && endIndex > startIndex)
            {
                // Extract just the JSON array part
                string jsonArray = response.Substring(startIndex, endIndex - startIndex + 1);
                Debug.Log($"Extracted JSON array: {jsonArray}");
                
                try {
                    // Parse as array
                    List<Dictionary<string, object>> lines = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(jsonArray);
                    Debug.Log($"Deserialized {lines.Count} lines from JSON");
                    
                    foreach (var line in lines)
                    {
                        // Get the text
                        string text = line.ContainsKey("text") ? line["text"].ToString() : "";
                        
                        // Get the bounding box
                        if (line.ContainsKey("boundingBox"))
                        {
                            var bboxDict = line["boundingBox"] as Newtonsoft.Json.Linq.JObject;
                            if (bboxDict != null)
                            {
                                float x = bboxDict.Value<float>("x");
                                float y = bboxDict.Value<float>("y");
                                float width = bboxDict.Value<float>("width");
                                float height = bboxDict.Value<float>("height");
                                
                                Rect boundingBox = new Rect(x, y, width, height);
                                semanticLines.Add(new SemanticLineData(text, boundingBox));
                                Debug.Log($"Added semantic line: '{text}' with bbox: {boundingBox}");
                            }
                            else
                            {
                                Debug.LogWarning($"Could not parse bounding box for line: {text}");
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"Line is missing boundingBox property: {text}");
                        }
                    }
                }
                catch (Newtonsoft.Json.JsonException jsonEx) {
                    Debug.LogError($"JSON parsing error: {jsonEx.Message}");
                    Debug.LogError($"Attempted to parse: {jsonArray}");
                }
            }
            else
            {
                Debug.LogError($"Could not find valid JSON array in Gemini response. Response: {response}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error extracting semantic lines: {e.GetType().Name} - {e.Message}");
        }
        
        return semanticLines;
    }
    
    /// <summary>
    /// Create semantic lines in 3D space from processed data
    /// </summary>
    private void CreateSemanticLines(List<SemanticLineData> semanticLines)
    {
        Debug.Log($"Creating {semanticLines.Count} semantic lines in 3D space");
        
        // Check if we have a valid surface
        if (lastScannedSurface == null)
        {
            Debug.LogError("No valid surface found to place semantic lines - lastScannedSurface is null");
            return;
        }
        
        // Check if we have a valid prefab
        if (semanticLinePrefab == null)
        {
            Debug.LogError("Semantic line prefab not assigned. Attempting to use OCR line prefab...");
            
            if (ocrLinePrefab != null) {
                Debug.Log("Using OCR line prefab as fallback for semantic lines");
                semanticLinePrefab = ocrLinePrefab;
            } else {
                Debug.LogError("Both semanticLinePrefab and ocrLinePrefab are null. Cannot create semantic lines.");
                return;
            }
        }
        
        // Create container if needed
        if (semanticLineContainer == null)
        {
            Debug.Log("Creating semantic line container since none was assigned");
            GameObject container = new GameObject("SemanticLineContainer");
            semanticLineContainer = container.transform;
        }
        
        // Calculate surface dimensions and orientation
        Vector3 surfaceWidth = surfacePoint2 - surfacePoint1;
        Vector3 surfaceHeight = surfacePoint3 - surfacePoint2;
        Vector3 surfaceNormal = Vector3.Cross(surfaceWidth, surfaceHeight).normalized;
        
        Debug.Log($"Surface dimensions for semantic lines: width={surfaceWidth.magnitude}, height={surfaceHeight.magnitude}, normal={surfaceNormal}");
        
        // Calculate surface dimensions
        float surfaceWidthMagnitude = surfaceWidth.magnitude;
        float surfaceHeightMagnitude = surfaceHeight.magnitude;
        
        // Create right and up vectors for the surface
        Vector3 surfaceRight = surfaceWidth.normalized;
        Vector3 surfaceUp = Vector3.Cross(surfaceNormal, surfaceRight).normalized;
        
        // Create a rotation for the surface plane
        Quaternion surfaceRotation = Quaternion.LookRotation(-surfaceNormal, -surfaceUp);
        
        // Process each semantic line
        int lineCount = 0;
        foreach (SemanticLineData line in semanticLines)
        {
            lineCount++;
            // Skip lines with no bounding box or empty text
            if (line.boundingBox.width <= 0 || line.boundingBox.height <= 0 || string.IsNullOrEmpty(line.text))
            {
                Debug.LogWarning($"Skipping semantic line {lineCount}: Invalid dimensions or empty text");
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
            
            // Add slight offset (more than OCR lines to prevent z-fighting)
            linePosition += surfaceNormal * (textZOffset * 2f);
            
            // Calculate scale based on bounding box and surface dimensions
            float lineWidth = line.boundingBox.width / croppedTextureWidth * surfaceWidthMagnitude;
            float lineHeight = line.boundingBox.height / croppedTextureHeight * surfaceHeightMagnitude;
            
            Debug.Log($"Creating semantic line {lineCount}: '{line.text}' at position {linePosition}, with size {lineWidth} x {lineHeight}");
            
            // Create the semantic line object
            GameObject semanticLine = Instantiate(semanticLinePrefab, linePosition, surfaceRotation, semanticLineContainer);
            semanticLine.name = $"SemanticLine_{line.text}";
            
            // Get the text component and background
            TextMeshPro textMesh = semanticLine.GetComponentInChildren<TextMeshPro>();
            Transform backgroundTransform = semanticLine.transform.Find("Background");
            
            if (textMesh != null && backgroundTransform != null)
            {
                // Set the text content
                textMesh.text = line.text;
                
                // Use a fixed font size
                textMesh.fontSize = 4f * 0.01f; // Slightly larger than OCR lines
                
                // Set a different color for semantic lines
                textMesh.color = Color.white; // Clear white text 
                
                // Center the text
                textMesh.alignment = TextAlignmentOptions.Center;
                textMesh.transform.localPosition = surfaceNormal * 0.001f;
                textMesh.transform.localRotation = Quaternion.identity;

                float textWidth = lineWidth * 1.5f;
                if (textWidth < 0.014f) textWidth = 0.014f;
                float textHeight = lineHeight;

                textMesh.rectTransform.sizeDelta = new Vector2(textWidth, textHeight);
                
                // Scale and position the background to match the bounding box
                backgroundTransform.localScale = new Vector3(lineWidth, lineHeight, 0.001f);
                backgroundTransform.localPosition = Vector3.zero;
                backgroundTransform.localRotation = Quaternion.identity;
                
                // Set a different color for the background
                Renderer backgroundRenderer = backgroundTransform.GetComponent<Renderer>();
                if (backgroundRenderer != null)
                {
                    backgroundRenderer.material.color = semanticLineColor;
                    Debug.Log($"Set semantic line background color to {semanticLineColor}");
                }
                else
                {
                    Debug.LogWarning("Background doesn't have a Renderer component");
                }

                // Get the SpatialUIButton component from the background
                var button = backgroundTransform.gameObject.GetComponent<PolySpatial.Template.SpatialUIButton>();
                if (button != null)
                {
                    // Update the reference scale after manually setting it
                    button.UpdateReferenceScale();
                    
                    // Store the text for the closure
                    string semanticText = line.text;
                    
                    // Add a listener to the WasPressed event (similar to OCR lines)
                    button.WasPressed += (buttonText, renderer, index) => 
                    {
                        if (geminiPrompter != null)
                        {
                            // Store a reference to the semantic line that triggered the response
                            activeResponseLine = semanticLine;
                            
                            // Call RequestResponse with the semantic text
                            geminiPrompter.RequestResponseWithCallback(
                                ocrTextPromptTemplate, 
                                semanticText, 
                                (response) => PositionResponsePanel(response, semanticLine, surfaceNormal)
                            );
                        }
                    };
                }
                else
                {
                    Debug.LogWarning("Background doesn't have a SpatialUIButton component");
                }
                
                Debug.Log($"Successfully created semantic line '{line.text}' at position {linePosition}, with size {lineWidth} x {lineHeight}");
            }
            else
            {
                Debug.LogError($"Semantic line prefab missing components: TextMeshPro={textMesh!=null}, Background={backgroundTransform!=null}");
            }
            
            // Add to created lines list for cleanup
            createdSemanticLines.Add(semanticLine);
        }
        
        // Log semantic line creation completion for user study
        LogUserStudy($"[SURFACE_SCAN] SEMANTIC_LINES_CREATED: Count={createdSemanticLines.Count}");
        
        // Generate questions about the semantic content
        if (semanticLines != null && semanticLines.Count > 0 && questionsParent != null)
        {
            GenerateQuestionsAfterSemanticLines(semanticLines);
        }
        
        Debug.Log($"Finished creating {createdSemanticLines.Count} semantic lines. Check if they are visible in the scene.");
    }
    
    /// <summary>
    /// Clears all semantic lines created previously
    /// </summary>
    public void ClearSemanticLines()
    {
        int count = createdSemanticLines.Count;
        foreach (GameObject semanticLine in createdSemanticLines)
        {
            if (semanticLine != null)
            {
                Destroy(semanticLine);
            }
        }
        
        createdSemanticLines.Clear();
        
        // Log semantic line clearing for user study
        if (count > 0)
        {
            LogUserStudy($"[SURFACE_SCAN] SEMANTIC_LINES_CLEARED: Count={count}");
        }
    }
    
    /// <summary>
    /// Positions the response panel above the OCR line and sets its text
    /// </summary>
    /// <param name="responseText">The text to display in the response panel</param>
    /// <param name="ocrLine">The OCR line that triggered the response</param>
    /// <param name="surfaceNormal">The normal of the surface for proper offset direction</param>
    private void PositionResponsePanel(string response, GameObject ocrLine, Vector3 surfaceNormal)
    {
        if (responsePanel == null || ocrLine == null)
        {
            Debug.LogWarning("Cannot position response panel: panel or OCR line is null");
            return;
        }
        
        // Set the response text
        if (responseText != null)
        {
            responseText.text = response;
        }
        
        // Set the parent of the response panel to the OCR line
        responsePanel.transform.SetParent(ocrLine.transform);
        
        // Position the panel above the OCR line with an offset
        responsePanel.transform.localPosition = new Vector3(0, responseVerticalOffset, 0);
        
        // Ensure the panel is facing the same direction as the OCR line
        responsePanel.transform.localRotation = Quaternion.identity;
        
        // Make the panel visible
        responsePanel.SetActive(true);
        
        // Log response panel display for user study
        string lineType = createdOCRLines.Contains(ocrLine) ? "OCR" : "Semantic";
        string lineText = ocrLine.name.Replace("OCRLine_", "").Replace("SemanticLine_", "");
        LogUserStudy($"[SURFACE_SCAN] RESPONSE_DISPLAYED: LineType=\"{lineType}\", Text=\"{lineText}\", ResponseLength={response?.Length ?? 0}");
    }
    
    /// <summary>
    /// Hides the response panel and resets its parent
    /// </summary>
    public void HideResponsePanel()
    {
        if (responsePanel != null)
        {
            responsePanel.SetActive(false);
            responsePanel.transform.SetParent(null);
            activeResponseLine = null;
        }
    }
    
    /// <summary>
    /// Clears all OCR lines created previously
    /// </summary>
    public void ClearOCRLines()
    {
        // First hide the response panel
        HideResponsePanel();
        
        int count = createdOCRLines.Count;
        foreach (GameObject ocrLine in createdOCRLines)
        {
            if (ocrLine != null)
            {
                Destroy(ocrLine);
            }
        }
        
        createdOCRLines.Clear();
        
        // // Log OCR line clearing for user study
        // if (count > 0)
        // {
        //     LogUserStudy($"OCR_LINES_CLEARED: Count={count}");
        // }
    }

    /// <summary>
    /// Generate questions about the scanned content after semantic lines are created
    /// </summary>
    private void GenerateQuestionsAfterSemanticLines(List<SemanticLineData> semanticLines)
    {
        if (semanticLines == null || semanticLines.Count == 0)
        {
            Debug.LogWarning("No semantic lines to generate questions for");
            return;
        }
        
        // Extract all semantic line texts to create context
        List<string> semanticTexts = new List<string>();
        foreach (var line in semanticLines)
        {
            semanticTexts.Add(line.text);
        }
        
        string semanticContext = string.Join(", ", semanticTexts);
        Debug.Log($"Generating questions about semantic context: {semanticContext}");
        
        // Show the menu canvas if it's not already visible
        if (menuCanvas != null)
        {
            menuCanvas.gameObject.SetActive(true);
            
            // Position it appropriately
            PositionMenuCanvas();
        }
        
        // Start the question generation routine
        StartCoroutine(GenerateQuestionsRoutine(semanticContext));
    }

    /// <summary>
    /// Positions the menu canvas appropriately in the scene
    /// </summary>
    private void PositionMenuCanvas()
    {
        if (menuCanvas == null) return;
        
        // Make sure it's not parented to anything
        menuCanvas.SetParent(null);
        
        // Set up LazyFollow behavior to follow the camera
        LazyFollow lazyFollow = menuCanvas.GetComponent<LazyFollow>();
        if (lazyFollow != null)
        {
            lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;
        }

        questionsParent.gameObject.SetActive(true);
        
        // Activate the first three children if they exist
        if (menuCanvas.childCount >= 3)
        {
            for (int i = 0; i < 3; i++)
            {
                if (menuCanvas.GetChild(i) != null)
                    menuCanvas.GetChild(i).gameObject.SetActive(true);
            }
        }
    }

    /// <summary>
    /// Coroutine that generates questions about the semantic content using Gemini
    /// </summary>
    private IEnumerator GenerateQuestionsRoutine(string semanticContext)
    {
        Debug.Log("Starting question generation for semantic content...");
        
        // Log question generation initiation for user study
        LogUserStudy($"[SURFACE_SCAN] QUESTIONS_GENERATION_AFTER_SEMANTIC_LINES_STARTED: Context=\"{(semanticContext.Length > 50 ? semanticContext.Substring(0, 50) + "..." : semanticContext)}\"");
        
        // Clear any existing questions first
        ClearPreviousQuestions();
        
        // Capture a frame of the current view if we have a camera render texture
        string base64Image = null;
        if (lastCroppedTexture != null)
        {
            base64Image = ConvertTextureToBase64(lastCroppedTexture);
        }
        
        // Build a prompt for Gemini to generate questions about the semantic content
        string prompt = $@"
            From the image you can see the user is scanning a surface on an object.
            
            Based on this object and this scan of an object with the following semantic elements: {semanticContext},
            
            Please return a JSON list of possible user questions about these elements.
            Focus on questions that are relevant to:
            
            1. Understanding the purpose and functionality of these elements
            2. How these elements relate to each other
            3. What the user might want to know about using or interacting with them
            4. Common issues or considerations related to these elements
            
            Return only the most likely questions, up to 5 maximum.
            Focus on questions that users would genuinely want answers to.
            
            In the format:
            json
            [
            ""Question 1"",
            ""Question 2"",
            ...
            ]
        ";
        
        // Call Gemini
        Debug.Log("Sending question generation request to Gemini...");
        var request = MakeGeminiRequest(prompt, base64Image);
        
        // Wait for completion
        while (!request.IsCompleted)
            yield return null;
        
        string geminiResponse = request.Result;
        Debug.Log($"Received response from Gemini: {geminiResponse}");
        
        // Extract JSON
        string extractedJson = TryExtractJson(geminiResponse);
        Debug.Log($"Extracted JSON: {extractedJson}");
        
        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("Could not find valid JSON block in Gemini question response.");
            yield break;
        }
        
        // Parse the JSON into a list of questions
        List<string> questionsList = null;
        try
        {
            questionsList = JsonConvert.DeserializeObject<List<string>>(extractedJson);
            Debug.Log($"Successfully parsed {questionsList.Count} questions from JSON");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse question array: {e}");
            yield break;
        }
        
        // Create UI elements for each question
        if (questionsList != null && questionsList.Count > 0 && questionsParent != null)
        {
            float currentY = -60f;  // Start at the top
            float questionHeight = 54f;  // Height of each question block, adjust as needed
            float spacing = 0f;  // Space between questions
            
            foreach (var q in questionsList)
            {
                // Skip empty questions
                if (string.IsNullOrWhiteSpace(q)) continue;
                
                // Instantiate the question prefab
                GameObject go = Instantiate(questionPrefab, questionsParent);
                go.name = "GeminiQuestion";
                
                // Position it correctly using the transform
                Transform t = go.transform;
                if (t != null)
                {
                    t.localPosition = new Vector3(0f, -currentY, 0f);
                    currentY += questionHeight + spacing;
                }
                
                // Set the text
                TextMeshPro txt = go.GetComponentInChildren<TextMeshPro>();
                if (txt != null) txt.text = q;
                
                // Add button functionality
                var button = go.GetComponent<SpatialUIButton>();
                if (button != null)
                {
                    string questionText = q;  // Capture for closure
                    button.WasPressed += (buttonText, renderer, index) =>
                    {
                        // Clear previous answer and set "Generating..."
                        if (answerPanel != null && answerPanel.GetComponentInChildren<TextMeshPro>() != null)
                        {
                            answerPanel.GetComponentInChildren<TextMeshPro>().text = "Generating...";
                        }
                        
                        // Request answer if we have a question answerer
                        if (questionAnswerer != null)
                        {
                            questionAnswerer.RequestAnswer(questionText);
                            if (answerPanel != null) answerPanel.SetActive(true);
                        }
                        else
                        {
                            Debug.LogWarning("No GeminiQuestionAnswerer component assigned!");
                        }
                    };
                }
                else
                {
                    Debug.LogWarning("Question prefab is missing SpatialUIButton component.");
                }
                
                // Store the created question object for cleanup later
                createdQuestions.Add(go);
            }
            
            Debug.Log($"Created {createdQuestions.Count} question UI elements");
        }
        
        // Log questions generated for user study after successful parsing
        if (questionsList != null && questionsList.Count > 0)
        {
            LogUserStudy($"[SURFACE_SCAN] QUESTIONS_GENERATED: Count={questionsList.Count}, Questions=\"{string.Join(" | ", questionsList)}\"");
        }
        else
        {
            LogUserStudy("[SURFACE_SCAN] QUESTIONS_GENERATION_FAILED");
        }
    }

    /// <summary>
    /// Clears any previously created question UI elements
    /// </summary>
    private void ClearPreviousQuestions()
    {
        foreach (GameObject question in createdQuestions)
        {
            if (question != null)
            {
                Destroy(question);
            }
        }
        
        createdQuestions.Clear();
    }

    /// <summary>
    /// Helper method to extract JSON from Gemini's response which might be wrapped in a code block
    /// </summary>
    private string TryExtractJson(string fullResponse)
    {
        try
        {
            var root = JsonConvert.DeserializeObject<GeminiRoot>(fullResponse);
            if (root?.candidates == null || root.candidates.Count == 0)
                return null;

            string rawText = root.candidates[0].content.parts[0].text;
            if (string.IsNullOrEmpty(rawText)) 
                return null;

            if (rawText.Contains("```json"))
            {
                var splitted = rawText.Split(new[] { "```json" }, StringSplitOptions.None);
                if (splitted.Length > 1)
                {
                    var splitted2 = splitted[1].Split(new[] { "```" }, StringSplitOptions.None);
                    rawText = splitted2[0].Trim();
                }
            }
            return rawText;
        }
        catch
        {
            // fallback: raw entire text as-is
            return fullResponse;
        }
    }

    /// <summary>
    /// Classes to deserialize the Gemini API response structure
    /// </summary>
    [Serializable]
    public class GeminiRoot
    {
        public List<Candidate> candidates;
    }

    [Serializable]
    public class Candidate
    {
        public Content content;
    }

    [Serializable]
    public class Content
    {
        public List<Part> parts;
    }

    [Serializable]
    public class Part
    {
        public string text;
    }

    /// <summary>
    /// Handles the event when a surface is cleared via double-pinch
    /// </summary>
    private void HandleSurfaceCleared()
    {
        Debug.Log("Surface cleared via double-pinch - Cleaning up semantic lines and UI elements");
        
        // Log surface clearing for user study
        LogUserStudy("[SURFACE_SCAN] SURFACE_CLEARED: Method=\"user_initiated\"");
        
        // Clear semantic lines
        ClearSemanticLines();
        
        // Clear OCR lines
        ClearOCRLines();
        
        // Hide the questions panel
        if (questionsParent != null)
        {
            questionsParent.gameObject.SetActive(false);
        }
        
        // Hide any preview panel
        HidePreview();
        
        // Hide response panel
        HideResponsePanel();
        
        // Clear any created questions
        ClearPreviousQuestions();
    }
    
    // Helper method for creating timestamped user study logs
    private void LogUserStudy(string message)
    {
        if (!enableUserStudyLogging) return;
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
    }
} 