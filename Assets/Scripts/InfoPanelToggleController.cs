using UnityEngine;
using TMPro;
using PolySpatial.Template;

public class InfoPanelToggleController : MonoBehaviour
{
    [Tooltip("Reference to the InfoPanel that will be toggled")]
    public GameObject infoPanel;

    [Tooltip("Reference to the answer panel that should be hidden when info panel is hidden")]
    public GameObject answerPanel;

    [Tooltip("Reference to the relation toggle that should be deactivated when this is activated")]
    public GameObject relationToggle;

    [Tooltip("Reference to the recorder toggle that should be deactivated when this is activated")]
    public GameObject recorderToggle;
    
    public SpatialUIToggle toggle;

    [Tooltip("Reference to the owner SphereToggleScript for logging and access to other components")]
    public SphereToggleScript sphereToggleScript;

    // Flag to prevent toggle loops when handling mutual exclusivity
    private bool isHandlingFunctionToggleExclusivity = false;

    // Public property to check if owner is active
    public bool OwnerIsActive => sphereToggleScript != null && sphereToggleScript.isOn;

    private void Awake()
    {
        // Find components if not assigned
        if (infoPanel == null)
        {
            // Try to find InfoPanel in the scene via tag or name
            infoPanel = GameObject.Find("InfoPanel");
        }
        
        if (answerPanel == null)
        {
            // Try to find AnswerPanel in the scene
            answerPanel = GameObject.Find("AnswerPanel");
        }
        
        // Set owner based on parent if not already set
        if (sphereToggleScript == null && transform.parent != null)
        {
            sphereToggleScript = transform.parent.GetComponent<SphereToggleScript>();
        }
        
        // If still null, try to find the active SphereToggleScript
        if (sphereToggleScript == null)
        {
            sphereToggleScript = SphereToggleScript.CurrentActiveToggle;
        }
    }

    private void Start()
    {
        // Get the SpatialUIToggle component on this GameObject
        if (toggle == null)
        {
            toggle = GetComponent<SpatialUIToggle>();
        }
        
        if (toggle != null)
        {
            // Clear any existing listeners to avoid duplicates
            toggle.m_ToggleChanged.RemoveAllListeners();
            
            // Add listener to control the InfoPanel
            // The listener should only respond when this specific toggle is changed
            toggle.m_ToggleChanged.AddListener(SetInfoPanelVisibility);
            
            // Initialize the toggle state to match the InfoPanel if it exists
            if (infoPanel != null)
            {
                bool isPanelActive = infoPanel.activeSelf;
                if (toggle.m_Active != isPanelActive)
                {
                    // Update toggle state without invoking events
                    if (isPanelActive)
                        toggle.PassiveToggleWithoutInvokeOn();
                    else
                        toggle.PassiveToggleWithoutInvokeOff();
                    
                    Debug.Log($"Initialized InfoPanelToggleController toggle state to match InfoPanel: {isPanelActive}");
                }
            }
        }
    }
    
    private void OnTransformParentChanged()
    {
        if (transform.parent != null)
        {
            // new parent detected, set this toggle to the new owner
            SphereToggleScript newOwner = transform.parent.GetComponent<SphereToggleScript>();
            if (newOwner != null && newOwner != sphereToggleScript)
            {
                UpdateOwner(newOwner);
            }
        }
        else
        {
            // if the questionToggle is still active when the parent is removed, remember to turn it off
            if (toggle.m_Active)
            {
                toggle.UpdateReferenceScale();
                toggle.PressStart();
                toggle.PressEnd();
            }
        }
    }
    
    public void UpdateOwner(SphereToggleScript newOwner)
    {
        if (newOwner == sphereToggleScript) return;
        
        // Update references from old owner to new owner
        sphereToggleScript = newOwner;
        
        // Update panel references from the new owner
        if (sphereToggleScript != null)
        {
            infoPanel = sphereToggleScript.InfoPanel;
            answerPanel = sphereToggleScript.answerPanel;
            relationToggle = sphereToggleScript.relationToggle;
            recorderToggle = sphereToggleScript.recorderToggle;
            
            Debug.Log($"UpdateOwner: Updated references from new owner {(sphereToggleScript.labelUnderSphere ? sphereToggleScript.labelUnderSphere.text : "unknown")}");
        }
    }

