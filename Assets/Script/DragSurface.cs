using UnityEngine;
using System.Collections;

/// <summary>
/// DragSurface: Creates interactive surfaces using hand pinch gestures.
/// 
/// Surface Creation Process:
/// 1. First Pinch (Drawing Length):
///    - PinchStart: Point1 is set at the pinch position, and a thin surface is spawned.
///    - During Pinch: The surface extends from Point1 to the current pinch position.
///    - PinchEnd: Point2 is finalized, completing the length of the surface.
///
/// 2. Between Pinches (Drawing Height):
///    - The surface length (Point1 to Point2) is fixed in space.
///    - Point3 is continuously updated based on the hand position.
///    - The height is always perpendicular to the length.
///
/// 3. Second Pinch:
///    - PinchEnd: Point3 is finalized, completing the surface.
///
/// The surface is a thin cuboid with adjustable thickness (default 0.005m).
/// The 4 points form a rectangular shape that defines the surface.
/// </summary>
public class DragSurface : MonoBehaviour
{
    // Delegate and event for surface length drawing completion
    public delegate void SurfaceLengthCompletedHandler(Vector3 startPoint, Vector3 endPoint);
    public static event SurfaceLengthCompletedHandler OnSurfaceLengthCompleted;

    // Delegate and event for complete surface creation
    public delegate void SurfaceCompletedHandler(Vector3 point1, Vector3 point2, Vector3 point3, Vector3 point4);
    public static event SurfaceCompletedHandler OnSurfaceCompleted;

    [Header("Dependencies")]
    [Tooltip("Reference to the hand tracking component")]
    public MyHandTracking handTracking;
    
    [Header("Surface Settings")]
    [Tooltip("Material to apply to the created surface")]
    public Material surfaceMaterial;
    
    [Tooltip("Thickness of the created surface in meters")]
    public float surfaceThickness = 0.005f;
    
    [Tooltip("Layer to place created surfaces on")]
    public string targetLayer = "Default";
    
    [Tooltip("Minimum distance required in meters for the first drag to be considered valid")]
    public float minimumDragDistance = 0.04f;
    
    [Header("Hand Settings")]
    [Tooltip("Whether to allow the left hand to draw surfaces")]
    public bool allowLeftHand = true;
    
    [Header("Debug")]
    [Tooltip("Whether to show debug visualizers for the control points")]
    public bool showDebugPoints = true;
    
    [Tooltip("Custom material to use for debug points (optional)")]
    public Material debugPointMaterial;
    
    // State tracking
    private enum SurfaceCreationState
    {
        None,           // No surface is being created
        DrawingLength,  // First pinch - creating length
        DrawingHeight,  // Between pinches - creating height
        Completed,      // After second pinch - surface is complete
        Cleared,        // Previous surface has been cleared, ready for new creation
        AwaitingSecondPinch  // Waiting for second pinch in a double-pinch sequence
    }
    
    private SurfaceCreationState currentState = SurfaceCreationState.None;
    
    // Which hand is currently being used for drawing
    private bool isLeftHandDrawing = false;
    
    // Points defining the surface
    private Vector3 point1; // First corner - start of pinch
    private Vector3 point2; // Second corner - end of first pinch
    private Vector3 point3; // Third corner - end of second pinch
    private Vector3 point4; // Fourth corner - calculated from other points
    
    // The current surface being created
    private GameObject currentSurface;
    
    // Debug visualizers
    private GameObject point1Visualizer;
    private GameObject point2Visualizer;
    private GameObject point3Visualizer;
    private GameObject point4Visualizer;

    // Variables for double-pinch detection
    private float lastPinchTime = 0f;
    private bool isAwaitingSecondPinch = false;
    private float doublePinchTimeThreshold = 1.0f; // Time window for double-pinch detection (in seconds)
    private bool lastPinchWasLeft = false; // Track which hand performed the last pinch
    
    // Additional variables for recovery mechanism
    private float longTimeoutThreshold = 5.0f; // Time window for giving up completely and resetting
    private bool hasLoggedTimeoutWarning = false;

