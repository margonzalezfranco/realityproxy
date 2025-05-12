using System.Collections.Generic;
using UnityEngine;
using TMPro;
using PolySpatial.Template;
using UnityEngine.XR.Interaction.Toolkit.UI;
using System;

/// <summary>
/// Singleton manager that tracks all recognized objects in the scene,
/// preventing duplicates and maintaining a single authoritative list of anchors.
/// </summary>
public class SceneObjectManager : MonoBehaviour
{
    public static SceneObjectManager Instance { get; private set; }

    // Event that fires when the anchor count changes (true = has objects, false = no objects)
    public event Action<bool> OnAnchorCountChanged;
    
    // Property to check if there are any anchors in the scene
    public bool HasAnchors => anchors != null && anchors.Count > 0;

    [Header("Matching Threshold")]
    [Tooltip("Distance threshold (in meters) to treat a new detection as the 'same' object.")]
    public float matchingRadius = 0.05f;

    [Header("Prefabs and Materials")]
    public GameObject spherePrefab;
    public Material sphereMaterial;
    public GameObject labelPrefab;
    public float sphereSize = 0.04f;
    public float labelOffset = 1.2f;
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

    [Tooltip("Scene analyzer to provide context for relationships.")]
    public SceneContextManager sceneContextManager;

    [Header("Level 3 Object Inspection")]
    public GameObject descriptionPanel;
    public TextMeshPro descriptionText;
    public GameObject pointingPlane;
    public TextMeshPro pointingPlaneText;
    public MyHandTracking handTracking;
    public GameObject recorderToggle;
    
    [Tooltip("Reference to the BaselineModeController for mode-specific behaviors")]
    public BaselineModeController baselineModeController;

    [Header("User Study Logging")]
    [SerializeField] private bool enableUserStudyLogging = true;

    public GameObject askSceneToggle;

    private void Awake()
    {
        // Basic singleton pattern
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
        
        // Find BaselineModeController if not set
        if (baselineModeController == null)
        {
            baselineModeController = FindObjectOfType<BaselineModeController>();
        }
        
        OnAnchorCountChanged += HandleAnchorCountChanged;
    }
    
    private void Start()
    {
        if (askSceneToggle != null)
        {
            askSceneToggle.SetActive(HasAnchors);
        }
    }
    
    private void OnDestroy()
    {
        OnAnchorCountChanged -= HandleAnchorCountChanged;
    }
    
    private void HandleAnchorCountChanged(bool hasObjects)
    {
        if (askSceneToggle != null)
        {
            askSceneToggle.SetActive(hasObjects);
        }
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
        bool hadAnchors = HasAnchors;
        
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
            
            // If this is the first anchor, fire the event
            if (!hadAnchors && OnAnchorCountChanged != null)
            {
                OnAnchorCountChanged(true);
            }
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
            string oldLabel = anchor.label;
            anchor.label = newLabel;
            Debug.Log($"Anchor updated label to: {newLabel}");
            LogUserStudy($"[SCENE_OBJECT_MANAGER] ANCHOR_LABEL_CHANGED: Object=\"{oldLabel}\", NewLabel=\"{newLabel}\"");

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

        // Ensure any new toggle is initialized in the off state if there's already an active toggle
        SphereToggleScript toggleScript = sphereObj.GetComponent<SphereToggleScript>();
        if (toggleScript != null)
        {
            // Get the current active toggle in the scene
            SphereToggleScript currentActive = SphereToggleScript.CurrentActiveToggle;
            
            // If there's already an active toggle, make sure this new one starts off
            if (currentActive != null)
            {
                // Force the new toggle to initialize in off state
                var toggle = toggleScript.GetComponent<SpatialUIToggle>();
                if (toggle != null && toggle.enabled)
                {
                    // Make sure the toggle starts in the off state
                    if (toggle.isActiveAndEnabled)
                    {
                        // Ensure it's off without triggering events
                        toggleScript.TurnOffToggle();
                    }
                }
                
                Debug.Log($"New anchor created with label '{label}' initialized in non-active state since there's already an active toggle");
            }
        }

        LogUserStudy($"[SCENE_OBJECT_MANAGER] ANCHOR_INITIALIZED: Object=\"{label}\", Position=\"{position}\", Radius={matchingRadius:F3}m");
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
                sphereToggleScript.sceneContextManager = sceneContextManager;
                
                // Make sure to pass our recorder toggle to the new sphere toggle script
                if (recorderToggle != null)
                {
                    sphereToggleScript.recorderToggle = recorderToggle;
                }
                
                // Make sure to pass baseline mode controller reference
                if (baselineModeController != null)
                {
                    sphereToggleScript.baselineModeController = baselineModeController;
                }
            }