    public void SetInfoPanelVisibility(bool isVisible)
    {
        if (infoPanel != null)
        {
            bool stateActuallyChanged = infoPanel.activeSelf != isVisible;
            infoPanel.SetActive(isVisible);
            
            // If hiding the panel, also hide the answer panel
            if (!isVisible && answerPanel != null)
            {
                answerPanel.SetActive(false);
            }

            if (isVisible && stateActuallyChanged) // Panel is being turned ON
            {
                if (isHandlingFunctionToggleExclusivity) return;
                isHandlingFunctionToggleExclusivity = true;

                // Log for user study if we have access to sphereToggleScript
                if (sphereToggleScript != null && sphereToggleScript.labelUnderSphere != null)
                {
                    LogUserStudy($"[OBJECT] INFO_PANEL_VISIBILITY: Object=\"{sphereToggleScript.labelUnderSphere.text}\", Visible={isVisible}");
                }

                // Deactivate Relation Toggle if it's active
                if (relationToggle != null)
                {
                    SpatialUIToggle rt = relationToggle.GetComponent<SpatialUIToggle>();
                    if (rt != null && IsRelationToggleActive(rt))
                    {
                        Debug.Log("Simulating relationToggle press to turn it off");
                        rt.PressStart(); // This will trigger ToggleRelationshipLines(false) through the controller
                        rt.PressEnd();
                    }
                }

                // Deactivate Recorder Toggle if it's active
                if (IsRecorderOn() && recorderToggle != null)
                {
                    SpatialUIToggle recT = recorderToggle.GetComponent<SpatialUIToggle>();
                    if (recT != null)
                    {
                        Debug.Log("Simulating recorderToggle press to turn it off");
                        recT.PressStart(); // This will trigger OnRecorderFunctionToggleChanged(false)
                        recT.PressEnd();
                    }
                }
                isHandlingFunctionToggleExclusivity = false;
            }
            else if (!isVisible && stateActuallyChanged) // Panel is being turned OFF
            {
                if (sphereToggleScript != null && sphereToggleScript.labelUnderSphere != null)
                {
                    LogUserStudy($"[OBJECT] INFO_PANEL_VISIBILITY: Object=\"{sphereToggleScript.labelUnderSphere.text}\", Visible={isVisible}");
                }
            }
        }
    }

    // Helper method to check if relation toggle is active
    private bool IsRelationToggleActive(SpatialUIToggle toggle)
    {
        if (toggle == null) return false;
        
        // Try to check the toggle state through the controller first,
        // as it may maintain its own state tracking
        var controller = toggle.GetComponent<RelationToggleController>();
        if (controller != null)
        {
            // RelationToggleController tracks active state in relationLinesAreActive
            // Try to access this using reflection as it's a private field
            var activeField = controller.GetType().GetField("relationLinesAreActive", 
                System.Reflection.BindingFlags.Instance | 
                System.Reflection.BindingFlags.NonPublic);
                
            if (activeField != null)
            {
                try
                {
                    return (bool)activeField.GetValue(controller);
                }
                catch (System.Exception)
                {
                    // Fallback to the direct toggle state
                    return toggle.m_Active;
                }
            }
        }
        
        // Fallback to the direct toggle state
        return toggle.m_Active;
    }

    // Helper method to check if recorder is on
    private bool IsRecorderOn()
    {
        if (recorderToggle == null) return false;
        
        SpeechToTextRecorder recorderComponent = recorderToggle.GetComponent<SpeechToTextRecorder>();
        if (recorderComponent == null && recorderToggle.transform.parent != null)
        {
            recorderComponent = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
        }
        
        if (recorderComponent != null)
        {
            var isRecordingField = recorderComponent.GetType().GetField("isRecording",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
                
            if (isRecordingField != null)
            {
                try { // Add try-catch for safety if field access fails
                    return (bool)isRecordingField.GetValue(recorderComponent);
                } catch (System.Exception ex) {
                    Debug.LogError($"Error accessing isRecording field: {ex.Message}");
                    return false;
                }
            }
        }
        return false; // Default if cannot determine
    }

    // Helper method for user study logging
    private void LogUserStudy(string message)
    {
        if (sphereToggleScript == null) return;
        
        // Use reflection to access the private enableUserStudyLogging field
        var loggingField = sphereToggleScript.GetType().GetField("enableUserStudyLogging", 
            System.Reflection.BindingFlags.Instance | 
            System.Reflection.BindingFlags.NonPublic);
            
        bool enableLogging = true;
        if (loggingField != null)
        {
            try
            {
                enableLogging = (bool)loggingField.GetValue(sphereToggleScript);
            }
            catch
            {
                enableLogging = true; // Default to enabling logging if field access fails
            }
        }
        
        if (!enableLogging) return;
        
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
    }
} 