    void Start()
    {
        // Auto-find the hand tracking component if not assigned
        if (handTracking == null)
        {
            handTracking = FindAnyObjectByType<MyHandTracking>();
            if (handTracking == null)
            {
                Debug.LogError("No MyHandTracking component found in the scene!");
                enabled = false;
                return;
            }
        }
        
        // Subscribe to pinch events
        MyHandTracking.OnPinchStarted += HandlePinchStarted;
        MyHandTracking.OnPinchEnded += HandlePinchEnded;
        
        // Initialize debug visualizers if needed
        if (showDebugPoints)
        {
            InitializeDebugVisualizers();
        }
    }
    
    void OnDestroy()
    {
        // Unsubscribe from events
        MyHandTracking.OnPinchStarted -= HandlePinchStarted;
        MyHandTracking.OnPinchEnded -= HandlePinchEnded;
    }
    
    void Update()
    {
        // Only update point3 during DrawingHeight phase
        if (currentState == SurfaceCreationState.DrawingHeight)
        {
            // Skip if using left hand and it's not allowed
            if (isLeftHandDrawing && !allowLeftHand)
                return;
                
            UpdateHeightDrawing();
        }
        // Add continuous update for point2 during DrawingLength phase
        else if (currentState == SurfaceCreationState.DrawingLength)
        {
            // Skip if using left hand and it's not allowed
            if (isLeftHandDrawing && !allowLeftHand)
                return;
                
            UpdateLengthDrawing();
        }
        
        // Check for double-pinch timeout
        if (isAwaitingSecondPinch)
        {
            float timeSinceLastPinch = Time.time - lastPinchTime;
            
            // First warning at normal timeout
            if (timeSinceLastPinch >= doublePinchTimeThreshold && !hasLoggedTimeoutWarning)
            {
                Debug.Log($"Double-pinch window expired. Please use your {(lastPinchWasLeft ? "left" : "right")} hand to pinch again to retry, or wait for auto-reset.");
                hasLoggedTimeoutWarning = true;
                
                // Immediately reset to None state if we were in AwaitingSecondPinch
                // This prevents the bug where the system can proceed to height drawing after a failed double-pinch
                if (currentState == SurfaceCreationState.AwaitingSecondPinch)
                {
                    currentState = SurfaceCreationState.None;
                    isAwaitingSecondPinch = false;
                    Debug.Log("Reset to None state after double-pinch timeout.");
                }
            }
            
            // Complete reset after long timeout (for any remaining cases)
            if (timeSinceLastPinch >= longTimeoutThreshold)
            {
                isAwaitingSecondPinch = false;
                hasLoggedTimeoutWarning = false;
                
                // If we were waiting to start drawing, revert to None state
                if (currentState == SurfaceCreationState.AwaitingSecondPinch)
                {
                    currentState = SurfaceCreationState.None;
                    Debug.Log("Auto-reset complete. Ready for new interaction.");
                }
            }
        }
    }
    
