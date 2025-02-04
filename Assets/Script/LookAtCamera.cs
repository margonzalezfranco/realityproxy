using UnityEngine;

public class LookAtCamera : MonoBehaviour
{
    public Camera targetCamera;

    void LateUpdate()
    {
        if (targetCamera != null)
        {
            // Get the direction to look at
            Vector3 direction = targetCamera.transform.rotation * Vector3.forward;
            
            // Create rotation with locked Z
            Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
            transform.rotation = rotation;
        }
    }
}