using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// When new bounding boxes arrive from Gemini2DBoundingBoxDetector,
/// this script converts them to 3D rays (viewport-based) and spawns spheres
/// (with optional label) where the rays intersect the scene.
///
/// This updated version also includes optional line rendering to visualize
/// each ray (and optional camera frustum).
/// </summary>
public class GeminiRaycast : MonoBehaviour
{
    [Header("Camera for Raycasting (e.g. Main Camera in XR)")]
    public Camera xrCamera;

    [Header("Sphere Settings")]
    [Tooltip("Sphere prefab to use. If null, we'll create a default primitive sphere.")]
    public GameObject spherePrefab;
    
    [Tooltip("Optional material for spheres (if no prefab or prefab doesn't have one).")]
    public Material sphereMaterial;

    [Tooltip("Sphere radius (or scale factor).")]
    public float sphereSize = 0.05f;

    [Header("Label Settings")]
    [Tooltip("Prefab for a TextMeshPro label that we'll place above each sphere.")]
    public GameObject labelPrefab;  // Should contain a TextMeshPro component
    [Tooltip("Vertical offset above the sphere center to place the label.")]
    public float labelOffset = 1.2f;

    [Header("Raycast Settings")]
    [Tooltip("Max distance for raycasting.")]
    public float maxRayDistance = 100f;

    [Header("Ray Visualization (Optional)")]
    [Tooltip("If true, draw lines showing the rays in 3D space.")]
    public bool visualizeRays = false;

    [Tooltip("Material used by the LineRenderer to display rays.")]
    public Material lineMaterial;

    [Tooltip("Width of the debug rays.")]
    public float rayWidth = 0.002f;

    [Tooltip("If true, also draw a line from the ray origin to the actual hit point.")]
    public bool showHitSegment = true;

    [Header("Camera Frame Visualization")]
    [Tooltip("If true, draw the camera's near-plane rectangle (and optionally far-plane).")]
    public bool visualizeCameraFrame = false;

    [Tooltip("If true, also draw the far-plane rectangle and lines connecting near/far corners.")]
    public bool visualizeFarPlane = false;

    // We'll track all spawned spheres & lines so we can clear them later
    private List<GameObject> spawnedObjects = new List<GameObject>();

    /// <summary>
    /// Call this method once you have a fresh list of bounding boxes from Gemini.
    /// Typically invoked by Gemini2DBoundingBoxDetector after detection completes.
    /// 
    /// This also clears old spheres/labels (and lines) before spawning new ones.
    /// </summary>
    public void OnBoxesUpdated(List<Box2DResult> boxes)
    {
        if (xrCamera == null)
        {
            Debug.LogError("GeminiRaycast: XR Camera not set!");
            return;
        }
        
        // 1) Clear old spheres/labels/lines from a previous run
        ClearSpawnedObjects();

        if (boxes == null || boxes.Count == 0)
        {
            Debug.Log("No boxes to process for raycasting.");
        }
        else
        {
            // 2) For each bounding box, compute center, do a raycast, spawn sphere+label
            foreach (var box in boxes)
            {
                // box_2d: [ymin, xmin, ymax, xmax]
                float ymin = box.box_2d[0];
                float xmin = box.box_2d[1];
                float ymax = box.box_2d[2];
                float xmax = box.box_2d[3];

                float centerX = (xmin + xmax) * 0.5f; // 0..1000 horizontally
                float centerY = (ymin + ymax) * 0.5f; // 0..1000 vertically

                // Convert top-left–down coords to viewport (0..1, bottom-left in Unity)
                float u = centerX / 1000f;
                float v = 1f - (centerY / 1000f);

                // Create the Ray from camera at that viewport coordinate
                Ray ray = xrCamera.ViewportPointToRay(new Vector3(u, v, 0f));

                // (Optional) visualize the full potential ray
                if (visualizeRays)
                {
                    VisualizeRaySegment(ray.origin, ray.origin + ray.direction * maxRayDistance, Color.cyan);
                }

                // 3) Raycast
                if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
                {
                    // If we want to see exactly where it hit
                    if (visualizeRays && showHitSegment)
                    {
                        VisualizeRaySegment(ray.origin, hit.point, Color.green);
                    }

                    // place a sphere at hit.point
                    SpawnSphereWithLabel(hit.point, box.label);
                }
                else
                {
                    Debug.Log($"Raycast missed for box '{box.label}' (u={u:F2}, v={v:F2}).");
                }
            }
        }

        // 4) (Optional) visualize the camera's near/far planes
        if (visualizeCameraFrame)
        {
            VisualizeCameraFrustum();
        }
    }

    /// <summary>
    /// Spawns a sphere at the given position, applies a material (if set),
    /// and spawns a label above it with the bounding box's label.
    /// </summary>
    private void SpawnSphereWithLabel(Vector3 position, string label)
    {
        GameObject sphereObj;

        // 1) Create the sphere
        if (spherePrefab != null)
        {
            sphereObj = Instantiate(spherePrefab, position, Quaternion.identity);
        }
        else
        {
            sphereObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereObj.transform.position = position;
        }

        sphereObj.name = $"GeminiHit_{label}";
        spawnedObjects.Add(sphereObj);

        // 2) Adjust size
        sphereObj.transform.localScale = Vector3.one * sphereSize;

        // 3) If we have a material, apply it (only if the prefab/primitive doesn't have one)
        if (sphereMaterial != null)
        {
            var renderer = sphereObj.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                renderer.material = sphereMaterial;
            }
        }

