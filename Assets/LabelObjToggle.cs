using UnityEngine;

public class LabelObjToggle : MonoBehaviour
{
    public GameObject MenuForSpawn;
    public void ToggleLabel()
    {
        MenuForSpawn.SetActive(!MenuForSpawn.activeSelf);
    }
}