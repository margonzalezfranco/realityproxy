using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Manages multi-select functionality based on hand middle-finger pinch gestures.
/// Left hand middle-finger pinching = multi-select mode
/// Right hand normal pinching = single-select mode
/// </summary>
public class MultiSelectManager : MonoBehaviour
{
    [Header("Hand Tracking Reference")]
    [Tooltip("Reference to the MyHandTracking component")]
    public MyHandTracking handTracking;
    
    [Header("Multi-Select Settings")]
    [Tooltip("Multi-select mode is active only while left hand is middle-finger pinching")]
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
        
        // Subscribe to middle finger pinch events
        MyHandTracking.OnMiddlePinchStarted += HandleMiddlePinchStarted;
        MyHandTracking.OnMiddlePinchEnded += HandleMiddlePinchEnded;
        
        // Subscribe to regular pinch events for right hand single-select
        MyHandTracking.OnPinchStarted += HandlePinchStarted;
        
        Debug.Log("MultiSelectManager: Initialized and subscribed to middle-finger pinch events");
    }
    
    void OnDestroy()
    {
        // Unsubscribe from events
        MyHandTracking.OnMiddlePinchStarted -= HandleMiddlePinchStarted;
        MyHandTracking.OnMiddlePinchEnded -= HandleMiddlePinchEnded;
        MyHandTracking.OnPinchStarted -= HandlePinchStarted;
    }
    
    void Update()
    {
        bool leftCurrentlyMiddlePinching = handTracking.IsMiddlePinching(true); // true = left hand
        
        // Update multi-select mode based on current left hand middle-finger pinch state
        if (leftCurrentlyMiddlePinching && !SphereToggleScript.IsMultiSelectMode)
        {
            // Left hand started middle-finger pinching - enter multi-select mode
            EnterMultiSelectMode();
        }
        else if (!leftCurrentlyMiddlePinching && SphereToggleScript.IsMultiSelectMode)
        {
            // Left hand stopped middle-finger pinching - exit multi-select mode immediately
            ExitMultiSelectMode();
        }
    }
    
    private void HandleMiddlePinchStarted(bool isLeftHand)
    {
        if (isLeftHand)
        {
            // Left hand middle-finger pinch started - enter multi-select mode
            EnterMultiSelectMode();
            if (debugMode) Debug.Log("Left hand middle-finger pinch started - entered multi-select mode");
        }
    }
    
    private void HandleMiddlePinchEnded(bool isLeftHand)
    {
        if (isLeftHand)
        {
            // Left hand middle-finger pinch ended - exit multi-select mode
            ExitMultiSelectMode();
            if (debugMode) Debug.Log("Left hand middle-finger pinch ended - exited multi-select mode");
        }
    }
    
    private void HandlePinchStarted(bool isLeftHand)
    {
        if (!isLeftHand)
        {
            // Right hand normal pinch started - force single-select mode
            ExitMultiSelectMode();
            if (debugMode) Debug.Log("Right hand pinch started - forced single-select mode");
        }
    }
    
    private void EnterMultiSelectMode()
    {
        if (!SphereToggleScript.IsMultiSelectMode)
        {
            SphereToggleScript.IsMultiSelectMode = true;
            if (debugMode) Debug.Log("Entered multi-select mode (left hand middle-finger pinching)");
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