using UnityEngine;
using System.Collections;

/// <summary>
/// SurfaceScanOCR: Listens for surface drawing events and triggers OCR scanning
/// This class connects the DragSurface system with the CloudVisionOCRUnified system
/// to perform OCR scanning when a user completes drawing the length of a surface.
/// </summary>
public class SurfaceScanOCR : MonoBehaviour
{
    [Header("Dependencies")]
    [Tooltip("Reference to the CloudVisionOCR component")]
    public CloudVisionOCRUnified ocrComponent;

    [Header("OCR Settings")]
    [Tooltip("Optional delay before triggering OCR (seconds)")]
    public float ocrDelay = 0f;

    [Tooltip("Whether to automatically trigger OCR when surface length is completed")]
    public bool autoTriggerOCR = true;

    // Start method to subscribe to events
    private void Start()
    {
        // Find OCR component if not assigned
        if (ocrComponent == null)
        {
            ocrComponent = FindAnyObjectByType<CloudVisionOCRUnified>();
            if (ocrComponent == null)
            {
                Debug.LogError("No CloudVisionOCRUnified component found in the scene!");
                enabled = false;
                return;
            }
        }

        // Subscribe to DragSurface's length completed event
        DragSurface.OnSurfaceLengthCompleted += HandleSurfaceLengthCompleted;
    }

    // Unsubscribe from events when this object is destroyed
    private void OnDestroy()
    {
        DragSurface.OnSurfaceLengthCompleted -= HandleSurfaceLengthCompleted;
    }

    /// <summary>
    /// Handles the event when a surface length is completed
    /// </summary>
    /// <param name="startPoint">Starting point of the surface</param>
    /// <param name="endPoint">Ending point of the surface</param>
    private void HandleSurfaceLengthCompleted(Vector3 startPoint, Vector3 endPoint)
    {
        if (!autoTriggerOCR)
            return;

        Debug.Log($"Surface length completed from {startPoint} to {endPoint} - Preparing to scan with OCR");
        
        // Start OCR scanning with optional delay
        if (ocrDelay > 0)
        {
            StartCoroutine(DelayedOCRScan());
        }
        else
        {
            PerformOCRScan();
        }
    }

    /// <summary>
    /// Coroutine to delay OCR scanning
    /// </summary>
    private IEnumerator DelayedOCRScan()
    {
        yield return new WaitForSeconds(ocrDelay);
        PerformOCRScan();
    }

    /// <summary>
    /// Performs the actual OCR scan
    /// </summary>
    private void PerformOCRScan()
    {
        if (ocrComponent != null)
        {
            Debug.Log("Starting OCR scan...");
            ocrComponent.StartOCR();
        }
        else
        {
            Debug.LogError("Cannot perform OCR scan: OCR component is missing");
        }
    }

    /// <summary>
    /// Public method to manually trigger OCR scan
    /// </summary>
    [ContextMenu("Trigger OCR Scan")]
    public void TriggerOCRScan()
    {
        PerformOCRScan();
    }
} 