    private void HandlePinchStarted(bool isLeft)
    {
        // Skip left hand pinches if not allowed
        if (isLeft && !allowLeftHand)
            return;
            
        float currentTime = Time.time;
        
        // Handle double-pinch detection - now more permissive
        if (isAwaitingSecondPinch && (currentTime - lastPinchTime) < longTimeoutThreshold)
        {
            // This is a pinch during the waiting period - accept it even if from different hand
            isAwaitingSecondPinch = false;
            hasLoggedTimeoutWarning = false;
            
            switch (currentState)
            {
                case SurfaceCreationState.AwaitingSecondPinch:
                    // Double-pinch to start drawing a new surface
                    StartLengthDrawing(isLeft);
                    break;
                    
                case SurfaceCreationState.Completed:
                    // Double-pinch to clear the previous surface
                    ClearCurrentSurface();
                    currentState = SurfaceCreationState.None;
                    Debug.Log("Previous surface cleared. Double-pinch to start drawing a new surface.");
                    break;
            }
            
            return;
        }
        
        // Handle the first pinch of a potential double-pinch
        switch (currentState)
        {
            case SurfaceCreationState.None:
                // Set up for potential double-pinch to start drawing
                isAwaitingSecondPinch = true;
                hasLoggedTimeoutWarning = false;
                lastPinchTime = currentTime;
                lastPinchWasLeft = isLeft;
                currentState = SurfaceCreationState.AwaitingSecondPinch;
                Debug.Log($"First pinch detected with {(isLeft ? "left" : "right")} hand. Pinch again within 1 second to start drawing.");
                break;
                
            case SurfaceCreationState.DrawingHeight:
                // Second pinch during height drawing, don't need to do anything here
                break;
                
            case SurfaceCreationState.Completed:
                // Set up for potential double-pinch to clear surface
                isAwaitingSecondPinch = true;
                hasLoggedTimeoutWarning = false;
                lastPinchTime = currentTime;
                lastPinchWasLeft = isLeft;
                Debug.Log($"First pinch detected with {(isLeft ? "left" : "right")} hand. Pinch again within 1 second to clear the surface.");
                break;
                
            default:
                // Reset double-pinch detection for other states
                isAwaitingSecondPinch = false;
                hasLoggedTimeoutWarning = false;
                break;
        }
    }
    
    private void HandlePinchEnded(bool isLeft)
    {
        // Skip left hand pinches if not allowed
        if (isLeft && !allowLeftHand)
            return;
            
        // Only process pinch end for the drawing hand when in drawing states
        // Critically, check for valid drawing states to prevent proceeding from a failed initial sequence
        if ((isLeft != isLeftHandDrawing && currentState != SurfaceCreationState.None) ||
            (currentState != SurfaceCreationState.DrawingLength && 
             currentState != SurfaceCreationState.DrawingHeight))
            return;
            
        switch (currentState)
        {
            case SurfaceCreationState.DrawingLength:
                // End the length drawing and start height drawing
                FinishLengthDrawing();
                break;
                
            case SurfaceCreationState.DrawingHeight:
                // End the height drawing and complete the surface
                FinishHeightDrawing();
                break;
                
            default:
                // Ignore pinch end in other states
                break;
        }
    }
    
    private void StartLengthDrawing(bool isLeft)
    {
        // First ensure any previous surface is cleared
        ClearAllSurfaces();
        
        // Prevent left hand drawing if not allowed
        if (isLeft && !allowLeftHand)
            return;
        
        // Set the initial point
        if (handTracking.TryGetPinchPosition(isLeft, out Vector3 pinchPosition))
        {
            isLeftHandDrawing = isLeft;
            currentState = SurfaceCreationState.DrawingLength;
            
            // Set point1 at the pinch position
            point1 = pinchPosition;
            
            // Initialize point2 to the same position (it will be updated during dragging)
            point2 = pinchPosition;
            
            // Create the surface (initially hidden)
            CreateSurface();
            
            // Initially hide the surface until drag threshold is reached
            if (currentSurface != null)
            {
                currentSurface.SetActive(false);
            }
            
            Debug.Log($"Started drawing surface at position: {point1}");
            
            // Update debug visualizers - they will be hidden initially since drag distance is 0
            UpdateDebugVisualizers();
        }
    }
    
    private void FinishLengthDrawing()
    {
        if (handTracking.TryGetPinchPosition(isLeftHandDrawing, out Vector3 pinchPosition))
        {
            // Calculate the drag distance
            float dragDistance = Vector3.Distance(point1, pinchPosition);
            
            // Check if the drag distance is too small
            if (dragDistance < minimumDragDistance)
            {
                Debug.Log($"Drag distance too small ({dragDistance:F3}m). Minimum required: {minimumDragDistance:F3}m. Canceling this drawing attempt.");
                
                // Reset to None state
                ClearCurrentSurface();
                currentState = SurfaceCreationState.None;
                isAwaitingSecondPinch = false;
                hasLoggedTimeoutWarning = false;
                
                return;
            }
            
            // Set point2 at the pinch end position
            point2 = pinchPosition;
            
            // Initialize point3 to be above point2 (it will be updated during height drawing)
            // This creates a default small height perpendicular to length
            point3 = CalculatePerpendicularPoint(point1, point2, surfaceThickness * 2);
            
            // Make sure the surface is visible
            if (currentSurface != null)
            {
                currentSurface.SetActive(true);
            }
            
            // Update the surface to reflect the final length
            UpdateSurface();
            
            // Change to height drawing state
            currentState = SurfaceCreationState.DrawingHeight;
            
            Debug.Log($"Finished drawing length. Point2: {point2}, Drag distance: {dragDistance:F3}m");
            
            // Trigger the event for surface length completion
            OnSurfaceLengthCompleted?.Invoke(point1, point2);
            
            // Now that we're proceeding to height drawing, update debug visualizers
            UpdateDebugVisualizers();
        }
    }
    
