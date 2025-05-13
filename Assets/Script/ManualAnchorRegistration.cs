using UnityEngine;
using System.Collections;
using UnityEngine.XR.Hands; // If needed for specific hand joint access
using System; // Added for Exception handling
using Newtonsoft.Json; // Added for JSON parsing if needed for single label

// Inherit from GeminiGeneral instead of MonoBehaviour
public class ManualAnchorRegistration : GeminiGeneral
{
    [Header("Dependencies")]
    [Tooltip("Reference to the MyHandTracking script for pinch detection")]
    public MyHandTracking handTracking;

    [Tooltip("Reference to the SceneObjectManager to register anchors")]
    public SceneObjectManager sceneObjectManager;

    [Tooltip("Reference to the XR Camera for raycasting origin/direction")]
    public Camera xrCamera; 
    
    [Header("Settings")]
    [Tooltip("Minimum duration (in seconds) to hold a pinch to trigger registration")]
    public float longPinchDuration = 1.0f;
    
    [Tooltip("Maximum distance for the raycast from the hand")]
    public float maxRayDistance = 5.0f;

    [Header("Transition Settings")]
    [Tooltip("Initial alpha value (transparency) of the anchor sphere")]
    [Range(0.1f, 1.0f)] public float initialAlpha = 0.3f;
    
    [Tooltip("Initial color of the anchor sphere")]
    public Color initialColor = new Color(0f, 0f, 0f); // Black (000000)
    
    [Tooltip("How long the transparency/color transition should take")]
    public float transitionDuration = 1.0f;

    // TODO: Add prefab for the temporary visual indicator
    // public GameObject temporaryVisualPrefab;

    // Internal state variables
    private float leftPinchStartTime = -1f;
    private float rightPinchStartTime = -1f;
    private bool isTransitioningLeft = false;
    private bool isTransitioningRight = false;
    private float leftTransitionStartTime = -1f;
    private float rightTransitionStartTime = -1f;
    private GameObject leftAnchorObj = null;
    private GameObject rightAnchorObj = null;
    private Material leftAnchorMaterial = null;
    private Material rightAnchorMaterial = null;
    private bool isRegisteringLeft = false;
    private bool isRegisteringRight = false;
    private Coroutine leftRegistrationCoroutine = null;
    private Coroutine rightRegistrationCoroutine = null;
    private string placeholderLabel = "Identifying...";

    // Override Awake if needed, but remember to call base.Awake()
    protected override void Awake()
    {
        base.Awake(); // Important: Call the base class Awake to initialize Gemini client
        // Add any additional initialization specific to ManualAnchorRegistration here
    }

    void Start()
    {
        // Validation for dependencies (GeminiGeneral and cameraRenderTex are handled by base class now)
        if (handTracking == null) Debug.LogError("MyHandTracking reference not set in ManualAnchorRegistration.");
        if (sceneObjectManager == null) Debug.LogError("SceneObjectManager reference not set in ManualAnchorRegistration.");
        if (xrCamera == null) Debug.LogError("XR Camera reference not set in ManualAnchorRegistration.");
        // Check inherited fields after base Awake has run
        if (geminiClient == null) Debug.LogError("Gemini client failed to initialize in base class.");
        if (cameraRenderTex == null) Debug.LogError("Camera RenderTexture reference not set in the inherited GeminiGeneral component."); 
    }

    void Update()
    {
        // Handle pinch detection for both hands
        HandleLongPinch(true);
        HandleLongPinch(false);
        
        // Update transitions for both hands
        if (isTransitioningLeft && leftAnchorObj != null)
        {
            UpdateAnchorTransition(isLeft: true);
        }
        if (isTransitioningRight && rightAnchorObj != null)
        {
            UpdateAnchorTransition(isLeft: false);
        }
    }

