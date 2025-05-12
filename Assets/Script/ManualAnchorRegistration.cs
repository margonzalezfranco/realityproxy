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

    // TODO: Add prefab for the temporary visual indicator
    // public GameObject temporaryVisualPrefab;

    // Internal state variables
    private float leftPinchStartTime = -1f;
    private float rightPinchStartTime = -1f;
    private bool isRegisteringLeft = false;
    private bool isRegisteringRight = false;
    private Coroutine leftRegistrationCoroutine = null; // Track coroutines
    private Coroutine rightRegistrationCoroutine = null;

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
        // --- Left Hand Long Pinch Detection ---
        HandleLongPinch(true);

        // --- Right Hand Long Pinch Detection ---
        HandleLongPinch(false);
    }

    private void HandleLongPinch(bool isLeft)
    {
        // Ensure dependencies are set before proceeding (removed geminiInteraction check)
        if (handTracking == null || sceneObjectManager == null || xrCamera == null || cameraRenderTex == null)
            return;

        // Access handSubsystem from MyHandTracking directly if public
        // If handSubsystem is not public in MyHandTracking, you'll need to make it public or add a getter.
        // Assuming handTracking.handSubsystem is accessible:
        if (handTracking.handSubsystem == null || !handTracking.handSubsystem.running) 
            return; // Exit if hand tracking isn't running

        bool isCurrentlyPinching = handTracking.IsPinching(isLeft);
        ref float startTime = ref (isLeft ? ref leftPinchStartTime : ref rightPinchStartTime);
        ref bool isCurrentlyRegistering = ref (isLeft ? ref isRegisteringLeft : ref isRegisteringRight);
        ref Coroutine registrationCoroutine = ref (isLeft ? ref leftRegistrationCoroutine : ref rightRegistrationCoroutine);

        if (isCurrentlyPinching)
        {
            if (startTime < 0) 
            {
                startTime = Time.time;
            }
            else if (!isCurrentlyRegistering) 
            {
                if (Time.time - startTime >= longPinchDuration)
                {
                    Debug.Log($"{(isLeft ? "Left" : "Right")} hand long pinch detected. Starting registration.");
                    if(registrationCoroutine != null) StopCoroutine(registrationCoroutine); 
                    registrationCoroutine = StartCoroutine(RegisterAnchorProcess(isLeft)); 
                    isCurrentlyRegistering = true;
                    startTime = -1f; 
                }
            }
        }
        else 
        {
            if (startTime >= 0) 
            {
                 startTime = -1f;
                 // Optional: Cancellation logic if pinch released during registration
            }
            startTime = -1f;
        }
    }

    private IEnumerator RegisterAnchorProcess(bool isLeft)
    {
        string handSide = isLeft ? "Left" : "Right";
        Debug.Log($"Starting RegisterAnchorProcess for {handSide} hand.");

        // 1. Determine Pointing Direction & Raycast
        Vector3 rayOrigin;
        Vector3 pointDirection;
        bool gotPose = false;

        // Assuming handTracking.handSubsystem is accessible
        XRHand hand = isLeft ? handTracking.handSubsystem.leftHand : handTracking.handSubsystem.rightHand;
        if (hand.isTracked && hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexTipPose) && hand.GetJoint(XRHandJointID.IndexIntermediate).TryGetPose(out Pose indexKnucklePose))
        {
            rayOrigin = indexTipPose.position;
            pointDirection = (indexTipPose.position - indexKnucklePose.position).normalized;
            gotPose = true;
             Debug.Log($"Using {handSide} index finger direction for raycast. Origin: {rayOrigin}, Direction: {pointDirection}");
        }
        else
        {
            Debug.LogWarning($"Could not get {handSide} index finger pose. Falling back to camera center raycast.");
            rayOrigin = xrCamera.transform.position;
            pointDirection = xrCamera.transform.forward;
        }

        RaycastHit hit;
        GameObject tempVisual = null;
        Texture2D capturedImage = null;

        if (Physics.Raycast(rayOrigin, pointDirection, out hit, maxRayDistance))
        {
            Debug.Log($"Raycast hit object: {hit.collider.name} at point: {hit.point}");

            // 2. Spawn Temporary Visual (Placeholder)

            // 3. Capture Image - Call inherited method directly
            Debug.Log("Capturing camera view...");
            capturedImage = CaptureFrame(cameraRenderTex); 
            if (capturedImage == null)
            {
                Debug.LogError("Failed to capture frame from RenderTexture.");
                yield break; 
            }
            
            // 4. Convert Image to Base64 - Call inherited method directly
            string base64Image = ConvertTextureToBase64(capturedImage);
            if (string.IsNullOrEmpty(base64Image))
            {
                 Debug.LogError("Failed to convert captured image to Base64.");
                 Destroy(capturedImage); 
                 yield break;
            }

            // 5. Call Gemini - Call inherited method directly
            string prompt = "Describe the main object visible in the center of the image. Provide only the object's name.";
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
                // Parse the response - Call inherited method directly
                 label = ParseGeminiRawResponse(response); 
                 if (label != null) label = label.Trim(); 
            }
            else
            {
                 Debug.LogWarning("Gemini request failed or returned empty response.");
            }

            // 6. Register Anchor
            if (!string.IsNullOrEmpty(label))
            {
                Debug.Log($"Gemini returned label: '{label}'. Registering anchor at {hit.point}.");
                sceneObjectManager.RegisterOrUpdateAnchor(label, hit.point);
            }
            else
            {
                 Debug.LogWarning("Gemini did not return a valid label. Anchor not registered.");
            }

            // 7. Cleanup 
            if (tempVisual != null) Destroy(tempVisual);
            if (capturedImage != null) Destroy(capturedImage); 
        }
        else
        {
            Debug.Log("Manual registration raycast did not hit any object within range.");
        }

        // Reset registration flag - Use direct assignment instead of ref locals
        if (isLeft)
        {
            isRegisteringLeft = false;
            leftRegistrationCoroutine = null;
        }
        else
        {
            isRegisteringRight = false;
            rightRegistrationCoroutine = null;
        }
        Debug.Log($"Finished RegisterAnchorProcess for {handSide} hand.");
    }
} 