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
    
    [Tooltip("Reference to the Object Tracking toggle UI element.")]
    public GameObject objectTrackingToggle;

    [Tooltip("Reference to the Question toggle UI element.")]
    public GameObject questionToggle;

    [Tooltip("Reference to the Relation toggle UI element.")]
    public GameObject relationToggle;

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

        // Find toggles if not assigned in Inspector
        if (recorderToggle == null) recorderToggle = GameObject.Find("RecorderToggle");
        if (objectTrackingToggle == null) objectTrackingToggle = GameObject.Find("ObjectTrackingToggle");
        if (questionToggle == null) questionToggle = GameObject.Find("QuestionToggle");
        if (relationToggle == null) relationToggle = GameObject.Find("RelationToggle");
        if (askSceneToggle == null) askSceneToggle = GameObject.Find("AskSceneToggle"); // Ensure correct name

        // Add warnings if any are still null after attempting to find them
        if (recorderToggle == null) Debug.LogWarning("SceneObjectManager: RecorderToggle not found!");
        if (objectTrackingToggle == null) Debug.LogWarning("SceneObjectManager: ObjectTrackingToggle not found!");
        if (questionToggle == null) Debug.LogWarning("SceneObjectManager: QuestionToggle not found!");
        if (relationToggle == null) Debug.LogWarning("SceneObjectManager: RelationToggle not found!");
        if (askSceneToggle == null) Debug.LogWarning("SceneObjectManager: AskSceneToggle not found!");

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
    public GameObject SpawnSphereWithLabel(Vector3 position, string label)
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

    /// <summary>
    /// Registers a manually created anchor directly with an existing GameObject.
    /// This is used by ManualAnchorRegistration to ensure its transition anchors are properly tracked.
    /// </summary>
    /// <param name="label">The label for the anchor</param>
    /// <param name="position">The world position of the anchor</param>
    /// <param name="existingSphereObj">The existing GameObject representing the anchor</param>
    /// <returns>The created SceneObjectAnchor</returns>
    public SceneObjectAnchor RegisterManualAnchor(string label, Vector3 position, GameObject existingSphereObj)
    {
        bool hadAnchors = HasAnchors;

        // Check if this object already has an associated anchor
        SceneObjectAnchor existing = GetAnchorByGameObject(existingSphereObj);
        if (existing != null)
        {
            // Just update the label if needed
            UpdateAnchorLabel(existing, label);
            return existing;
        }

        // Create a new anchor data object but use the existing GameObject
        SceneObjectAnchor newAnchor = new SceneObjectAnchor
        {
            label = label,
            position = position,
            boundingRadius = matchingRadius,
            userLocked = false,
            sphereObj = existingSphereObj
        };

        // Add to the list
        anchors.Add(newAnchor);

        // If this is the first anchor, fire the event
        if (!hadAnchors && OnAnchorCountChanged != null)
        {
            OnAnchorCountChanged(true);
        }

        LogUserStudy($"[SCENE_OBJECT_MANAGER] MANUAL_ANCHOR_REGISTERED: Object=\"{label}\", Position=\"{position}\", Radius={matchingRadius:F3}m");
        return newAnchor;
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
        Debug.Log("Clear All Anchors operation starting...");
        bool hadAnchors = HasAnchors;
        int anchorCount = anchors.Count;

        // 1. ensure all functional toggles are reset and unparented
        
        // List of toggles to manage using the persistent references
        List<GameObject> functionToggles = new List<GameObject>();
        if (recorderToggle != null) functionToggles.Add(recorderToggle);
        if (objectTrackingToggle != null) functionToggles.Add(objectTrackingToggle);
        if (questionToggle != null) functionToggles.Add(questionToggle);
        if (relationToggle != null) functionToggles.Add(relationToggle);

        // Reset all toggles to known good state BEFORE handling anchors
        foreach (var toggle in functionToggles)
        {
            ResetFunctionToggleState(toggle);
        }

        // 2. check if any are still children of anchors
        List<SceneObjectAnchor> anchorsToProcess = new List<SceneObjectAnchor>(anchors);
        foreach (var anchor in anchorsToProcess)
        {
            if (anchor.sphereObj == null) continue;

            // Check children of each anchor
            List<Transform> childrenToCheck = new List<Transform>();
            foreach (Transform child in anchor.sphereObj.transform)
            {
                childrenToCheck.Add(child);
            }

            foreach (Transform childTransform in childrenToCheck)
            {
                // Check if this is one of our function toggles
                if (functionToggles.Contains(childTransform.gameObject))
                {
                    Debug.LogWarning($"Toggle '{childTransform.name}' is still a child of anchor '{anchor.label}' - unparenting again");
                    ResetFunctionToggleState(childTransform.gameObject);
                }
                else if (childTransform.name.Contains("Toggle") || 
                         childTransform.name.Contains("Question") ||
                         childTransform.name.Contains("Relation") ||
                         childTransform.name.Contains("Recorder") ||
                         childTransform.name.Contains("ObjectTracking"))
                {
                    // This is potentially another toggle we don't have in our list
                    Debug.LogWarning($"Found unknown toggle '{childTransform.name}' under anchor '{anchor.label}' - unparenting");
                    childTransform.SetParent(null);
                }
            }
        }
        
        // 3. clear relationship lines
        if (relationLineManager != null)
        {
            Debug.Log("Clearing all relationship lines");
            relationLineManager.ClearAllLines();
        }

        // 4. destroy all anchors
        Debug.Log($"Destroying {anchors.Count} anchors");
        foreach (var anchor in anchors)
        {
            if (anchor.sphereObj != null)
            {
                Destroy(anchor.sphereObj);
            }
        }
        anchors.Clear();

        // 5. reset state of all toggles one more time
        foreach (var toggle in functionToggles)
        {
            if (toggle == null)
            {
                Debug.LogError("A function toggle reference was unexpectedly null after clearing anchors!");
                continue;
            }

            // Set Active State based on game logic
            bool shouldBeActive = true; // Default
            if (toggle == recorderToggle || toggle == objectTrackingToggle)
            {
                shouldBeActive = (baselineModeController == null || !baselineModeController.baselineMode);
            }
            else if (toggle == askSceneToggle)
            {
                shouldBeActive = false; // Since there are no anchors
            }

            toggle.SetActive(shouldBeActive);
            Debug.Log($"Set {toggle.name} active state to: {shouldBeActive}");
        }

        // Fire the event if we had anchors before clearing
        if (hadAnchors && OnAnchorCountChanged != null)
        {
            OnAnchorCountChanged(false);
        }

        Debug.Log($"Successfully cleared all {anchorCount} anchors from the scene");
        LogUserStudy($"[SCENE_OBJECT_MANAGER] ALL_ANCHORS_CLEARED: Count={anchorCount}");
    }

    // Helper function to reset a function toggle
    private void ResetFunctionToggleState(GameObject toggle)
    {
        if (toggle == null) return;

        Debug.Log($"Resetting function toggle: {toggle.name}");

        // First, force the visual state to inactive if it's a SpatialUIToggle
        SpatialUIToggle spatialUIToggle = toggle.GetComponent<SpatialUIToggle>();
        if (spatialUIToggle != null && spatialUIToggle.m_Active)
        {
            Debug.Log($"Forcing {toggle.name} toggle to inactive state");
            // Use PassiveToggleWithoutInvoke to avoid triggering events
            spatialUIToggle.PassiveToggleWithoutInvokeOff();
        }

        // Reset parent 
        toggle.transform.SetParent(null);

        // Update reference scale
        var spatialUI = toggle.GetComponent<SpatialUI>();
        if (spatialUI != null) spatialUI.UpdateReferenceScale();

        // Reset position and rotation
        toggle.transform.localPosition = Vector3.zero;
        toggle.transform.localRotation = Quaternion.identity;

        // Disable DualTargetLazyFollow if present
        var dualLazyFollow = toggle.GetComponent<DualTargetLazyFollow>();
        if (dualLazyFollow != null)
        {
            Debug.Log($"Disabling DualTargetLazyFollow on {toggle.name}");
            dualLazyFollow.enabled = false;
            
            // For more thorough cleanup, destroy it completely
            Destroy(dualLazyFollow);
        }

        // Enable standard LazyFollow for default floating behavior
        var lazyFollow = toggle.GetComponent<LazyFollow>();
        if (lazyFollow != null)
        {
            Debug.Log($"Enabling LazyFollow on {toggle.name}");
            
            // Reset LazyFollow parameters to defaults
            lazyFollow.enabled = false; // Disable first
            
            // Configure optimal parameters before re-enabling
            lazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;
            lazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAt;
            
            if (Camera.main != null)
            {
                lazyFollow.target = Camera.main.transform;
                lazyFollow.snapOnEnable = true; // Force a snap to target position
            }
            
            lazyFollow.enabled = true; // Re-enable
        }

        // Specific resets based on toggle type
        if (toggle == recorderToggle)
        {
            // Reset recorder
            SpeechToTextRecorder recorder = toggle.GetComponent<SpeechToTextRecorder>();
            if (recorder == null && toggle.transform.parent != null) 
                recorder = toggle.transform.parent.GetComponent<SpeechToTextRecorder>();
                
            if (recorder != null)
            {
                Debug.Log("Resetting recorder object label");
                recorder.ResetObjectLabel();
                
                // Ensure recording is stopped if active
                var isRecordingField = recorder.GetType().GetField("isRecording", 
                    System.Reflection.BindingFlags.Instance | 
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                    
                if (isRecordingField != null)
                {
                    bool isRecording = (bool)isRecordingField.GetValue(recorder);
                    if (isRecording)
                    {
                        Debug.Log("Stopping active recording during reset");
                        // Can't directly access internal methods, so use toggle
                        if (spatialUIToggle != null && spatialUIToggle.enableInteraction)
                        {
                            spatialUIToggle.PressStart();
                            spatialUIToggle.PressEnd();
                        }
                    }
                }
            }
        }
        else if (toggle == relationToggle)
        {
            // Reset relation toggle
            var controller = toggle.GetComponent<RelationToggleController>();
            if (controller != null)
            {
                Debug.Log("Resetting relation toggle controller");
                
                // Clear references
                controller.ownerSphereToggle = null;
                
                // Ensure relationships are cleared
                if (controller.relationshipLineManager != null)
                {
                    controller.relationshipLineManager.ClearAllLines();
                }
                
                // Reset toggle state
                if (controller.toggle != null && controller.toggle.m_Active)
                {
                    controller.ToggleRelationshipLines(false); // Logical off
                    controller.toggle.PassiveToggleWithoutInvokeOff(); // Visual off
                }
            }
        }
        else if (toggle == questionToggle)
        {
            // Reset question toggle
            var controller = toggle.GetComponent<InfoPanelToggleController>();
            if (controller != null)
            {
                Debug.Log("Resetting question toggle controller");
                
                // Clear references
                controller.sphereToggleScript = null;
                
                // Hide panels
                if (controller.infoPanel != null && controller.infoPanel.activeSelf)
                {
                    controller.SetInfoPanelVisibility(false);
                }
                
                if (controller.answerPanel != null && controller.answerPanel.activeSelf)
                {
                    controller.answerPanel.SetActive(false);
                }
                
                // Reset toggle state
                if (controller.toggle != null && controller.toggle.m_Active)
                {
                    controller.toggle.PassiveToggleWithoutInvokeOff();
                }
            }
        }
        else if (toggle == objectTrackingToggle)
        {
            // Reset object tracking toggle if needed
            // This is mostly handled by the general reset above
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