    private void HandleLongPinch(bool isLeft)
    {
        // Skip if any essential component is missing
        if (handTracking == null || sceneObjectManager == null || xrCamera == null || cameraRenderTex == null)
            return;
        if (handTracking.handSubsystem == null || !handTracking.handSubsystem.running)
            return;

        bool isCurrentlyPinching = handTracking.IsPinching(isLeft);
        ref float startTime = ref (isLeft ? ref leftPinchStartTime : ref rightPinchStartTime);
        ref bool isCurrentlyRegistering = ref (isLeft ? ref isRegisteringLeft : ref isRegisteringRight);
        ref bool isTransitioning = ref (isLeft ? ref isTransitioningLeft : ref isTransitioningRight);
        ref float transitionStartTime = ref (isLeft ? ref leftTransitionStartTime : ref rightTransitionStartTime);
        
        if (isCurrentlyPinching)
        {
            if (startTime < 0) // Pinch just started
            {
                startTime = Time.time;
            }
            else if (!isCurrentlyRegistering && !isTransitioning) // Not already in transition/registration
            {
                if (Time.time - startTime >= longPinchDuration)
                {
                    // Long pinch confirmed - start transition phase
                    Debug.Log($"{(isLeft ? "Left" : "Right")} hand long pinch detected. Creating semi-transparent anchor.");
                    
                    // Get current pinch position
                    Vector3 pinchPosition;
                    if (handTracking.TryGetPinchPosition(isLeft, out pinchPosition))
                    {
                        // Create the semi-transparent anchor
                        CreateTransitionAnchor(isLeft, pinchPosition);
                        
                        // Start transition
                        isTransitioning = true;
                        transitionStartTime = Time.time;
                    }
                    else
                    {
                        Debug.LogWarning($"Could not get pinch position for {(isLeft ? "Left" : "Right")} hand.");
                    }
                }
            }
        }
        else // Not pinching
        {
            if (isTransitioning) // Was transitioning and now released
            {
                Debug.Log($"{(isLeft ? "Left" : "Right")} hand pinch released. Finalizing anchor.");
                
                // Get the anchor object and position before we reset
                GameObject anchorObj = isLeft ? leftAnchorObj : rightAnchorObj;
                Vector3 finalPosition = anchorObj != null ? anchorObj.transform.position : Vector3.zero;
                
                // Ensure anchor is fully opaque at release
                Material material = isLeft ? leftAnchorMaterial : rightAnchorMaterial;
                if (material != null)
                {
                    // Set final color/alpha
                    Color finalColor = material.color;
                    finalColor.a = 1.0f;
                    material.color = finalColor;
                }
                
                // Stop transition phase
                isTransitioning = false;
                transitionStartTime = -1f;
                
                // Start Gemini processing
                if (finalPosition != Vector3.zero)
                {
                    if (isLeft) leftRegistrationCoroutine = StartCoroutine(FinalizeAnchor(isLeft, finalPosition, anchorObj));
                    else rightRegistrationCoroutine = StartCoroutine(FinalizeAnchor(isLeft, finalPosition, anchorObj));
                    isCurrentlyRegistering = true;
                }
                
                // Don't clear anchor objects yet - they'll be updated with final label by FinalizeAnchor
            }
            
            // Reset pinch timer if not transitioning or registering
            if (!isTransitioning && !isCurrentlyRegistering)
            {
                startTime = -1f;
            }
        }
    }
    
