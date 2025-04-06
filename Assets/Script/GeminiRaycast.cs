using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Unity.Mathematics; // for float3x3, float4x4

/// <summary>
/// Casts rays from bounding box centers and calls SceneObjectManager to register/update anchors,
/// rather than spawning spheres/labels directly here.
/// </summary>
public class GeminiRaycast : MonoBehaviour
{
    [Header("Camera for Raycasting (Left Eye)")]
    [Tooltip("Assign the XR camera whose Tracked Pose Driver is set to LeftEye. " +
             "We'll use its position as the base, but final offset is from offsetNode.")]
    public Camera xrCamera;

    [Header("SceneObjectManager")]
    public SceneObjectManager sceneObjectManager;

    [Header("Manual Offset Node")]
    [Tooltip("An empty child object under XR Camera for final offset/rotation.")]
    public Transform offsetNode;

    [Header("Raycast Settings")]
    public float maxRayDistance = 100f;

    [Header("Ray Visualization (Optional)")]
    public bool visualizeRays = false;
    public Material lineMaterial;
    public float rayWidth = 0.002f;
    public bool showHitSegment = true;

    [Header("Offset Node Camera Visualization (Optional)")]
    [Tooltip("If true, we'll draw a pseudo 'camera frustum' for the offsetNode after each OnBoxesUpdated.")]
    public bool visualizeOffsetNodeCamera = true;
    [Tooltip("Pseudo FOV for offsetNode camera visualization.")]
    public float offsetNodeFOV = 72.5f;
    [Tooltip("Aspect ratio for offsetNode camera visualization.")]
    public float offsetNodeAspect = 16f / 9f;
    [Tooltip("Near plane for offsetNode camera visualization.")]
    public float offsetNodeNear = 0.1f;
    [Tooltip("Far plane for offsetNode camera visualization.")]
    public float offsetNodeFar = 0.4f;

    // Add a new list specifically for debug visualization objects
    private List<GameObject> debugVisualizationObjects = new List<GameObject>();
    
    public List<GameObject> spawnedObjects = new List<GameObject>();
    
    // Hard-coded camera resolution in your pipeline
    private int imageWidth = 1920;
    private int imageHeight = 1080;

    // Example intrinsics and extrinsics
    private float3x3 intrinsicsMatrix = new float3x3(
        736.6339f,  0.0f,        960.0f,
        0.0f,       736.6339f,   540.0f,
        0.0f,       0.0f,        1.0f
    );

    private float4x4 extrinsicsMatrix = new float4x4(
        0.99122864f,  -0.0038917887f, -0.13210104f,  0.024276158f,
        0.006838121f, -0.99671704f,    0.08067425f, -0.02069169f,
       -0.13198134f,  -0.08086995f,   -0.9879479f,  -0.057551354f,
        0.0f,          0.0f,           0.0f,         1.0f
    );