    private void UpdateHeightDrawing()
    {
        // Use the current position of the hand to update the height
        if (handTracking.TryGetHandPosition(isLeftHandDrawing, out Vector3 handPosition))
        {
            // Project the hand position onto the plane perpendicular to the length
            point3 = ProjectOntoPerpendicularPlane(point1, point2, handPosition);
            
            // Update the surface
            UpdateSurface();
            
            // Update debug visualizers
            UpdateDebugVisualizers();
        }
    }
    
    private void FinishHeightDrawing()
    {
        if (handTracking.TryGetPinchPosition(isLeftHandDrawing, out Vector3 pinchPosition))
        {
            // Set final point3 at the pinch end position, projected onto perpendicular plane
            point3 = ProjectOntoPerpendicularPlane(point1, point2, pinchPosition);
            
            // Update the surface one last time
            UpdateSurface();
            
            // Change to completed state
            currentState = SurfaceCreationState.Completed;
            
            Debug.Log($"Surface completed. Point3: {point3}");
            
            // Trigger the event for surface completion
            OnSurfaceCompleted?.Invoke(point1, point2, point3, point4);
            
            // Update debug visualizers
            UpdateDebugVisualizers();
            
            // Allow creating a new surface
            StartCoroutine(ResetAfterDelay(1.0f));
        }
    }
    
    private void CreateSurface()
    {
        // Create a new cube for the surface
        currentSurface = GameObject.CreatePrimitive(PrimitiveType.Cube);
        currentSurface.name = "DragSurface";
        
        // Set the layer
        if (!string.IsNullOrEmpty(targetLayer))
        {
            currentSurface.layer = LayerMask.NameToLayer(targetLayer);
        }
        
        // Apply material if provided
        if (surfaceMaterial != null)
        {
            Renderer renderer = currentSurface.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = surfaceMaterial;
            }
        }
        
