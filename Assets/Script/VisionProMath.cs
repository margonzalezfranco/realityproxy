using Unity.Mathematics;
using UnityEngine;

public static class VisionProMath
{
    /// <summary>
    /// Unprojects an image point (px,py) -> local camera space direction, ignoring extrinsics translation.
    /// flipX/flipY handle top-left vs. bottom-left or horizontally mirrored images if needed.
    /// extrinsicsMatrixNoTrans is purely rotation if you want to rotate the direction 
    /// in 'camera' space. We do that inside this function or you can do it outside.
    /// 
    /// This function returns a direction in "world" space or "camera" space depending on usage. 
    /// For clarity, let's rename it unprojectToLocalNoTranslation, then apply XR camera rotation outside.
    /// </summary>
    public static Vector3 UnprojectToLocalNoTranslation(
        float px,
        float py,
        float imageWidth,
        float imageHeight,
        bool flipX,
        bool flipY,
        float3x3 intrinsicsMatrix,
        float4x4 extrinsicsMatrixNoTrans
    )
    {
        if (flipX) {
            px = imageWidth - px;
        }
        if (flipY) {
            py = imageHeight - py;
        }

        float3 imageVec = new float3(px, py, 1.0f);
        float3x3 Kinv = math.inverse(intrinsicsMatrix);
        float3 camDir = math.mul(Kinv, imageVec);

        // Option A: if you want to use the extrinsics rotation, apply it now:
        //   The code below transforms the direction by extrinsicsMatrixNoTrans 
        //   ignoring translation. So the returned vector is in "world" axes 
        //   if extrinsics is "camera->world" rotation. 
        //
        //   If you prefer to do it outside, just return (camDir.x, camDir.y, camDir.z).
        // 
        // We'll assume you want the rotation from extrinsics:
        // float4 d = new float4(camDir.x, camDir.y, camDir.z, 0f);
        // float4 r = math.mul(extrinsicsMatrixNoTrans, d);
        // return new Vector3(r.x, r.y, r.z);
        return new Vector3(camDir.x, camDir.y, camDir.z);
    }
}