    /// <summary>
    /// Processes bounding boxes using the camera pose at the time of capture.
    /// This ensures raycasts occur from the position when the user pressed the button,
    /// not from where they are when the API response arrives.
    /// </summary>
    public void OnBoxesUpdatedWithCameraPose(List<Box2DResult> boxes, Gemini2DBoundingBoxDetector.CameraPoseData cameraPose)
    {
        // Clear only debug visualization objects, not the persistent spheres
        ClearDebugObjects();

        if (boxes == null || boxes.Count == 0)
        {
            Debug.Log("No bounding boxes to process.");
            return;
        }

        Debug.Log("Processing boxes with camera pose from capture time: " + cameraPose.position);

        // Iterate over each bounding box
        foreach (var box in boxes)
        {
            // box_2d = [ymin, xmin, ymax, xmax]
            float ymin = box.box_2d[0];
            float xmin = box.box_2d[1];
            float ymax = box.box_2d[2];
            float xmax = box.box_2d[3];

            // 1) Convert [0..1000]-based coords -> pixel coords
            float centerX = (xmin + xmax) * 0.5f * (imageWidth / 1000f);
            float centerY = (ymin + ymax) * 0.5f * (imageHeight / 1000f);

            // 2) Unproject to camera local space
            Vector3 directionLocal = UnprojectPixel(
                centerX, centerY,
                imageWidth, imageHeight,
                flipX: false,
                flipY: true,   // Because top-left might map differently in your pipeline
                useExtrinsics: false, 
                intrinsicsMatrix,
                extrinsicsMatrix
            ).normalized; // normalize it

            // 3) Build a ray using the CAPTURED camera pose, not the current one
            Vector3 directionWS;
            Vector3 finalOrigin;
            
            // Determine if we should use the offset node pose or the camera pose
            if (offsetNode != null && cameraPose.offsetNodePosition != Vector3.zero)
            {
                // Use captured offset node pose
                directionWS = cameraPose.offsetNodeRotation * directionLocal;
                finalOrigin = cameraPose.offsetNodePosition;
            }
            else
            {
                // Use captured camera pose
                directionWS = cameraPose.rotation * directionLocal;
                finalOrigin = cameraPose.position;
            }

            Ray finalRay = new Ray(finalOrigin, directionWS);

            // Optionally visualize the entire ray
            if (visualizeRays)
            {
                Vector3 endPos = finalRay.origin + finalRay.direction * maxRayDistance;
                VisualizeRaySegment(finalRay.origin, endPos, Color.cyan);
            }

            // 4) Do a Physics.Raycast
            if (Physics.Raycast(finalRay, out RaycastHit hit, maxRayDistance))
            {
                // Visualize the "hit" portion in green
                if (visualizeRays && showHitSegment)
                {
                    VisualizeRaySegment(finalRay.origin, hit.point, Color.green);
                }

                // Register or update the anchor
                string detectedLabel = box.label;
                sceneObjectManager.RegisterOrUpdateAnchor(detectedLabel, hit.point);
            }
            else
            {
                Debug.Log($"Raycast missed for box '{box.label}' center=({centerX},{centerY}).");
            }
        }

        // Optionally visualize the captured camera pose as a frustum
        if (visualizeOffsetNodeCamera)
        {
            // Create a temporary GameObject to visualize the captured camera pose
            GameObject tempCamObj = new GameObject("CapturedCameraPose");
            debugVisualizationObjects.Add(tempCamObj);
            
            // Set it to the captured pose
            tempCamObj.transform.position = offsetNode != null ? cameraPose.offsetNodePosition : cameraPose.position;
            tempCamObj.transform.rotation = offsetNode != null ? cameraPose.offsetNodeRotation : cameraPose.rotation;
            
            // Visualize its frustum
            VisualizeOffsetNodeCamera(tempCamObj.transform, offsetNodeFOV, offsetNodeAspect, offsetNodeNear, offsetNodeFar);
        }
    }

    /// <summary>
    /// Original method - keeping for backward compatibility
    /// </summary>
    public void OnBoxesUpdated(List<Box2DResult> boxes)
    {
        // For backward compatibility, redirect to the new method with current camera pose
        if (xrCamera != null)
        {
            var currentPose = new Gemini2DBoundingBoxDetector.CameraPoseData(
                xrCamera.transform, offsetNode);
            
            OnBoxesUpdatedWithCameraPose(boxes, currentPose);
        }
        else
        {
            Debug.LogError("Cannot process boxes - xrCamera is null");
        }
    }

    /// <summary>
    /// Unprojects a 2D pixel (px, py) into a 3D direction based on the given intrinsics & extrinsics.
    /// You can optionally flip X/Y or apply extrinsics transforms if needed.
    /// </summary>
    public static Vector3 UnprojectPixel(
        float px,
        float py,
        float imageWidth,
        float imageHeight,
        bool flipX,
        bool flipY,
        bool useExtrinsics,
        float3x3 intrinsicsMatrix,
        float4x4 extrinsicsMatrix
    )
    {
        if (flipX) px = imageWidth - px;
        if (flipY) py = imageHeight - py;

        float3 imageVec = new float3(px, py, 1.0f);
        float3x3 Kinv = math.inverse(intrinsicsMatrix);
        float3 camDir = math.mul(Kinv, imageVec);

        if (useExtrinsics)
        {
            float4 d = new float4(camDir.x, camDir.y, camDir.z, 0f);
            float4 r = math.mul(extrinsicsMatrix, d);
            return new Vector3(r.x, r.y, r.z);
        }
        else
        {
            return new Vector3(camDir.x, camDir.y, camDir.z);
        }
    }