    private void CreateTransitionAnchor(bool isLeft, Vector3 position)
    {
        // Check if we already have an anchor for this hand (shouldn't happen, but just in case)
        if ((isLeft && leftAnchorObj != null) || (!isLeft && rightAnchorObj != null))
        {
            Debug.LogWarning($"Anchor for {(isLeft ? "Left" : "Right")} hand already exists. Destroying old one.");
            if (isLeft)
            {
                Destroy(leftAnchorObj);
                leftAnchorObj = null;
                leftAnchorMaterial = null;
            }
            else
            {
                Destroy(rightAnchorObj);
                rightAnchorObj = null;
                rightAnchorMaterial = null;
            }
        }
        
        // Create a new anchor object at the pinch position with placeholder label
        GameObject anchorObj = sceneObjectManager.SpawnSphereWithLabel(position, placeholderLabel);
        
        // Get the anchor's renderer to modify the material
        Renderer renderer = anchorObj.GetComponentInChildren<Renderer>();
        if (renderer != null)
        {
            // Create a new material instance based on the current material to avoid affecting other objects
            Material newMaterial = new Material(renderer.material);
            
            // Set initial color and transparency
            Color color = initialColor;
            color.a = initialAlpha;
            newMaterial.color = color;
            
            // Enable transparency on the material
            newMaterial.SetFloat("_Mode", 3); // Transparent mode in Standard shader
            newMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            newMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            newMaterial.SetInt("_ZWrite", 0);
            newMaterial.DisableKeyword("_ALPHATEST_ON");
            newMaterial.EnableKeyword("_ALPHABLEND_ON");
            newMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            newMaterial.renderQueue = 3000; // Transparent render queue
            
            // Apply the new material to the renderer
            renderer.material = newMaterial;
            
            // Store reference to material
            if (isLeft) leftAnchorMaterial = newMaterial;
            else rightAnchorMaterial = newMaterial;
        }
        
        // Store reference to object
        if (isLeft) leftAnchorObj = anchorObj;
        else rightAnchorObj = anchorObj;
    }
    
    private void UpdateAnchorTransition(bool isLeft)
    {
        GameObject anchorObj = isLeft ? leftAnchorObj : rightAnchorObj;
        Material material = isLeft ? leftAnchorMaterial : rightAnchorMaterial;
        float transitionStartTime = isLeft ? leftTransitionStartTime : rightTransitionStartTime;
        
        if (anchorObj == null || material == null)
        {
            Debug.LogWarning($"Cannot update transition for {(isLeft ? "Left" : "Right")} hand - missing anchor or material.");
            return;
        }
        
        // Update anchor position to follow pinch
        Vector3 pinchPosition;
        if (handTracking.TryGetPinchPosition(isLeft, out pinchPosition))
        {
            anchorObj.transform.position = pinchPosition;
        }
        
        // Calculate transition progress (0 to 1)
        float elapsedTime = Time.time - transitionStartTime;
        float progress = Mathf.Clamp01(elapsedTime / transitionDuration);
        
        // Update material color and alpha
        Color currentColor = material.color;
        Color targetColor = Color.white; // or whatever final color you want
        targetColor.a = 1.0f; // fully opaque
        
        Color newColor = Color.Lerp(initialColor, targetColor, progress);
        newColor.a = Mathf.Lerp(initialAlpha, 1.0f, progress);
        material.color = newColor;
    }
    
