using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class VisionProCameraBridge : MonoBehaviour
{
    // Native plugin imports
    [DllImport("__Internal")]
    private static extern void startCapture();

    [DllImport("__Internal")]
    private static extern void stopCapture();

    [DllImport("__Internal")]
    private static extern IntPtr getTexturePointer();

    private bool hasAcquiredTexture = false;
    private Texture2D nativeTexture;
    private IntPtr nativeTexPtr = IntPtr.Zero;

    [SerializeField] private RenderTexture renderTex;
    [SerializeField] private Material blitMaterial; // optional for flipping
    [SerializeField] private Material planeMaterial;
    [SerializeField] private int textureWidth = 1920;
    [SerializeField] private int textureHeight = 1080;

    void Start()
    {
        if (renderTex == null)
        {
            renderTex = new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.ARGB32);
            renderTex.Create();
        }

        if (planeMaterial != null)
        {
            planeMaterial.mainTexture = renderTex;
        }

        // Kick off camera capture in Swift
        startCapture();
    }

    void Update()
    {
        if (!hasAcquiredTexture)
        {
            TryAcquireNativeTexture();
        }
        else
        {
            UpdateRenderTexture();
        }
    }

    private void TryAcquireNativeTexture()
    {
        IntPtr ptr = getTexturePointer();
        if (ptr == IntPtr.Zero) return; // Not ready yet

        nativeTexPtr = ptr;
        // Create external texture referencing the MTLTexture
        nativeTexture = Texture2D.CreateExternalTexture(
            textureWidth, textureHeight,
            TextureFormat.BGRA32,
            false, false,
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
        
        // If needed for PolySpatial:
        Unity.PolySpatial.PolySpatialObjectUtils.MarkDirty(renderTex);
    }

    private void OnDestroy()
    {
        stopCapture();
    }
}
