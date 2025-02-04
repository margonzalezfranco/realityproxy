using UnityEngine;
using TMPro;

public class MenuScript : MonoBehaviour
{
    [SerializeField] private TextMeshPro titleText;

    public void SetMenuTitle(string newTitle)
    {
        if (titleText != null)
        {
            titleText.text = newTitle;
        }
    }
}
