using UnityEngine;

/// <summary>
/// Represents a single recognized object ("anchor") in the AR scene.
/// </summary>
[System.Serializable]
public class SceneObjectAnchor
{
    public string label;
    public Vector3 position;
    public GameObject sphereObj;
    public float boundingRadius = 0.2f;
    public bool userLocked = false;
}
