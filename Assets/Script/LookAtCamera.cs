using UnityEngine;

public class LookAtCamera : MonoBehaviour
{
    public Camera targetCamera;

    void Start()
    {
        if (targetCamera != null)
        {
            // Get direction from label to camera
            Vector3 direction = transform.position - targetCamera.transform.position;
            
            // Create rotation with locked Z
            Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
            transform.rotation = rotation;
        }
    }
}