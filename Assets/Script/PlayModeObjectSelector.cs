using UnityEngine;

/// <summary>
/// Simple component to enable mouse click selection of objects in Unity Play Mode.
/// This helps with testing multi-select and comparison functionality without VR hardware.
/// </summary>
public class PlayModeObjectSelector : MonoBehaviour
{
    [Header("Play Mode Testing")]
    [Tooltip("Enable mouse click selection in Unity Play Mode")]
    public bool enableMouseSelection = true;
    
    [Tooltip("Enable debug logging for mouse interactions")]
    public bool enableDebugLog = false;
    
    private SphereToggleScript sphereToggle;
    private bool isInPlayMode;
    
    void Start()
    {
        // Get the SphereToggleScript component
        sphereToggle = GetComponent<SphereToggleScript>();
        
        // Check if we're in Unity Play Mode (not on device)
        isInPlayMode = Application.isPlaying && !Application.isMobilePlatform;
        
        if (!isInPlayMode || sphereToggle == null || !enableMouseSelection)
        {
            // Disable this component if not in Play Mode or missing SphereToggleScript
            enabled = false;
            return;
        }
        
        // Add a collider if one doesn't exist (needed for mouse detection)
        if (GetComponent<Collider>() == null)
        {
            SphereCollider collider = gameObject.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 0.1f; // Reasonable size for clicking
        }
        
        if (enableDebugLog) Debug.Log($"PlayModeObjectSelector enabled for object: {sphereToggle.labelUnderSphere?.text}");
    }
    
    void OnMouseDown()
    {
        if (!isInPlayMode || !enableMouseSelection || sphereToggle == null) return;
        
        // Simulate the toggle being pressed
        if (enableDebugLog) Debug.Log($"Mouse clicked on object: {sphereToggle.labelUnderSphere?.text}");
        
        // Call the same toggle logic that would be triggered by spatial UI
        ToggleObject();
    }
    
    private void ToggleObject()
    {
        try
        {
            // Get the current toggle state
            bool currentState = sphereToggle.isOn;
            
            if (enableDebugLog) Debug.Log($"Toggling object {sphereToggle.labelUnderSphere?.text} from {currentState} to {!currentState}");
            
            // Simulate pressing the spatial UI toggle
            var spatialToggle = sphereToggle.GetComponent<PolySpatial.Template.SpatialUIToggle>();
            if (spatialToggle != null)
            {
                spatialToggle.PressStart();
                spatialToggle.PressEnd();
            }
            else
            {
                // Fallback - directly call toggle methods if spatial toggle isn't available
                if (currentState)
                {
                    sphereToggle.TurnOffToggle();
                }
                else
                {
                    // Activate the toggle - this will trigger the multi-select logic
                    sphereToggle.isOn = true;
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error toggling object in Play Mode: {e.Message}");
        }
    }
    
    // Visual feedback when hovering
    void OnMouseEnter()
    {
        if (!isInPlayMode || !enableMouseSelection) return;
        
        // Add slight visual feedback
        Renderer renderer = GetComponent<Renderer>();
        if (renderer != null)
        {
            // Slightly brighten the material
            foreach (Material mat in renderer.materials)
            {
                if (mat.HasProperty("_Color"))
                {
                    Color originalColor = mat.color;
                    mat.color = new Color(originalColor.r * 1.2f, originalColor.g * 1.2f, originalColor.b * 1.2f, originalColor.a);
                }
            }
        }
    }
    
    void OnMouseExit()
    {
        if (!isInPlayMode || !enableMouseSelection) return;
        
        // Reset visual feedback
        Renderer renderer = GetComponent<Renderer>();
        if (renderer != null)
        {
            // Reset material to original color
            foreach (Material mat in renderer.materials)
            {
                if (mat.HasProperty("_Color"))
                {
                    Color currentColor = mat.color;
                    mat.color = new Color(currentColor.r / 1.2f, currentColor.g / 1.2f, currentColor.b / 1.2f, currentColor.a);
                }
            }
        }
    }
}