    private IEnumerator FinalizeAnchor(bool isLeft, Vector3 position, GameObject existingAnchorObj)
    {
        string handSide = isLeft ? "Left" : "Right";
        Debug.Log($"Starting finalization for {handSide} hand anchor at position {position}.");
        
        // 1. Capture Image
        Debug.Log("Capturing camera view for label identification...");
        Texture2D capturedImage = CaptureFrame(cameraRenderTex);
        if (capturedImage == null)
        {
            Debug.LogError("Failed to capture frame from RenderTexture.");
            CleanupRegistration(isLeft, existingAnchorObj);
            yield break;
        }
        
        // 2. Convert Image to Base64
        string base64Image = ConvertTextureToBase64(capturedImage);
        if (string.IsNullOrEmpty(base64Image))
        {
            Debug.LogError("Failed to convert captured image to Base64.");
            Destroy(capturedImage);
            CleanupRegistration(isLeft, existingAnchorObj);
            yield break;
        }
        
        // 3. Call Gemini
        string prompt = $"Identify the object at this pinch point location ({position}). Provide only the object's name.";
        Debug.Log("Sending image and prompt to Gemini...");
        var request = MakeGeminiRequest(prompt, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        
        string response = request.Result;
        string label = null;
        
        if (!string.IsNullOrEmpty(response))
        {
            label = ParseGeminiRawResponse(response);
            if (label != null) label = label.Trim();
        }
        
        // 4. Update or register anchor
        if (!string.IsNullOrEmpty(label))
        {
            Debug.Log($"Gemini returned label: '{label}' for manual anchor.");
            
            // We have two approaches to handle the anchor:
            // Option 1: Update the existing transition anchor with the new label
            if (existingAnchorObj != null)
            {
                // Update the label text
                TMPro.TextMeshPro tmpLabel = existingAnchorObj.GetComponentInChildren<TMPro.TextMeshPro>();
                if (tmpLabel != null)
                {
                    tmpLabel.text = label;
                    Debug.Log($"Updated existing anchor object with label: {label}");
                    
                    // Find this object in the anchors list and update its label
                    SceneObjectAnchor anchor = sceneObjectManager.GetAnchorByGameObject(existingAnchorObj);
                    if (anchor != null)
                    {
                        anchor.label = label;
                    }
                    else
                    {
                        Debug.LogWarning("Could not find anchor in SceneObjectManager's list - may not be properly registered.");
                    }
                }
                else
                {
                    Debug.LogWarning("Could not find TextMeshPro component on anchor to update label.");
                }
            }
            // Option 2: (fallback) Register a new anchor and destroy the transition one
            else
            {
                Debug.Log("Creating new anchor via SceneObjectManager.RegisterOrUpdateAnchor");
                sceneObjectManager.RegisterOrUpdateAnchor(label, position);
            }
        }
        else
        {
            Debug.LogWarning("Gemini did not return a valid label. Using 'Unknown Object' as fallback.");
            
            // Use a fallback label
            if (existingAnchorObj != null)
            {
                // Update the existing object with fallback label
                TMPro.TextMeshPro tmpLabel = existingAnchorObj.GetComponentInChildren<TMPro.TextMeshPro>();
                if (tmpLabel != null)
                {
                    tmpLabel.text = "Unknown Object";
                }
                
                // Find this object in the anchors list and update its label
                SceneObjectAnchor anchor = sceneObjectManager.GetAnchorByGameObject(existingAnchorObj);
                if (anchor != null)
                {
                    anchor.label = "Unknown Object";
                }
            }
            else
            {
                // Fallback to creating a new anchor
                sceneObjectManager.RegisterOrUpdateAnchor("Unknown Object", position);
            }
        }
        
        // 5. Cleanup
        if (capturedImage != null) Destroy(capturedImage);
        
        // Reset registration state (keep the anchor object as it's now fully integrated)
        if (isLeft)
        {
            isRegisteringLeft = false;
            leftAnchorObj = null;
            leftAnchorMaterial = null;
            leftRegistrationCoroutine = null;
        }
        else
        {
            isRegisteringRight = false;
            rightAnchorObj = null;
            rightAnchorMaterial = null;
            rightRegistrationCoroutine = null;
        }
        
        Debug.Log($"Finished anchor finalization for {handSide} hand.");
    }
    
    private void CleanupRegistration(bool isLeft, GameObject existingAnchorObj)
    {
        // Destroy the existing anchor object if registration failed
        if (existingAnchorObj != null)
        {
            Destroy(existingAnchorObj);
        }
        
        // Reset state
        if (isLeft)
        {
            isRegisteringLeft = false;
            leftAnchorObj = null;
            leftAnchorMaterial = null;
            leftRegistrationCoroutine = null;
        }
        else
        {
            isRegisteringRight = false;
            rightAnchorObj = null;
            rightAnchorMaterial = null;
            rightRegistrationCoroutine = null;
        }
    }
} 