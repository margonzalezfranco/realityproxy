using UnityEngine;
using UnityEngine.UI;

public class FOVAdjuster : MonoBehaviour
{
    [Header("Camera to Adjust")]
    public Camera xrCamera; 

    [Header("Slider Reference")]
    public Slider fovSlider;

    private void Start()
    {
        if (fovSlider != null)
        {
            // Initialize the slider value to match the camera’s current FOV
            fovSlider.value = xrCamera.fieldOfView;

            // Add a listener so each time the slider changes, we update the camera FOV
            fovSlider.onValueChanged.AddListener(OnFovSliderChanged);
        }
    }

    private void OnFovSliderChanged(float newValue)
    {
        if (xrCamera != null)
        {
            xrCamera.fieldOfView = newValue;
        }
    }
}
