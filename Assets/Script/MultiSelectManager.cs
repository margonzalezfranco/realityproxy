using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Manages multi-select functionality based on hand pinch gestures.
/// Left hand pinching = multi-select mode
/// Right hand pinching = single-select mode
/// </summary>
public class MultiSelectManager : MonoBehaviour
{
    [Header("Hand Tracking Reference")]
    [Tooltip("Reference to the MyHandTracking component")]
    public MyHandTracking handTracking;
    
    [Header("Multi-Select Settings")]
    [Tooltip("Multi-select mode is active only while left hand is pinching")]
    public bool debugMode = false;
    
    void Start()
    {
        if (handTracking == null)
        {
            handTracking = FindObjectOfType<MyHandTracking>();
        }
        
        if (handTracking == null)
        {
            Debug.LogError("MultiSelectManager: Could not find MyHandTracking component!");
            enabled = false;
            return;
        }
        
        // Subscribe to pinch events
        MyHandTracking.OnPinchStarted += HandlePinchStarted;
        MyHandTracking.OnPinchEnded += HandlePinchEnded;
        
        Debug.Log("MultiSelectManager: Initialized and subscribed to pinch events");
    }
    
    void OnDestroy()
    {
        // Unsubscribe from events
        MyHandTracking.OnPinchStarted -= HandlePinchStarted;
        MyHandTracking.OnPinchEnded -= HandlePinchEnded;
    }
    
    void Update()
    {
        bool leftCurrentlyPinching = handTracking.IsPinching(true); // true = left hand
        
        // Update multi-select mode based on current left hand pinch state
        if (leftCurrentlyPinching && !SphereToggleScript.IsMultiSelectMode)
        {
            // Left hand started pinching - enter multi-select mode
            EnterMultiSelectMode();
        }
        else if (!leftCurrentlyPinching && SphereToggleScript.IsMultiSelectMode)
        {
            // Left hand stopped pinching - exit multi-select mode immediately
            ExitMultiSelectMode();
        }
    }
    
    private void HandlePinchStarted(bool isLeftHand)
    {
        if (!isLeftHand)
        {
            // Right hand pinch started - force single-select mode
            ExitMultiSelectMode();
            Debug.Log("Right hand pinch started - forced single-select mode");
        }
    }
    
    private void HandlePinchEnded(bool isLeftHand)
    {
        // We handle mode changes in Update() based on current pinch state
        // This is just for logging purposes
        if (isLeftHand)
        {
            Debug.Log("Left hand pinch ended - multi-select mode will exit");
        }
    }
    
    private void EnterMultiSelectMode()
    {
        if (!SphereToggleScript.IsMultiSelectMode)
        {
            SphereToggleScript.IsMultiSelectMode = true;
            if (debugMode) Debug.Log("Entered multi-select mode (left hand pinching)");
        }
    }
    
    private void ExitMultiSelectMode()
    {
        if (SphereToggleScript.IsMultiSelectMode)
        {
            SphereToggleScript.IsMultiSelectMode = false;
            
            // Clear all multi-selections and go back to single-select mode
            if (SphereToggleScript.SelectedToggles.Count > 1)
            {
                if (debugMode) Debug.Log($"Exiting multi-select mode. Clearing {SphereToggleScript.SelectedToggles.Count} selected objects");
                SphereToggleScript.ClearAllMultiSelections();
            }
            
            if (debugMode) Debug.Log("Exited multi-select mode (returning to single-select)");
        }
    }
    
    // Public method to manually exit multi-select mode
    public void ForceExitMultiSelectMode()
    {
        ExitMultiSelectMode();
    }
    
    // Public method to check current mode
    public bool IsInMultiSelectMode()
    {
        return SphereToggleScript.IsMultiSelectMode;
    }
    
    // Public method to get selected objects count
    public int GetSelectedObjectsCount()
    {
        return SphereToggleScript.SelectedToggles.Count;
    }
    
    // Public method to get selected object names
    public List<string> GetSelectedObjectNames()
    {
        List<string> names = new List<string>();
        foreach (var toggle in SphereToggleScript.SelectedToggles)
        {
            if (toggle != null && toggle.labelUnderSphere != null)
            {
                names.Add(toggle.labelUnderSphere.text);
            }
        }
        return names;
    }
}