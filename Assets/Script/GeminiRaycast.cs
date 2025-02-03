using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Unity.Mathematics; // for float3x3, float4x4

/// <summary>
/// Uses only the intrinsics-based unprojection, ignoring extrinsics translation.
/// Rays start from the XR camera's left-eye position (TrackedPoseDriver).
/// If we want to rotate directions by the extrinsics' rotation, we can embed that in extrinsicsMatrixNoTrans.
/// </summary>
public class GeminiRaycast : MonoBehaviour
{
    [Header("Camera for Raycasting (Left Eye)")]
    [Tooltip("Assign the XR camera whose Tracked Pose Driver is set to LeftEye. " +
             "We'll use its position as the ray origin.")]
    public Camera xrCamera;

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

    [Header("Camera Frame Visualization (Optional)")]
    public bool visualizeCameraFrame = false;
    public bool visualizeFarPlane = false;

    // Track created objects (spheres, lines, labels) so we can clear them
    private List<GameObject> spawnedObjects = new List<GameObject>();

    // Hard-coded camera resolution
    private int imageWidth = 1920;
    private int imageHeight = 1080;

    // Hard-coded intrinsics (row-major).
    // row0 = (736.6339, 0.0,     960.0)
    // row1 = (0.0,      736.6339, 540.0)
    // row2 = (0.0,      0.0,      1.0)
    private float3x3 intrinsicsMatrix = new float3x3(
        736.6339f,   0.0f,     960.0f,
        0.0f,        736.6339f,540.0f,
        0.0f,        0.0f,     1.0f
    );

    // Hard-coded extrinsics (row-major), but we'll zero out the translation (no translation).
    // row0= (0.99122864,  0.006838121, -0.13198134,   0.0f)
    // row1= (-0.0038917887, -0.99671704, -0.08086995, 0.0f)
    // row2= (-0.13210104,   0.08067425,  -0.9879479,  0.0f)
    // row3= (0.0f,          0.0f,         0.0f,       1.0f)  // ignoring translation
    private float4x4 extrinsicsMatrixNoTrans = new float4x4(
        0.99122864f,   0.006838121f, -0.13198134f,   0.0f,
       -0.0038917887f, -0.99671704f, -0.08086995f,   0.0f,
       -0.13210104f,    0.08067425f, -0.9879479f,    0.0f,
        0.0f,           0.0f,         0.0f,          1.0f
    );

    /// <summary>
    /// Called once we have bounding boxes from Gemini. 
    /// For each box, we unproject using intrinsics, possibly rotate with extrinsicsMatrixNoTrans,
    /// start from the XR camera's left-eye position, cast a ray, spawn a sphere on hit.
    /// </summary>
    public void OnBoxesUpdated(List<Box2DResult> boxes)
    {
        // Clear old objects from previous detection
        ClearSpawnedObjects();

        if (boxes == null || boxes.Count == 0)
        {
            Debug.Log("No bounding boxes to process.");
            return;
        }

        // 1) The XR camera's left-eye transform
        Vector3 leftEyeOrigin = (xrCamera != null) ? xrCamera.transform.position : Vector3.zero;
        Quaternion leftEyeRotation = (xrCamera != null) ? xrCamera.transform.rotation : Quaternion.identity;

        foreach (var box in boxes)
        {
            // box_2d = [ymin, xmin, ymax, xmax] in 0..1000
            float ymin = box.box_2d[0];
            float xmin = box.box_2d[1];
            float ymax = box.box_2d[2];
            float xmax = box.box_2d[3];

            // Scale from 0..1000 to pixel coordinates
            float centerX = (xmin + xmax) * 0.5f * (imageWidth / 1000f); 
            float centerY = (ymin + ymax) * 0.5f * (imageHeight / 1000f);

            // 2) Unproject into camera space via intrinsics. 
            //    We'll also flipX and flipY if needed.
            Vector3 directionLocal = VisionProMath.UnprojectToLocalNoTranslation(
                centerX, centerY,
                imageWidth, imageHeight,
                flipX: false, 
                flipY: true,   // top-left => bottom-left
                intrinsicsMatrix,
                extrinsicsMatrixNoTrans
            );

            // If your real camera uses +Z forward but you need -Z, do:
            // directionLocal = -directionLocal;

            // 3) Now transform from local "camera" space to the left eye's actual orientation
            //    if you want the extrinsics rotation, you can skip or remove it 
            //    and just rely on leftEyeRotation alone. 
            //    For maximum control, let's do:
            Vector3 directionWS = leftEyeRotation * directionLocal;

            // 4) Build the final ray from the left-eye position
            Ray finalRay = new Ray(leftEyeOrigin, directionWS.normalized);

            // (Optional) visualize
            if (visualizeRays)
            {
                Vector3 endPos = finalRay.origin + finalRay.direction * maxRayDistance;
                VisualizeRaySegment(finalRay.origin, endPos, Color.cyan);
            }

            // 5) Raycast
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

        // (Optional) visualize XR camera frustum
        if (visualizeCameraFrame && xrCamera != null)
        {
            VisualizeCameraFrustum();
        }
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
            var renderer = sphereObj.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                renderer.material = sphereMaterial;
            }
        }

