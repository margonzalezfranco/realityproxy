using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// When new bounding boxes arrive from Gemini2DBoundingBoxDetector,
/// this script converts them to 3D rays (viewport-based) and spawns spheres
/// where the rays intersect the scene.
/// </summary>
public class GeminiRaycast : MonoBehaviour
{
    [Header("Camera for Raycasting (e.g. Main Camera in XR)")]
    public Camera xrCamera;

    [Header("Sphere prefab (optional)")]
    public GameObject spherePrefab;  // If null, we'll create a default primitive sphere

    [Tooltip("Max distance for raycasting.")]
    public float maxRayDistance = 100f;

    /// <summary>
    /// Call this method once you have a fresh list of bounding boxes from Gemini.
    /// Typically invoked by Gemini2DBoundingBoxDetector after detection completes.
    /// </summary>
    public void OnBoxesUpdated(List<Box2DResult> boxes)
    {
        if (xrCamera == null)
        {
            Debug.LogError("GeminiRaycast: XR Camera not set!");
            return;
        }
        if (boxes == null || boxes.Count == 0)
        {
            Debug.Log("No boxes to process for raycasting.");
            return;
        }

        // For each bounding box, compute center, do a raycast, spawn a sphere
        foreach (var box in boxes)
        {
            // box_2d: [ymin, xmin, ymax, xmax]
            float ymin = box.box_2d[0];
            float xmin = box.box_2d[1];
            float ymax = box.box_2d[2];
            float xmax = box.box_2d[3];

            // 1) Box center in 0..1000 (top-left origin)
            float centerX = (xmin + xmax) * 0.5f; // 0..1000 horizontally
            float centerY = (ymin + ymax) * 0.5f; // 0..1000 vertically

            // 2) Convert to viewport coords [0..1], flipping Y for bottom-up
            float u = centerX / 1000f;
            float v = 1f - (centerY / 1000f);

            // 3) Viewport to Ray
            Ray ray = xrCamera.ViewportPointToRay(new Vector3(u, v, 0f));

            // 4) Raycast
            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
            {
                // 5) Spawn sphere at the hit point
                SpawnSphereAt(hit.point, box.label);
            }
            else
            {
                Debug.Log($"Raycast missed for box: {box.label} (u={u:F2}, v={v:F2}).");
            }
        }
    }

    private void SpawnSphereAt(Vector3 position, string label)
    {
        GameObject sphereObj;
        if (spherePrefab != null)
        {
            sphereObj = Instantiate(spherePrefab, position, Quaternion.identity);
        }
        else
        {
            sphereObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereObj.transform.position = position;
            sphereObj.transform.localScale = Vector3.one * 0.05f; // small sphere
        }
        sphereObj.name = $"GeminiHit_{label}";
    }
}
