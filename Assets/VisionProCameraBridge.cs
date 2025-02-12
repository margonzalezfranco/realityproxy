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

    // --- NEW: mask pointer from Swift
    [DllImport("__Internal")]
    private static extern IntPtr getMaskTexturePointer();

#if UNITY_VISIONOS && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern int getCameraChosenWidth();

    [DllImport("__Internal")]
    private static extern int getCameraChosenHeight();
#endif

    // Color feed
    private bool hasAcquiredTexture = false;
    private Texture2D nativeTexture;
    private IntPtr nativeTexPtr = IntPtr.Zero;

    // Mask feed (NEW)
    private bool hasAcquiredMask = false;
    private Texture2D nativeMaskTexture;
    private IntPtr nativeMaskPtr = IntPtr.Zero;

    // ---------- Webcam fallback (for Editor) ----------
    [Header("Webcam (Editor Fallback)")]
    [Tooltip("If blank, uses the default available webcam.")]
    public string webCamDeviceName = "";
    private WebCamTexture webCam;
    private Texture2D webcamFrameTex;

    // ---------- Shared RenderTexture + Materials ----------
    [Header("Shared Rendering")]
    [SerializeField] private RenderTexture renderTex;       // color feed
    [SerializeField] private Material planeMaterial;         // shows color feed
    [SerializeField] private Material blitMaterial;          // optional for flipping color feed

    // ---------- NEW for Mask -----------
    [Header("Mask Rendering")]
    [SerializeField] private RenderTexture maskRenderTex;   // optional RT for the mask
    [SerializeField] private Material maskPlaneMaterial;     // material used by a plane or sphere for the mask

    [Header("Camera / Texture Dimensions")]
    [SerializeField] private int textureWidth = 1920;
    [SerializeField] private int textureHeight = 1080;

    private void Start()
    {
#if UNITY_EDITOR || !UNITY_VISIONOS
        // Editor or Non-visionOS: Start Webcam fallback
#else
        // On Vision Pro, dynamically get actual camera size from Swift
        textureWidth = getCameraChosenWidth();
        textureHeight = getCameraChosenHeight();
        Debug.Log($"[VisionProCameraBridge] Dynamic resolution: {textureWidth}x{textureHeight}");
#endif

        // Create RenderTexture for color feed if none is assigned
        if (renderTex == null)
        {
            renderTex = new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.ARGB32);
            renderTex.Create();
        }
        // assign color feed RT to plane material
        if (planeMaterial != null)
        {
            planeMaterial.mainTexture = renderTex;
        }

        // (Optional) create maskRenderTex if not assigned
        if (maskRenderTex == null)
        {
            maskRenderTex = new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.ARGB32);
            maskRenderTex.Create();
        }
        if (maskPlaneMaterial != null)
        {
            // you can assign the mask RT to this material
            maskPlaneMaterial.mainTexture = maskRenderTex;
        }

#if UNITY_EDITOR || !UNITY_VISIONOS
        // Editor fallback
        InitWebCam(webCamDeviceName);
#else
        // visionOS device: start camera
        startCapture();
#endif
    }

    private void Update()
    {
#if UNITY_EDITOR || !UNITY_VISIONOS
        // Editor or Non-visionOS: read from webcam
        UpdateWebCamRender();
#else
        // Try to get native color feed
        if (!hasAcquiredTexture)
        {
            TryAcquireNativeTexture();
        }
        else
        {
            UpdateRenderTexture();   // color
        }

        // Also acquire the mask
        TryAcquireMaskTexture();
        if (hasAcquiredMask)
        {
            UpdateMaskRenderTexture();
        }
#endif
    }

    private void OnDestroy()
    {
#if UNITY_EDITOR || !UNITY_VISIONOS
        if (webCam != null)
        {
            webCam.Stop();
            webCam = null;
        }
#else
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

        if (webcamFrameTex == null)
        {
            webcamFrameTex = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false);
        }
    }

    private void UpdateWebCamRender()
    {
        if (webCam == null) return;
        if (webCam.width < 16 || webCam.height < 16) return; // not ready

        if (webcamFrameTex == null || webcamFrameTex.width != webCam.width || webcamFrameTex.height != webCam.height)
        {
            if (webcamFrameTex != null) Destroy(webcamFrameTex);
            webcamFrameTex = new Texture2D(webCam.width, webCam.height, TextureFormat.RGBA32, false);
        }

        webcamFrameTex.SetPixels(webCam.GetPixels());
        webcamFrameTex.Apply();

        Graphics.Blit(webcamFrameTex, renderTex);

        // For the mask in Editor, you could do a separate approach if you want
        // But not included in this example
    }
#endif

    // =============================================================================
    // SECTION B: Vision Pro camera approach
    // =============================================================================

#if UNITY_VISIONOS && !UNITY_EDITOR
    private void TryAcquireNativeTexture()
    {
        IntPtr ptr = getTexturePointer();
        if (ptr == IntPtr.Zero) return;

        nativeTexPtr = ptr;
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

        if (blitMaterial == null)
        {
            Graphics.Blit(nativeTexture, renderTex);
        }
        else
        {
            Graphics.Blit(nativeTexture, renderTex, blitMaterial);
        }
        Unity.PolySpatial.PolySpatialObjectUtils.MarkDirty(renderTex);
    }

    // --- NEW: mask side
    private void TryAcquireMaskTexture()
    {
        IntPtr ptr = getMaskTexturePointer();
        if (ptr == IntPtr.Zero) return; // no mask yet

        if (nativeMaskPtr != ptr)
        {
            nativeMaskPtr = ptr;
            if (nativeMaskTexture == null)
            {
                // Might assume same dimension as camera or something else
                nativeMaskTexture = Texture2D.CreateExternalTexture(
                    textureWidth,
                    textureHeight,
                    TextureFormat.BGRA32,
                    false,
                    false,
                    nativeMaskPtr
                );
            }
            nativeMaskTexture.UpdateExternalTexture(nativeMaskPtr);
            hasAcquiredMask = true;
        }
    }

    private void UpdateMaskRenderTexture()
    {
        if (nativeMaskTexture == null) return;
        if (maskRenderTex == null) return;

        Graphics.Blit(nativeMaskTexture, maskRenderTex);
        // if you want to do something custom, place code here
        // e.g. maskPlaneMaterial.mainTexture = maskRenderTex;

        Unity.PolySpatial.PolySpatialObjectUtils.MarkDirty(maskRenderTex);
    }
#endif
}
