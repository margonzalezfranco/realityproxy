using UnityEngine;
using System;

public class BaselineModeController : MonoBehaviour
{
    public bool baselineMode = false;
    
    // Add event for baseline mode changes
    public event Action<bool> OnBaselineModeChanged;
    
    public void ToggleBaselineMode()
    {
        baselineMode = !baselineMode;
        // Trigger the event
        OnBaselineModeChanged?.Invoke(baselineMode);
    }
}