        if (labelPrefab != null)
        {
            var labelObj = Instantiate(labelPrefab);
            labelObj.name = $"Label_{label}";
            spawnedObjects.Add(labelObj);

            labelObj.transform.SetParent(sphereObj.transform, false);
            labelObj.transform.localPosition = new Vector3(0f, labelOffset, 0f);

            var tmp = labelObj.GetComponentInChildren<TextMeshPro>();
            if (tmp) tmp.text = label;
            else
            {
                var tmpUGUI = labelObj.GetComponentInChildren<TextMeshProUGUI>();
                if (tmpUGUI) tmpUGUI.text = label;
            }
        }
    }

    private void VisualizeRaySegment(Vector3 start, Vector3 end, Color color)
    {
        GameObject lineObj = new GameObject("RayVisualizer");
        spawnedObjects.Add(lineObj);

        var lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);

        lr.startWidth = rayWidth;
        lr.endWidth = rayWidth;

        if (lineMaterial != null)
        {
            lr.material = lineMaterial;
        }
        else
        {
            lr.material = new Material(Shader.Find("Sprites/Default"));
        }
        lr.material.color = color;
    }

    private void VisualizeCameraFrustum()
    {
        if (xrCamera == null) return;

        float near = xrCamera.nearClipPlane;
        float halfHeight = near * Mathf.Tan(0.5f * xrCamera.fieldOfView * Mathf.Deg2Rad);
        float halfWidth = halfHeight * xrCamera.aspect;

        Vector3 nearBL = new Vector3(-halfWidth, -halfHeight, near);
        Vector3 nearBR = new Vector3(+halfWidth, -halfHeight, near);
        Vector3 nearTL = new Vector3(-halfWidth, +halfHeight, near);
        Vector3 nearTR = new Vector3(+halfWidth, +halfHeight, near);

        nearBL = xrCamera.transform.TransformPoint(nearBL);
        nearBR = xrCamera.transform.TransformPoint(nearBR);
        nearTL = xrCamera.transform.TransformPoint(nearTL);
        nearTR = xrCamera.transform.TransformPoint(nearTR);

        VisualizeRaySegment(nearBL, nearBR, Color.magenta);
        VisualizeRaySegment(nearBR, nearTR, Color.magenta);
        VisualizeRaySegment(nearTR, nearTL, Color.magenta);
        VisualizeRaySegment(nearTL, nearBL, Color.magenta);
    }

    private void ClearSpawnedObjects()
    {
        foreach (var obj in spawnedObjects)
        {
            if (obj != null) Destroy(obj);
        }
        spawnedObjects.Clear();
    }
}