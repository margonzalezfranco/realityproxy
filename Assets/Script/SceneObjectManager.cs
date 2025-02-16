using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Singleton manager that tracks all recognized objects in the scene,
/// preventing duplicates and maintaining a single authoritative list of anchors.
/// </summary>
public class SceneObjectManager : MonoBehaviour
{
    public static SceneObjectManager Instance { get; private set; }

    [Header("Matching Threshold")]
    [Tooltip("Distance threshold (in meters) to treat a new detection as the 'same' object.")]
    public float matchingRadius = 0.05f;

    [Header("Prefabs and Materials")]
    public GameObject spherePrefab;
    public Material sphereMaterial;
    public GameObject labelPrefab;
    public float sphereSize = 0.025f;
    public float labelOffset = 1.2f;
    [Tooltip("Scale multiplier for the label prefab")]
    public float labelScale = 0.1f;

    public Camera xrCamera; // Used to set the LookAtCamera for labels

    // (Optional) Additional references (InfoPanel, questionAnswerer, etc.)
    public GameObject InfoPanel;
    public GameObject answerPanel;
    public GeminiQuestionAnswerer questionAnswerer;

    // Our authoritative list of scene anchors
    private List<SceneObjectAnchor> anchors = new List<SceneObjectAnchor>();
    public GeminiRaycast geminiRaycast;

    [Header("Level 2 Relationship")]
    [Tooltip("Manager that draws lines between related items.")]
    public RelationshipLineManager relationLineManager;

    [Tooltip("Manager that tracks all recognized objects in the scene.")]
    public SceneObjectManager sceneObjManager;

