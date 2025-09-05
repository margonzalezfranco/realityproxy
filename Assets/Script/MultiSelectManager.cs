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
    [Tooltip("Duration to keep multi-select mode active after left pinch ends")]
    public float multiSelectModeDuration = 2.0f;
    
    private float leftPinchEndTime = -1f;
    private bool wasLeftPinching = false;
    
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
        // Check if we should exit multi-select mode
        if (SphereToggleScript.IsMultiSelectMode)
        {
            bool leftCurrentlyPinching = handTracking.IsPinching(true); // true = left hand
            
            // If left hand is not pinching and enough time has passed since it stopped
            if (!leftCurrentlyPinching && leftPinchEndTime > 0 && 
                Time.time - leftPinchEndTime > multiSelectModeDuration)
            {
                ExitMultiSelectMode();
            }
        }
    }
    
    private void HandlePinchStarted(bool isLeftHand)
    {
        if (isLeftHand)
        {
            // Left hand pinch started - enter multi-select mode
            EnterMultiSelectMode();
            wasLeftPinching = true;
        }
        else
        {
            // Right hand pinch started - ensure single-select mode
            ExitMultiSelectMode();
        }
        
        Debug.Log($"Pinch started - {(isLeftHand ? "Left" : "Right")} hand. Multi-select mode: {SphereToggleScript.IsMultiSelectMode}");
    }
    
    private void HandlePinchEnded(bool isLeftHand)
    {
        if (isLeftHand && wasLeftPinching)
        {
            // Left hand pinch ended - mark the time but keep multi-select mode active for a while
            leftPinchEndTime = Time.time;
            wasLeftPinching = false;
            Debug.Log($"Left hand pinch ended. Multi-select mode will remain active for {multiSelectModeDuration} seconds");
        }
    }
    
    private void EnterMultiSelectMode()
    {
        if (!SphereToggleScript.IsMultiSelectMode)
        {
            SphereToggleScript.IsMultiSelectMode = true;
            leftPinchEndTime = -1f; // Reset the timer
            Debug.Log("Entered multi-select mode (left hand pinching)");
        }
    }
    
    private void ExitMultiSelectMode()
    {
        if (SphereToggleScript.IsMultiSelectMode)
        {
            SphereToggleScript.IsMultiSelectMode = false;
            leftPinchEndTime = -1f; // Reset the timer
            
            // Clear all multi-selections and go back to single-select mode
            if (SphereToggleScript.SelectedToggles.Count > 1)
            {
                Debug.Log($"Exiting multi-select mode. Clearing {SphereToggleScript.SelectedToggles.Count} selected objects");
                SphereToggleScript.ClearAllMultiSelections();
            }
            
            Debug.Log("Exited multi-select mode (returning to single-select)");
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