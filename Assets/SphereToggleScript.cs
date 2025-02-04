using UnityEngine;
using UnityEngine.UI; // for Toggle
using TMPro;
using PolySpatial.Template;

public class SphereToggleScript : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The Toggle component on this sphere.")]
    [SerializeField]
    private SpatialUIToggle spatialUIToggle;

    [Tooltip("TextMeshPro label that holds the sphere's 'name' or 'content'.")]
    public TextMeshPro labelUnderSphere;

    [Tooltip("Reference to the scene's menu. We call SetMenuTitle(...) on it.")]
    public MenuScript menuScript;

    public GameObject InfoPanel;

    private void Start()
    {
        // Subscribe to the toggle's onValueChanged event
        if (spatialUIToggle != null)
        {
            spatialUIToggle.m_ToggleChanged.AddListener(OnSphereToggled);
        }
    }

    private void OnSphereToggled(bool isOn)
    {
        if (isOn)
        {
            InfoPanel.SetActive(true);
            // We just toggled ON this sphere: tell the menu to update
            if (labelUnderSphere != null)
            {
                // get the text from labelUnderSphere
                string labelContent = labelUnderSphere.text;
                menuScript.SetMenuTitle(labelContent);
            }
        }
        else
        {
            InfoPanel.SetActive(false);
        }
    }
}