            var menuScript = InfoPanel.GetComponentInChildren<MenuScript>();
            if (menuScript != null) sphereToggleScript.menuScript = menuScript;
        }

        if (labelPrefab != null)
        {
            var lblObj = Instantiate(labelPrefab, sphereObj.transform);
            lblObj.name = $"Label_{label}";
            lblObj.transform.localPosition = new Vector3(0f, labelOffset, 0f);
            lblObj.transform.localScale = labelPrefab.transform.localScale / sphereObj.transform.localScale.x;
            lblObj.GetComponent<SpatialUI>().UpdateReferenceScale();
            
            // // Add a LookAt component to make the label face the camera
            // var lookAt = lblObj.AddComponent<LookAtCamera>();
            // lookAt.targetCamera = xrCamera;
            
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

        if (descriptionText != null)
        {
            sphereToggleScript.descriptionText = descriptionText;
            sphereToggleScript.descriptionPanel = descriptionPanel;
            sphereToggleScript.pointingPlane = pointingPlane;
            sphereToggleScript.pointingPlaneText = pointingPlaneText;
        }

        if (handTracking != null)
        {
            sphereToggleScript.handTracking = handTracking;
        }

        return sphereObj;
    }

    public SceneObjectAnchor GetAnchorByGameObject(GameObject obj)
    {
        return anchors.Find(a => a.sphereObj == obj);
    }

    /// <summary>
    /// Clears all anchors from the scene, removing their GameObjects and emptying the anchors list.
    /// </summary>
    /// 

    [ContextMenu("Clear All Anchors")]
    public void ClearAllAnchors()
    {
        bool hadAnchors = HasAnchors;
        int anchorCount = anchors.Count;
        
        // Handle recorder toggle
        if (recorderToggle != null)
        {
            // Apply the same settings as used in SphereToggleScript.UpdateTogglePosition when setting parent to null
            recorderToggle.transform.SetParent(null);
            
            // Update reference scale using SpatialUI component
            var spatialUI = recorderToggle.GetComponent<SpatialUI>();
            if (spatialUI != null) spatialUI.UpdateReferenceScale();
            
            // Reset position and rotation
            recorderToggle.transform.localPosition = Vector3.zero;
            recorderToggle.transform.localRotation = Quaternion.identity;
            
            // Enable LazyFollow if it exists
            var lazyFollow = recorderToggle.GetComponent<LazyFollow>();
            if (lazyFollow != null) lazyFollow.enabled = true;
            
            // Reset object label in the recorder component
            SpeechToTextRecorder recorder = recorderToggle.GetComponent<SpeechToTextRecorder>();
            if (recorder == null && recorderToggle.transform.parent != null) 
                recorder = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
            if (recorder != null) recorder.ResetObjectLabel();
            
            // Handle baseline mode if available
            if (baselineModeController != null && baselineModeController.baselineMode)
            {
                // In baseline mode, set inactive - but we'll need to reactivate it for new spheres
                recorderToggle.SetActive(false);
            }
            else
            {
                // In normal mode, make sure it's active
                recorderToggle.SetActive(true);
            }
            
            // Store this recorder toggle in a static variable so it can be accessed by new spheres
            StoreRecorderToggleReference();
        }
        
        // Handle object tracking toggle if we can find one
        GameObject objectTrackingToggle = GameObject.Find("ObjectTrackingToggle");
        if (objectTrackingToggle != null)
        {
            // Apply the same settings as the recorder toggle
            objectTrackingToggle.transform.SetParent(null);
            
            var spatialUI = objectTrackingToggle.GetComponent<SpatialUI>();
            if (spatialUI != null) spatialUI.UpdateReferenceScale();
            
            objectTrackingToggle.transform.localPosition = Vector3.zero;
            objectTrackingToggle.transform.localRotation = Quaternion.identity;
            
            var lazyFollow = objectTrackingToggle.GetComponent<LazyFollow>();
            if (lazyFollow != null) lazyFollow.enabled = true;
            
            // Handle visibility based on baseline mode
            if (baselineModeController != null && baselineModeController.baselineMode)
            {
                // In baseline mode, always hide the object tracking toggle
                objectTrackingToggle.SetActive(false);
            }
            else
            {
                // In normal mode, make sure it's visible
                objectTrackingToggle.SetActive(true);
            }
            
            // Make sure all sphere toggles have a reference to this toggle
            SphereToggleScript[] allToggles = FindObjectsOfType<SphereToggleScript>();
            foreach (var toggle in allToggles)
            {
                toggle.objectTrackingToggle = objectTrackingToggle;
            }
        }
        
        // Destroy all the sphere GameObjects
        foreach (var anchor in anchors)
        {
            if (anchor.sphereObj != null)
            {
                Destroy(anchor.sphereObj);
            }
        }

        // Clear the anchors list
        anchors.Clear();
        
        // Also clear relationship lines if we have a reference to the manager
        if (relationLineManager != null)
        {
            relationLineManager.ClearAllLines();
        }
        
        // Fire the event if we had anchors before clearing
        if (hadAnchors && OnAnchorCountChanged != null)
        {
            OnAnchorCountChanged(false);
        }
        
        Debug.Log("All anchors have been cleared from the scene");
        LogUserStudy($"[SCENE_OBJECT_MANAGER] ALL_ANCHORS_CLEARED: Count={anchorCount}");
    }
    
    // Helper method to store a reference to the recorder toggle
    private void StoreRecorderToggleReference()
    {
        if (recorderToggle != null)
        {
            // Get all SphereToggleScript components in the scene
            SphereToggleScript[] allToggles = FindObjectsOfType<SphereToggleScript>();
            foreach (var toggle in allToggles)
            {
                // Update the recorder toggle reference
                toggle.recorderToggle = recorderToggle;
            }
        }
    }
    
    // Helper method for creating timestamped user study logs
    private void LogUserStudy(string message)
    {
        if (!enableUserStudyLogging) return;
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
    }
}