        // 4) Spawn the label
        if (labelPrefab != null)
        {
            // Instantiate the label prefab above the sphere
            var labelObj = Instantiate(labelPrefab);
            labelObj.name = $"Label_{label}";
            spawnedObjects.Add(labelObj);

            // Make it a child of the sphere so it moves with the sphere
            labelObj.transform.SetParent(sphereObj.transform, false);
            
            // Position offset above the sphere
            labelObj.transform.localPosition = new Vector3(0f, labelOffset, 0f);

            // Try to set text
            var tmp = labelObj.GetComponentInChildren<TextMeshPro>();
            if (tmp)
            {
                tmp.text = label;
            }
            else
            {
                // Or if we have TextMeshProUGUI, depends on your label prefab
                var tmpUGUI = labelObj.GetComponentInChildren<TextMeshProUGUI>();
                if (tmpUGUI)
                {
                    tmpUGUI.text = label;
                }
            }
        }
    }

    /// <summary>
    /// Utility to draw a line between two points in the scene using a LineRenderer.
    /// We'll add the line object to 'spawnedObjects' so it clears later.
    /// </summary>
    private void VisualizeRaySegment(Vector3 start, Vector3 end, Color color)
    {
        // Create a new GameObject for the line
        GameObject lineObj = new GameObject("RayVisualizer");
        spawnedObjects.Add(lineObj);

        // Add a LineRenderer
        var lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);

        // Set widths
        lr.startWidth = rayWidth;
        lr.endWidth = rayWidth;

        // Use the user-assigned material if available, otherwise a default
        if (lineMaterial != null)
        {
            lr.material = lineMaterial;
        }
        else
        {
            // if no line material is assigned, use a simple default
            lr.material = new Material(Shader.Find("Sprites/Default"));
        }
        // color
        lr.material.color = color;
    }

    /// <summary>
    /// Draws a rectangle for the camera's near plane (and optionally far plane),
    /// allowing you to visualize the camera's FOV in the scene.
    /// </summary>
    private void VisualizeCameraFrustum()
    {
        if (xrCamera == null) return;

        // 1) near-plane corners in camera space
        float near = xrCamera.nearClipPlane;
        float halfHeight = near * Mathf.Tan(0.5f * xrCamera.fieldOfView * Mathf.Deg2Rad);
        float halfWidth = halfHeight * xrCamera.aspect;

        Vector3 nearBL = new Vector3(-halfWidth, -halfHeight, near);
        Vector3 nearBR = new Vector3(+halfWidth, -halfHeight, near);
        Vector3 nearTL = new Vector3(-halfWidth, +halfHeight, near);
        Vector3 nearTR = new Vector3(+halfWidth, +halfHeight, near);

        // transform them to world space
        nearBL = xrCamera.transform.TransformPoint(nearBL);
        nearBR = xrCamera.transform.TransformPoint(nearBR);
        nearTL = xrCamera.transform.TransformPoint(nearTL);
        nearTR = xrCamera.transform.TransformPoint(nearTR);

        // connect the corners
        VisualizeRaySegment(nearBL, nearBR, Color.magenta);
        VisualizeRaySegment(nearBR, nearTR, Color.magenta);
        VisualizeRaySegment(nearTR, nearTL, Color.magenta);
        VisualizeRaySegment(nearTL, nearBL, Color.magenta);

        if (visualizeFarPlane)
        {
            float farDist = xrCamera.farClipPlane;
            float farHalfHeight = farDist * Mathf.Tan(0.5f * xrCamera.fieldOfView * Mathf.Deg2Rad);
            float farHalfWidth = farHalfHeight * xrCamera.aspect;

            Vector3 farBL = new Vector3(-farHalfWidth, -farHalfHeight, farDist);
            Vector3 farBR = new Vector3(+farHalfWidth, -farHalfHeight, farDist);
            Vector3 farTL = new Vector3(-farHalfWidth, +farHalfHeight, farDist);
            Vector3 farTR = new Vector3(+farHalfWidth, +farHalfHeight, farDist);

            // transform to world space
            farBL = xrCamera.transform.TransformPoint(farBL);
            farBR = xrCamera.transform.TransformPoint(farBR);
            farTL = xrCamera.transform.TransformPoint(farTL);
            farTR = xrCamera.transform.TransformPoint(farTR);

            // connect corners in magenta or another color
            VisualizeRaySegment(farBL, farBR, Color.yellow);
            VisualizeRaySegment(farBR, farTR, Color.yellow);
            VisualizeRaySegment(farTR, farTL, Color.yellow);
            VisualizeRaySegment(farTL, farBL, Color.yellow);

            // connect near plane corners to far plane corners with white lines
            VisualizeRaySegment(nearBL, farBL, Color.white);
            VisualizeRaySegment(nearBR, farBR, Color.white);
            VisualizeRaySegment(nearTL, farTL, Color.white);
            VisualizeRaySegment(nearTR, farTR, Color.white);
        }
    }

    /// <summary>
    /// Clears all previously spawned spheres, labels, and line objects.
    /// </summary>
    private void ClearSpawnedObjects()
    {
        foreach (var obj in spawnedObjects)
        {
            if (obj != null)
            {
                Destroy(obj);
            }
        }
        spawnedObjects.Clear();
    }
}
