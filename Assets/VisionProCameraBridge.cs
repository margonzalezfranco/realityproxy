using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class VisionProCameraBridge : MonoBehaviour
{
    // ---------- Vision Pro Native Plugin (for real device) ----------
    [DllImport("__Internal")]
    private static extern void startCapture();

    [DllImport("__Internal")]
    private static extern void stopCapture();

    [DllImport("__Internal")]
    private static extern IntPtr getTexturePointer();

    // (NEW) Retrieve actual chosen resolution from Swift
#if UNITY_VISIONOS && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern int getCameraChosenWidth();

    [DllImport("__Internal")]
    private static extern int getCameraChosenHeight();
#endif

    private bool hasAcquiredTexture = false;
    private Texture2D nativeTexture;
    private IntPtr nativeTexPtr = IntPtr.Zero;

    // ---------- Webcam fallback (for Editor) ----------
    [Header("Webcam (Editor Fallback)")]
    [Tooltip("If blank, uses the default available webcam.")]
    public string webCamDeviceName = "";
    private WebCamTexture webCam;
    private Texture2D webcamFrameTex;

    // ---------- Shared RenderTexture + Materials ----------
    [Header("Shared Rendering")]
    [SerializeField] private RenderTexture renderTex;
    [SerializeField] private Material planeMaterial;   // material used by a plane in the scene
    [SerializeField] private Material blitMaterial;    // optional: flipping or other effect

    [Header("Camera / Texture Dimensions")]
    [SerializeField] private int textureWidth = 1920;
    [SerializeField] private int textureHeight = 1080;

    private void Start()
    {
#if UNITY_EDITOR || !UNITY_VISIONOS
        // -----------------------------------------
        // Editor or Non-visionOS: Start Webcam fallback
        // -----------------------------------------
        // Keep default 1920x1080 or whatever is in the inspector
#else
        // -----------------------------------------
        // visionOS device: dynamically get actual camera size from Swift
        // -----------------------------------------
        textureWidth = getCameraChosenWidth();
        textureHeight = getCameraChosenHeight();
        Debug.Log($"[VisionProCameraBridge] Dynamically chosen resolution: {textureWidth}x{textureHeight}");
#endif

        // Create a RenderTexture if none is assigned
        if (renderTex == null)
        {
            renderTex = new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.ARGB32);
            renderTex.Create();
        }

        // Assign the RenderTexture to our plane material (so it can be seen in the scene)
        if (planeMaterial != null)
        {
            planeMaterial.mainTexture = renderTex;
        }

#if UNITY_EDITOR || !UNITY_VISIONOS
        // Editor or Non-visionOS: Start Webcam fallback
        InitWebCam(webCamDeviceName);

#else
        // visionOS: Start the native camera
        startCapture();
#endif
    }

    private void Update()
    {
#if UNITY_EDITOR || !UNITY_VISIONOS
        // Update: read from the webcam and blit to renderTex
        UpdateWebCamRender();
#else
        // Update: read from native camera texture pointer
        if (!hasAcquiredTexture)
        {
            TryAcquireNativeTexture();
        }
        else
        {
            UpdateRenderTexture();
        }
#endif
    }

    private void OnDestroy()
    {
#if UNITY_EDITOR || !UNITY_VISIONOS
        // Stop the webcam if we have one
        if (webCam != null)
        {
            webCam.Stop();
            webCam = null;
        }
#else
        // Stop the Vision Pro capture
        stopCapture();
#endif
    }

    // =============================================================================
    // SECTION A: Editor/Webcam fallback
    // =============================================================================

#if UNITY_EDITOR || !UNITY_VISIONOS
    private void InitWebCam(string deviceName)
    {
        if (!string.IsNullOrEmpty(deviceName))
        {
            webCam = new WebCamTexture(deviceName, textureWidth, textureHeight);
        }
        else
        {
            webCam = new WebCamTexture(textureWidth, textureHeight);
        }
        webCam.Play();

        // Optionally create a temporary texture to store each frame
        if (webcamFrameTex == null)
        {
            webcamFrameTex = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false);
        }
    }

    private void UpdateWebCamRender()
    {
        if (webCam == null) return;
        if (webCam.width < 16 || webCam.height < 16) return; // webcam not ready

        // Ensure webcamFrameTex matches webcam dimensions
        if (webcamFrameTex == null || webcamFrameTex.width != webCam.width || webcamFrameTex.height != webCam.height)
        {
            if (webcamFrameTex != null) Destroy(webcamFrameTex);
            webcamFrameTex = new Texture2D(webCam.width, webCam.height, TextureFormat.RGBA32, false);
        }

        // Copy pixels from WebCamTexture to a CPU Texture2D
        webcamFrameTex.SetPixels(webCam.GetPixels());
        webcamFrameTex.Apply();

        Graphics.Blit(webcamFrameTex, renderTex);
    }
#endif

    // =============================================================================
    // SECTION B: Vision Pro native camera approach
    // =============================================================================

#if UNITY_VISIONOS && !UNITY_EDITOR

    private void TryAcquireNativeTexture()
    {
        IntPtr ptr = getTexturePointer();
        if (ptr == IntPtr.Zero) return; // Not ready yet

        nativeTexPtr = ptr;

        // Create an ExternalTexture referencing the MTLTexture
        nativeTexture = Texture2D.CreateExternalTexture(
            textureWidth,
            textureHeight,
            TextureFormat.BGRA32,
            false,
            false,
            nativeTexPtr
        );

        nativeTexture.UpdateExternalTexture(nativeTexPtr);
        hasAcquiredTexture = true;
    }

    private void UpdateRenderTexture()
    {
        if (nativeTexture == null) return;

        // Blit to the shared RenderTexture
        if (blitMaterial == null)
        {
            Graphics.Blit(nativeTexture, renderTex);
        }
        else
        {
            Graphics.Blit(nativeTexture, renderTex, blitMaterial);
        }

        // For PolySpatial, you might need MarkDirty for updates
        Unity.PolySpatial.PolySpatialObjectUtils.MarkDirty(renderTex);
    }

#endif
}
