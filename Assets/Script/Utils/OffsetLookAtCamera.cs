using UnityEngine;
using System.Collections;

/// <summary>
/// A utility component that makes a UI element properly face the camera while considering its offset position.
/// This is particularly useful for UI elements that are positioned with an offset from their parent object
/// but still need to maintain proper camera-facing orientation.
/// </summary>
public class OffsetLookAtCamera : MonoBehaviour
{
    [Header("Look At Settings")]
    [Tooltip("The camera transform to look at. If null, will use Camera.main")]
    public Transform targetCamera;

    [Tooltip("How smoothly to interpolate the rotation (higher = smoother but slower)")]
    [Range(1f, 20f)]
    public float rotationSmoothness = 10f;

    [Tooltip("Whether to start the look-at behavior automatically on enable")]
    public bool startOnEnable = false;

    private bool isActive = false;
    private Coroutine updateRoutine;
    private Quaternion targetRotation;

    /// <summary>
    /// Helper method to easily attach this component to any transform
    /// </summary>
    /// <param name="transform">The transform to attach the component to</param>
    /// <param name="cameraTransform">Optional specific camera transform to look at</param>
    /// <returns>The attached OffsetLookAtCamera component</returns>
    public static OffsetLookAtCamera AttachToTransform(Transform transform, Transform cameraTransform = null)
    {
        var lookAtComponent = transform.gameObject.AddComponent<OffsetLookAtCamera>();
        lookAtComponent.targetCamera = cameraTransform ?? Camera.main?.transform;
        return lookAtComponent;
    }

    private void OnEnable()
    {
        if (startOnEnable)
        {
            StartLookAt();
        }
    }

    private void OnDisable()
    {
        StopLookAt();
    }

    /// <summary>
    /// Starts the look-at-camera behavior
    /// </summary>
    public void StartLookAt()
    {
        if (!isActive && (targetCamera != null || Camera.main != null))
        {
            isActive = true;
            if (updateRoutine != null)
            {
                StopCoroutine(updateRoutine);
            }
            updateRoutine = StartCoroutine(UpdateRotation());
            
            // Initial rotation
            UpdateLookAtRotation();
        }
    }

    /// <summary>
    /// Stops the look-at-camera behavior
    /// </summary>
    public void StopLookAt()
    {
        isActive = false;
        if (updateRoutine != null)
        {
            StopCoroutine(updateRoutine);
            updateRoutine = null;
        }
    }

    private void UpdateLookAtRotation()
    {
        Transform currentCamera = targetCamera ?? Camera.main?.transform;
        if (currentCamera != null)
        {
            Vector3 worldPos = transform.position;
            Vector3 cameraPos = currentCamera.position;
            
            Vector3 directionToCamera = cameraPos - worldPos;
            targetRotation = Quaternion.LookRotation(-directionToCamera);
            
            // Smooth rotation
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRotation,
                Time.deltaTime * rotationSmoothness
            );
        }
    }

    private IEnumerator UpdateRotation()
    {
        while (isActive)
        {
            UpdateLookAtRotation();
            yield return null;
        }
    }

    private void OnDestroy()
    {
        StopLookAt();
    }

    /// <summary>
    /// Sets a new camera target and optionally restarts the look-at behavior
    /// </summary>
    /// <param name="newTarget">The new camera transform to look at</param>
    /// <param name="restart">Whether to restart the look-at behavior if it's already active</param>
    public void SetNewTarget(Transform newTarget, bool restart = true)
    {
        targetCamera = newTarget;
        if (restart && isActive)
        {
            StopLookAt();
            StartLookAt();
        }
    }
} 