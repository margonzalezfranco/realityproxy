using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Unity.Mathematics; // for float3x3, float4x4

public class GeminiRaycast : MonoBehaviour
{
    [Header("Camera for Raycasting (Left Eye)")]
    [Tooltip("Assign the XR camera whose Tracked Pose Driver is set to LeftEye. " +
             "We'll use its position as the base, but final offset is from offsetNode.")]
    public Camera xrCamera;

    [Header("Manual Offset Node")]
    [Tooltip("An empty child object under XR Camera for final offset/rotation.")]
    public Transform offsetNode;

    [Header("Sphere Settings")]
    public GameObject spherePrefab;
    public Material sphereMaterial;
    public float sphereSize = 0.05f;

    [Header("Label Settings")]
    public GameObject labelPrefab;
    public float labelOffset = 1.2f;

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

    // Store spawned spheres/lines/labels to clear
    private List<GameObject> spawnedObjects = new List<GameObject>();

    // Hard-coded camera resolution
    private int imageWidth = 1920;
    private int imageHeight = 1080;

    private float3x3 intrinsicsMatrix = new float3x3(
        736.6339f,  0.0f,    960.0f,
        0.0f,       736.6339f, 540.0f,
        0.0f,       0.0f,    1.0f
    );

    private float4x4 extrinsicsMatrix = new float4x4(
        0.99122864f,  -0.0038917887f, -0.13210104f,  0.024276158f,
        0.006838121f, -0.99671704f,    0.08067425f, -0.02069169f,
       -0.13198134f,  -0.08086995f,   -0.9879479f,  -0.057551354f,
        0.0f,          0.0f,           0.0f,         1.0f
    );

    public void OnBoxesUpdated(List<Box2DResult> boxes)
    {
        ClearSpawnedObjects();

        if (boxes == null || boxes.Count == 0)
        {
            Debug.Log("No bounding boxes to process.");
            return;
        }

        // 1) If offsetNode is missing, fallback to camera
        if (!offsetNode)
        {
            Debug.LogWarning("No offsetNode assigned, using xrCamera transform directly.");
        }

        foreach (var box in boxes)
        {
            // box_2d = [ymin, xmin, ymax, xmax]
            float ymin = box.box_2d[0];
            float xmin = box.box_2d[1];
            float ymax = box.box_2d[2];
            float xmax = box.box_2d[3];

            // 2) Convert [0..1000] -> pixel coords
            float centerX = (xmin + xmax) * 0.5f * (imageWidth / 1000f);
            float centerY = (ymin + ymax) * 0.5f * (imageHeight / 1000f);

            // 3) unproject in camera local space (no extrinsics or partial)
            //    if you want extrinsics rotation, set useExtrinsics = true
            Vector3 directionLocal = UnprojectPixel(
                centerX, centerY,
                imageWidth, imageHeight,
                flipX: false,
                flipY: true,  // top-left -> bottom-left
                useExtrinsics: false,
                intrinsicsMatrix,
                extrinsicsMatrix
            );

            // (Optional) Flip Z if Apple device anchor's z is reversed
            // directionLocal = -directionLocal;

            // 4) Now find final ray origin & direction from offsetNode
            // if offsetNode is null, fallback to xrCamera
            Transform finalNode = offsetNode ? offsetNode : xrCamera.transform;

            // directionLocal is still "camera local", plus or minus extrinsics rotation if you want
            // Next, we rotate it by the finalNode's world rotation
            Quaternion finalWorldRot = finalNode.rotation;
            Vector3 directionWS = finalWorldRot * directionLocal.normalized;

            Vector3 finalOrigin = finalNode.position;

            // 5) build the final Ray
            Ray finalRay = new Ray(finalOrigin, directionWS);

            if (visualizeRays)
            {
                Vector3 endPos = finalRay.origin + finalRay.direction * maxRayDistance;
                VisualizeRaySegment(finalRay.origin, endPos, Color.cyan);
            }

            // 6) Raycast
            if (Physics.Raycast(finalRay, out RaycastHit hit, maxRayDistance))
            {
                if (visualizeRays && showHitSegment)
                {
                    VisualizeRaySegment(finalRay.origin, hit.point, Color.green);
                }
                // spawn sphere
                SpawnSphereWithLabel(hit.point, box.label);
            }
            else
            {
                Debug.Log($"Raycast missed for box '{box.label}' center=({centerX},{centerY}).");
            }
        }

        if (visualizeOffsetNodeCamera && offsetNode)
        {
            VisualizeOffsetNodeCamera(offsetNode, offsetNodeFOV, offsetNodeAspect, offsetNodeNear, offsetNodeFar);
        }
    }

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
    /// Visualize the view cone of the "camera" offsetNode to easily see its position and direction in the world.
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

        // Add direction ray visualization
        Vector3 nodePosition = node.position;
        Vector3 directionEnd = nodePosition + node.forward * far * 1.5f; // Extend past far plane
        DrawLine(nodePosition, directionEnd, Color.red);
    }

    private void DrawLine(Vector3 start, Vector3 end, Color color)
    {
        GameObject lineObj = new GameObject("Line_OffsetCamVis");
        spawnedObjects.Add(lineObj);

        var lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);

        lr.startWidth = rayWidth;
        lr.endWidth = rayWidth;

        if (lineMaterial)
            lr.material = lineMaterial;
        else
            lr.material = new Material(Shader.Find("Sprites/Default"));

        lr.material.color = color;
    }

    private void SpawnSphereWithLabel(Vector3 position, string label)
    {
        GameObject sphereObj = (spherePrefab != null)
            ? Instantiate(spherePrefab, position, Quaternion.identity)
            : GameObject.CreatePrimitive(PrimitiveType.Sphere);

        sphereObj.transform.position = position;
        sphereObj.name = $"GeminiHit_{label}";
        spawnedObjects.Add(sphereObj);

        sphereObj.transform.localScale = Vector3.one * sphereSize;

        if (sphereMaterial != null)
        {
            var rend = sphereObj.GetComponentInChildren<Renderer>();
            if (rend != null) rend.material = sphereMaterial;
        }

        if (labelPrefab != null)
        {
            var lblObj = Instantiate(labelPrefab, sphereObj.transform);
            lblObj.name = $"Label_{label}";
            lblObj.transform.localPosition = new Vector3(0f, labelOffset, 0f);
            spawnedObjects.Add(lblObj);

            var tmp = lblObj.GetComponentInChildren<TextMeshPro>();
            if (tmp) tmp.text = label;
            else
            {
                var tmpUGUI = lblObj.GetComponentInChildren<TextMeshProUGUI>();
                if (tmpUGUI) tmpUGUI.text = label;
            }
        }
    }

    private void VisualizeRaySegment(Vector3 start, Vector3 end, Color color)
    {
        var lineObj = new GameObject("RayVisualizer");
        spawnedObjects.Add(lineObj);

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

    private void ClearSpawnedObjects()
    {
        foreach (var obj in spawnedObjects)
        {
            if (obj) Destroy(obj);
        }
        spawnedObjects.Clear();
    }
}