        // Initialize with minimum size
        currentSurface.transform.position = point1;
        currentSurface.transform.localScale = new Vector3(surfaceThickness, surfaceThickness, surfaceThickness);
    }
    
    private void UpdateSurface()
    {
        if (currentSurface == null)
            return;
            
        // Calculate point4 based on the other three points
        point4 = point3 + (point1 - point2);
        
        switch (currentState)
        {
            case SurfaceCreationState.DrawingLength:
                // During length drawing, the surface is just a line with minimal height/width
                UpdateSurfaceForLengthDrawing();
                break;
                
            case SurfaceCreationState.DrawingHeight:
            case SurfaceCreationState.Completed:
                // During height drawing and when completed, create proper rectangular surface
                UpdateSurfaceForHeightDrawing();
                break;
        }
    }
    
    private void UpdateSurfaceForLengthDrawing()
    {
        // Calculate midpoint and direction
        Vector3 midPoint = (point1 + point2) / 2f;
        Vector3 direction = (point2 - point1).normalized;
        float length = Vector3.Distance(point1, point2);
        
        // Set position to midpoint
        currentSurface.transform.position = midPoint;
        
        // Orient the cube along the line
        if (length > 0.001f) // Avoid very small values that could cause issues
        {
            currentSurface.transform.rotation = Quaternion.LookRotation(direction);
            
            // Scale along the z-axis (forward) for length, keep minimal height/width
            currentSurface.transform.localScale = new Vector3(surfaceThickness, surfaceThickness, length);
        }
        else
        {
            // If points are too close, just show a small cube
            currentSurface.transform.localScale = new Vector3(surfaceThickness, surfaceThickness, surfaceThickness);
        }
    }
    
    private void UpdateSurfaceForHeightDrawing()
    {
        // Calculate dimensions of the surface
        Vector3 lengthVector = point2 - point1;
        Vector3 heightVector = point3 - point2;
        
        float length = lengthVector.magnitude;
        float height = heightVector.magnitude;
        
        // Calculate the center point of the rectangle
        Vector3 centerPoint = (point1 + point2 + point3 + point4) / 4f;
        
        // Set position to center
        currentSurface.transform.position = centerPoint;
        
        if (length > 0.001f && height > 0.001f) // Avoid very small values
        {
            // Calculate normal of the surface
            Vector3 normal = Vector3.Cross(lengthVector, heightVector).normalized;
            
            // Calculate the right vector and up vector for rotation
            Vector3 rightVector = lengthVector.normalized;
            Vector3 upVector = Vector3.Cross(normal, rightVector).normalized;
            
            // Create rotation matrix based on these vectors
            Quaternion rotation = Quaternion.LookRotation(normal, upVector);
            currentSurface.transform.rotation = rotation;
            
            // Set scale based on length, height, and thickness
            currentSurface.transform.localScale = new Vector3(length, height, surfaceThickness);
        }
    }
    
    private Vector3 CalculatePerpendicularPoint(Vector3 start, Vector3 end, float distance)
    {
        // Calculate a point perpendicular to the line segment
        Vector3 direction = (end - start).normalized;
        
        // Try to use up vector for perpendicular calculation if possible
        Vector3 perpendicular;
        if (Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.9f)
        {
            // If direction is too close to up, use right instead
            perpendicular = Vector3.Cross(direction, Vector3.right).normalized;
        }
        else
        {
            perpendicular = Vector3.Cross(direction, Vector3.up).normalized;
        }
        
        // Return a point at the specified distance in the perpendicular direction from end
        return end + perpendicular * distance;
    }
    
    private Vector3 ProjectOntoPerpendicularPlane(Vector3 start, Vector3 end, Vector3 point)
    {
        Vector3 lineDirection = (end - start).normalized;
        
        // Calculate normal of the perpendicular plane
        Vector3 planeNormal = lineDirection;
        
        // Project point onto this plane using end as a point on the plane
        Vector3 v = point - end;
        float distance = Vector3.Dot(v, planeNormal);
        Vector3 projectedPoint = point - distance * planeNormal;
        
        return projectedPoint;
    }
    
    public void ClearCurrentSurface()
    {
        // Cancel any pending reset
        StopAllCoroutines();
        
        // Destroy the current surface if it exists
        if (currentSurface != null)
        {
            Destroy(currentSurface);
            currentSurface = null;
        }
        
        // Hide debug visualizers
        SetDebugVisualizersActive(false);
    }
    
    private IEnumerator ResetAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        // Reset state to allow creating a new surface, but don't clear the surface yet
        // This will happen on the next pinch
        currentState = SurfaceCreationState.Completed;
    }
    
    private void InitializeDebugVisualizers()
    {
        // Create spheres for each point
        point1Visualizer = CreateDebugSphere(Color.red);
        point2Visualizer = CreateDebugSphere(Color.green);
        point3Visualizer = CreateDebugSphere(Color.blue);
        point4Visualizer = CreateDebugSphere(Color.yellow);
        
        // Hide them initially
        SetDebugVisualizersActive(false);
    }
    
    private GameObject CreateDebugSphere(Color color)
    {
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "DebugPoint";
        sphere.transform.localScale = Vector3.one * 0.01f; // 1cm
        
        // Set the layer
        if (!string.IsNullOrEmpty(targetLayer))
        {
            sphere.layer = LayerMask.NameToLayer(targetLayer);
        }
        
        // Apply material and color
        Renderer renderer = sphere.GetComponent<Renderer>();
        if (renderer != null)
        {
            if (debugPointMaterial != null)
            {
                // Use the custom material without changing its color
                renderer.material = new Material(debugPointMaterial);
                // No color tinting applied when using custom material
            }
            else
            {
                // Fall back to default behavior with color tinting
                renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                renderer.material.color = color;
            }
        }
        
        return sphere;
    }
    
    private void UpdateDebugVisualizers()
    {
        if (!showDebugPoints)
            return;
            
        // Show the active points based on the current state
        switch (currentState)
        {
            case SurfaceCreationState.None:
            case SurfaceCreationState.AwaitingSecondPinch:
                SetDebugVisualizersActive(false);
                break;
                
            case SurfaceCreationState.DrawingLength:
                // Check if we should show debug points based on drag distance
                float dragDistance = Vector3.Distance(point1, point2);
                bool exceedsThreshold = dragDistance >= minimumDragDistance;
                
                // Only show points if drag distance is sufficient
                point1Visualizer.SetActive(exceedsThreshold);
                point2Visualizer.SetActive(exceedsThreshold);
                
                // Always hide points 3 and 4 during length drawing
                point3Visualizer.SetActive(false);
                point4Visualizer.SetActive(false);
                
                // If showing, update positions
                if (exceedsThreshold)
                {
                    point1Visualizer.transform.position = point1;
                    point2Visualizer.transform.position = point2;
                }
                break;
                
            case SurfaceCreationState.DrawingHeight:
            case SurfaceCreationState.Completed:
                // Always show all points in these states
                point1Visualizer.SetActive(true);
                point1Visualizer.transform.position = point1;
                
                point2Visualizer.SetActive(true);
                point2Visualizer.transform.position = point2;
                
                point3Visualizer.SetActive(true);
                point3Visualizer.transform.position = point3;
                
                point4Visualizer.SetActive(true);
                point4Visualizer.transform.position = point4;
                break;
        }
    }
    
    private void SetDebugVisualizersActive(bool active)
    {
        if (point1Visualizer != null) point1Visualizer.SetActive(active);
        if (point2Visualizer != null) point2Visualizer.SetActive(active);
        if (point3Visualizer != null) point3Visualizer.SetActive(active);
        if (point4Visualizer != null) point4Visualizer.SetActive(active);
    }
    
    private void UpdateLengthDrawing()
    {
        // Continuously update point2 to the current pinch position
        if (handTracking.TryGetPinchPosition(isLeftHandDrawing, out Vector3 pinchPosition))
        {
            // Update point2 to the current pinch position
            point2 = pinchPosition;
            
            // Calculate drag distance for threshold checking
            float dragDistance = Vector3.Distance(point1, pinchPosition);
            
            // Only show the surface if drag distance exceeds minimum
            if (currentSurface != null)
            {
                bool shouldShowSurface = dragDistance >= minimumDragDistance;
                currentSurface.SetActive(shouldShowSurface);
                
                // Only update the surface if it's visible
                if (shouldShowSurface)
                {
                    // Update the surface to show the current length
                    UpdateSurface();
                }
            }
            
            // Update debug visualizers
            UpdateDebugVisualizers();
        }
    }

    [ContextMenu("Clear All Surfaces")]
    public void ClearAllSurfaces()
    {
        // Cancel any pending reset
        StopAllCoroutines();
        
        // Clear the current surface
        ClearCurrentSurface();
        
        // Find and clear any other surfaces with the same name that might have been missed
        GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>();
        int count = 0;
        foreach (GameObject obj in allObjects)
        {
            if (obj.name == "DragSurface")
            {
                Destroy(obj);
                count++;
            }
        }
        
        if (count > 0)
        {
            Debug.LogWarning($"Found {count} leftover surfaces that were not properly cleared.");
        }
        
        // Reset state
        currentState = SurfaceCreationState.None;
        isAwaitingSecondPinch = false;
        hasLoggedTimeoutWarning = false;
    }
} 