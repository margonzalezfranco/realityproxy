using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Manages multi-select functionality based on hand middle-finger pinch gestures.
/// Any hand middle-finger pinching = multi-select mode
/// Any hand index-finger pinching = manual anchor detection (handled separately)
/// </summary>
public class MultiSelectManager : MonoBehaviour
{
    [Header("Hand Tracking Reference")]
    [Tooltip("Reference to the MyHandTracking component")]
    public MyHandTracking handTracking;
    
    [Header("Multi-Select Settings")]
    [Tooltip("Multi-select mode is active while any hand is middle-finger pinching")]
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
        
        // Subscribe to middle finger pinch events for multi-select mode
        MyHandTracking.OnMiddlePinchStarted += HandleMiddlePinchStarted;
        MyHandTracking.OnMiddlePinchEnded += HandleMiddlePinchEnded;
        
        Debug.Log("MultiSelectManager: Initialized and subscribed to middle-finger pinch events");
    }
    
    void OnDestroy()
    {
        // Unsubscribe from events
        MyHandTracking.OnMiddlePinchStarted -= HandleMiddlePinchStarted;
        MyHandTracking.OnMiddlePinchEnded -= HandleMiddlePinchEnded;
    }
    
    void Update()
    {
        bool leftMiddlePinching = handTracking.IsMiddlePinching(true); // Left hand
        bool rightMiddlePinching = handTracking.IsMiddlePinching(false); // Right hand
        bool anyMiddlePinching = leftMiddlePinching || rightMiddlePinching;
        
        // Update multi-select mode based on any hand middle-finger pinch state
        if (anyMiddlePinching && !SphereToggleScript.IsMultiSelectMode)
        {
            // Any hand started middle-finger pinching - enter multi-select mode
            EnterMultiSelectMode();
        }
        else if (!anyMiddlePinching && SphereToggleScript.IsMultiSelectMode)
        {
            // No hand middle-finger pinching - exit multi-select mode immediately
            ExitMultiSelectMode();
        }
    }
    
    private void HandleMiddlePinchStarted(bool isLeftHand)
    {
        // Any hand middle-finger pinch started - enter multi-select mode
        EnterMultiSelectMode();
        if (debugMode) Debug.Log($"{(isLeftHand ? "Left" : "Right")} hand middle-finger pinch started - entered multi-select mode");
    }
    
    private void HandleMiddlePinchEnded(bool isLeftHand)
    {
        // Check if any hand is still middle-finger pinching before exiting
        bool leftStillPinching = handTracking.IsMiddlePinching(true);
        bool rightStillPinching = handTracking.IsMiddlePinching(false);
        
        if (!leftStillPinching && !rightStillPinching)
        {
            // No hand is middle-finger pinching - exit multi-select mode
            ExitMultiSelectMode();
            if (debugMode) Debug.Log($"{(isLeftHand ? "Left" : "Right")} hand middle-finger pinch ended - exited multi-select mode");
        }
        else
        {
            if (debugMode) Debug.Log($"{(isLeftHand ? "Left" : "Right")} hand middle-finger pinch ended - but other hand still pinching");
        }
    }
    
    private void EnterMultiSelectMode()
    {
        if (!SphereToggleScript.IsMultiSelectMode)
        {
            SphereToggleScript.IsMultiSelectMode = true;
            if (debugMode) Debug.Log("Entered multi-select mode (any hand middle-finger pinching)");
        }
    }
    
    private void ExitMultiSelectMode()
    {
        if (SphereToggleScript.IsMultiSelectMode)
        {
            SphereToggleScript.IsMultiSelectMode = false;
            
            // Do NOT clear selections - leave all selected objects as they are
            // Only exit the mode so no new multi-selections can be made
            
            if (debugMode) Debug.Log($"Exited multi-select mode. {SphereToggleScript.SelectedToggles.Count} objects remain selected");
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