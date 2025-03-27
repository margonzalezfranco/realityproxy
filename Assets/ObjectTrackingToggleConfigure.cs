using UnityEngine;
using PolySpatial.Template;

public class ObjectTrackingToggleConfigure : MonoBehaviour
{
    private SpatialUIToggle spatialUIToggle;
    private HandGrabTrigger leftHandTrigger;
    private HandGrabTrigger rightHandTrigger;
    private bool handTriggersFound = false;

    void Start()
    {
        spatialUIToggle = GetComponent<SpatialUIToggle>();
        if (spatialUIToggle != null) 
            spatialUIToggle.m_ToggleChanged.AddListener(OnToggleChanged);
    }

    void Update()
    {
        if (!handTriggersFound)
        {
            // Find both hand triggers
            var handTriggers = FindObjectsOfType<HandGrabTrigger>();
            foreach (var trigger in handTriggers)
            {
                if (trigger.handType == "left")
                    leftHandTrigger = trigger;
                else if (trigger.handType == "right")
                    rightHandTrigger = trigger;
            }

            if (leftHandTrigger != null && rightHandTrigger != null)
            {
                handTriggersFound = true;
                Debug.Log("Found both left and right hand triggers");
            }
        }
    }

    void OnToggleChanged(bool isActive)
    {
        if (!handTriggersFound)
        {
            Debug.LogWarning("Hand triggers not found yet, cannot process toggle change");
            return;
        }

        if (isActive)
        {
            // Check if onlyAllowLeftHandGrab is enabled
            bool onlyAllowLeftHandGrab = leftHandTrigger.grabbingDetector != null && 
                                       leftHandTrigger.grabbingDetector.onlyAllowLeftHandGrab;

            // Allow auto-grabbing again on both hands
            leftHandTrigger.AllowAutoGrab();
            rightHandTrigger.AllowAutoGrab();

            if (onlyAllowLeftHandGrab)
            {
                Debug.Log("Only allow left hand grab, grabbing left hand");
                leftHandTrigger.ManualGrabAnchor();
            }
            else
            {
                // Use both hands if onlyAllowLeftHandGrab is disabled
                leftHandTrigger.ManualGrabAnchor();
                rightHandTrigger.ManualGrabAnchor();
            }
        }
        else
        {
            Debug.Log("ObjectTrackingToggleConfigure: Toggle is inactive, releasing both hands");
            leftHandTrigger.ReleaseAnchor(true);
            rightHandTrigger.ReleaseAnchor(true);
        }
    }

    void OnDestroy()
    {
        if (spatialUIToggle != null) spatialUIToggle.m_ToggleChanged.RemoveListener(OnToggleChanged);
    }
}