    /// <summary>
    /// Visualizes the offsetNode's frustum as magenta lines, plus a red line for forward direction.
    /// </summary>
    private void VisualizeOffsetNodeCamera(
        Transform node, float fovY, float aspect, float near, float far
    )
    {
        float halfHeightNear = near * Mathf.Tan(0.5f * fovY * Mathf.Deg2Rad);
        float halfWidthNear = halfHeightNear * aspect;

        float halfHeightFar = far * Mathf.Tan(0.5f * fovY * Mathf.Deg2Rad);
        float halfWidthFar = halfHeightFar * aspect;

        Vector3 nearBL = new Vector3(-halfWidthNear, -halfHeightNear, near);
        Vector3 nearBR = new Vector3(+halfWidthNear, -halfHeightNear, near);
        Vector3 nearTL = new Vector3(-halfWidthNear, +halfHeightNear, near);
        Vector3 nearTR = new Vector3(+halfWidthNear, +halfHeightNear, near);

        Vector3 farBL  = new Vector3(-halfWidthFar, -halfHeightFar, far);
        Vector3 farBR  = new Vector3(+halfWidthFar, -halfHeightFar, far);
        Vector3 farTL  = new Vector3(-halfWidthFar, +halfHeightFar, far);
        Vector3 farTR  = new Vector3(+halfWidthFar, +halfHeightFar, far);

        nearBL = node.TransformPoint(nearBL);
        nearBR = node.TransformPoint(nearBR);
        nearTL = node.TransformPoint(nearTL);
        nearTR = node.TransformPoint(nearTR);

        farBL  = node.TransformPoint(farBL);
        farBR  = node.TransformPoint(farBR);
        farTL  = node.TransformPoint(farTL);
        farTR  = node.TransformPoint(farTR);

        DrawLine(nearBL, nearBR, Color.magenta);
        DrawLine(nearBR, nearTR, Color.magenta);
        DrawLine(nearTR, nearTL, Color.magenta);
        DrawLine(nearTL, nearBL, Color.magenta);

        DrawLine(farBL, farBR, Color.magenta);
        DrawLine(farBR, farTR, Color.magenta);
        DrawLine(farTR, farTL, Color.magenta);
        DrawLine(farTL, farBL, Color.magenta);

        DrawLine(nearBL, farBL, Color.magenta);
        DrawLine(nearBR, farBR, Color.magenta);
        DrawLine(nearTL, farTL, Color.magenta);
        DrawLine(nearTR, farTR, Color.magenta);

        // Red line for forward direction
        Vector3 nodePosition = node.position;
        Vector3 directionEnd = nodePosition + node.forward * far * 4f; 
        DrawLine(nodePosition, directionEnd, Color.red);
    }

    /// <summary>
    /// Draws a single line (start->end) with a LineRenderer for debugging.
    /// We'll store it in 'spawnedObjects' so we can clear them next time.
    /// </summary>
    private void DrawLine(Vector3 start, Vector3 end, Color color)
    {
        GameObject lineObj = new GameObject("Line_OffsetCamVis");
        // Add to debug objects instead of spawnedObjects
        debugVisualizationObjects.Add(lineObj);

        var lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);

        lr.startWidth = rayWidth;
        lr.endWidth = rayWidth;

        if (lineMaterial != null)
            lr.material = lineMaterial;
        else
            lr.material = new Material(Shader.Find("Sprites/Default"));

        lr.material.color = color;
    }

    /// <summary>
    /// Visualizes a ray segment from start to end (for the ray's path).
    /// </summary>
    private void VisualizeRaySegment(Vector3 start, Vector3 end, Color color)
    {
        var lineObj = new GameObject("RayVisualizer");
        // Add to debug objects instead of spawnedObjects
        debugVisualizationObjects.Add(lineObj);

        var lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);

        lr.startWidth = rayWidth;
        lr.endWidth = rayWidth;

        if (lineMaterial != null) lr.material = lineMaterial;
        else lr.material = new Material(Shader.Find("Sprites/Default"));

        lr.material.color = color;
    }

    /// <summary>
    /// Clears only the debug visualization objects (lines) from the previous frame
    /// </summary>
    private void ClearDebugObjects()
    {
        foreach (var obj in debugVisualizationObjects)
        {
            if (obj) Destroy(obj);
        }
        debugVisualizationObjects.Clear();
    }
}
