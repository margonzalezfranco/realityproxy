using UnityEngine;

public class BaselineModeController : MonoBehaviour
{
    public bool baselineMode = false;
    public void ToggleBaselineMode()
    {
        baselineMode = !baselineMode;
    }
}