    private void Awake()
    {
        // Basic singleton pattern
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public List<SceneObjectAnchor> GetAllAnchors()
    {
        return anchors;
    }

    public SceneObjectAnchor GetAnchorByLabel(string label)
    {
        return anchors.Find(a => a.label == label);
    }

    /// <summary>
    /// Called whenever we get a new detection from GeminiRaycast or elsewhere.
    /// This tries to find an existing anchor near 'hitPos'. If found, we optionally update it.
    /// Otherwise, we create a new anchor.
    /// </summary>
    public void RegisterOrUpdateAnchor(string newLabel, Vector3 hitPos)
    {
        // 1) Attempt to find an existing anchor within matchingRadius
        SceneObjectAnchor existing = CheckForExistingAnchor(hitPos);
        if (existing != null)
        {
            // It's the same object => maybe update label if "better"
            UpdateAnchorLabel(existing, newLabel);
        }
        else
        {
            // No match => create a new anchor
            SceneObjectAnchor newAnchor = CreateNewAnchor(newLabel, hitPos);
            anchors.Add(newAnchor);
        }
    }

    /// <summary>
    /// Check if there is an existing anchor whose position is within 'matchingRadius' of 'hitPos'.
    /// Return that anchor if found, else return null.
    /// </summary>
    private SceneObjectAnchor CheckForExistingAnchor(Vector3 hitPos)
    {
        foreach (var anchor in anchors)
        {
            float dist = Vector3.Distance(anchor.position, hitPos);
            if (dist <= anchor.boundingRadius || dist <= matchingRadius)
            {
                // Possibly the same object. 
                // You could refine further (e.g. compare color histograms), but for now we accept this match.
                return anchor;
            }
        }
        return null;
    }

    /// <summary>
    /// Possibly update the label on an existing anchor.
    /// This logic is naive: if the anchor isn't user-locked, we just override.
    /// In a real system, you'd compare confidences or specificity.
    /// </summary>
    private void UpdateAnchorLabel(SceneObjectAnchor anchor, string newLabel)
    {
        if (anchor.userLocked)
        {
            // If user locked it, do nothing
            return;
        }

        // If the new label is identical or less useful, you might skip.
        // For a simple example, let's just do a direct override if it's not the same string.
        if (!anchor.label.Equals(newLabel))
        {
            anchor.label = newLabel;
            Debug.Log($"Anchor updated label to: {newLabel}");

            // If we have a sphere object with a label, let's update its text
            if (anchor.sphereObj != null)
            {
                var tmp = anchor.sphereObj.GetComponentInChildren<TMPro.TextMeshPro>();
                if (tmp) tmp.text = newLabel;
            }
        }
    }

    /// <summary>
    /// Creates a new anchor data + spawns the sphere/label in the scene.
    /// </summary>
    private SceneObjectAnchor CreateNewAnchor(string label, Vector3 position)
    {
        SceneObjectAnchor newAnchor = new SceneObjectAnchor
        {
            label = label,
            position = position,
            boundingRadius = matchingRadius,
            userLocked = false
        };

        GameObject sphereObj = SpawnSphereWithLabel(position, label);
        newAnchor.sphereObj = sphereObj;

        Debug.Log($"Created new anchor with label={label} at pos={position}");
        return newAnchor;
    }

    /// <summary>
    /// Spawns a sphere with a label in 3D, returning the GameObject so we can store it in the anchor.
    /// This is basically the logic you had in GeminiRaycast, but extracted here so it's
    /// only done once per unique object anchor.
    /// </summary>
    private GameObject SpawnSphereWithLabel(Vector3 position, string label)
    {
        GameObject sphereObj = (spherePrefab != null)
            ? Instantiate(spherePrefab, position, Quaternion.identity)
            : GameObject.CreatePrimitive(PrimitiveType.Sphere);

        sphereObj.transform.position = position;
        sphereObj.name = $"GeminiHit_{label}";
        geminiRaycast.spawnedObjects.Add(sphereObj);

        sphereObj.transform.localScale = Vector3.one * sphereSize;

        if (sphereMaterial != null)
        {
            var rend = sphereObj.GetComponentInChildren<Renderer>();
            if (rend != null) rend.material = sphereMaterial;
        }

        SphereToggleScript sphereToggleScript = null;
        if (InfoPanel != null)
        {
            sphereToggleScript = sphereObj.GetComponentInChildren<SphereToggleScript>();
            if (sphereToggleScript != null) 
            {
                sphereToggleScript.InfoPanel = InfoPanel;
                sphereToggleScript.questionsParent = InfoPanel.transform;
                sphereToggleScript.relationLineManager = relationLineManager;
                sphereToggleScript.sceneObjManager = sceneObjManager;
            }

            var menuScript = InfoPanel.GetComponentInChildren<MenuScript>();
            if (menuScript != null) sphereToggleScript.menuScript = menuScript;
        }

        if (labelPrefab != null)
        {
            var lblObj = Instantiate(labelPrefab, sphereObj.transform);
            lblObj.name = $"Label_{label}";
            lblObj.transform.localPosition = new Vector3(0f, labelOffset, 0f);
            lblObj.transform.localScale = Vector3.one * labelScale;
            
            // Add a LookAt component to make the label face the camera
            var lookAt = lblObj.AddComponent<LookAtCamera>();
            lookAt.targetCamera = xrCamera;
            
            geminiRaycast.spawnedObjects.Add(lblObj);

            var tmp = lblObj.GetComponentInChildren<TextMeshPro>();
            if (tmp) tmp.text = label;

            // give tmp to sphereToggleScript
            if (sphereToggleScript != null) sphereToggleScript.labelUnderSphere = tmp;
        }

        if (questionAnswerer != null)
        {
            // pass the questionAnswerer to the sphereToggleScript
            if (sphereToggleScript != null)
            {
                sphereToggleScript.questionAnswerer = questionAnswerer;
                sphereToggleScript.answerPanel = answerPanel;
            }
        }

        return sphereObj;
    }

    public SceneObjectAnchor GetAnchorByGameObject(GameObject obj)
    {
        return anchors.Find(a => a.sphereObj == obj);
    }
}
