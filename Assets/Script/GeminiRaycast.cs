using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// When new bounding boxes arrive from Gemini2DBoundingBoxDetector,
/// this script converts them to 3D rays (viewport-based) and spawns spheres
/// (with optional label) where the rays intersect the scene.
/// </summary>
public class GeminiRaycast : MonoBehaviour
{
    [Header("Camera for Raycasting (e.g. Main Camera in XR)")]
    public Camera xrCamera;

    [Header("Sphere Settings")]
    [Tooltip("Sphere prefab to use. If null, we'll create a default primitive sphere.")]
    public GameObject spherePrefab;
    
    [Tooltip("Optional material for spheres (if no prefab or prefab doesn't include it).")]
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

    // We'll track all spawned spheres so we can clear them later
    private List<GameObject> spawnedObjects = new List<GameObject>();

    /// <summary>
    /// Call this method once you have a fresh list of bounding boxes from Gemini.
    /// Typically invoked by Gemini2DBoundingBoxDetector after detection completes.
    /// 
    /// This also clears old spheres/labels before spawning new ones.
    /// </summary>
    public void OnBoxesUpdated(List<Box2DResult> boxes)
    {
        if (xrCamera == null)
        {
            Debug.LogError("GeminiRaycast: XR Camera not set!");
            return;
        }
        
        // 1) Clear old spheres from a previous run
        ClearSpawnedObjects();

        if (boxes == null || boxes.Count == 0)
        {
            Debug.Log("No boxes to process for raycasting.");
            return;
        }

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

            // Convert top-left–down coords to viewport (0..1, bottom-left)
            float u = centerX / 1000f;
            float v = 1f - (centerY / 1000f);

            Ray ray = xrCamera.ViewportPointToRay(new Vector3(u, v, 0f));

            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
            {
                // place a sphere at hit.point
                SpawnSphereWithLabel(hit.point, box.label);
            }
            else
            {
                Debug.Log($"Raycast missed for box '{box.label}' (u={u:F2}, v={v:F2}).");
            }
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

        // 3) If we have a material, apply it (only if the prefab or primitive doesn't have one)
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

            // Make it a child of the sphere (optional) so it moves with the sphere
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
    /// Clears all previously spawned spheres/labels.
    /// Useful if you re-run detection.
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
