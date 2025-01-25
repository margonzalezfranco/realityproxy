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
        // ------------------------------------------------
        // 1) EDITOR or Non-visionOS: Start Webcam fallback
        // ------------------------------------------------
        InitWebCam(webCamDeviceName);

        // Set material tiling and offset for Editor/non-visionOS
        // if (planeMaterial != null)
        // {
        //     planeMaterial.mainTextureScale = new Vector2(1, 1);    // Tiling
        //     planeMaterial.mainTextureOffset = new Vector2(0, 0);   // Offset
        // }

#else
        // ------------------------------------------------
        // 2) visionOS device: Use native plugin calls
        // ------------------------------------------------
        // if (planeMaterial != null)
        // {
        //     planeMaterial.mainTextureScale = new Vector2(1, -1);    // Tiling
        //     planeMaterial.mainTextureOffset = new Vector2(0, 1);   // Offset
        // }
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
        if (webCam != null) {
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
