using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Manages visual "relationship lines" between anchors when an object is "focused."
/// Attach this to an empty GameObject in your scene.
/// </summary>
public class RelationshipLineManager : MonoBehaviour
{
    [Header("Line Settings")]
    public Material lineMaterial;
    public float lineWidth = 0.01f;
    public GameObject labelPrefab;   // A small canvas or 3D text prefab
    [SerializeField] private float labelScale = 0.002f;  // Add this new field for label scaling

    // We'll store active lines so we can remove them later
    private List<LineConnection> activeLines = new List<LineConnection>();

    /// <summary>
    /// Creates lines from 'sourceAnchor' to each 'targetAnchor', with a text label describing the relationship.
    /// </summary>
    public void ShowRelationships(
        SceneObjectAnchor sourceAnchor, 
        Dictionary<string, string> relationships, 
        List<SceneObjectAnchor> allAnchors)
    {
        ClearAllLines();

        // Loop over each key in 'relationships'
        // Key = the label of the related item
        // Value = short relationship descriptor
        foreach (var kvp in relationships)
        {
            string relatedItemLabel = kvp.Key;          // "milk", "spoon", etc.
            string relationText = kvp.Value;            // "used with coffee", etc.

            // Find the anchor with label == relatedItemLabel
            var targetAnchor = allAnchors.Find(a => a.label == relatedItemLabel);
            if (targetAnchor == null)
            {
                Debug.LogWarning($"No anchor found for label '{relatedItemLabel}' in scene. Skipping line.");
                continue;
            }

            // Create a line from sourceAnchor -> targetAnchor
            var lineObj = new GameObject($"RelLine_{sourceAnchor.label}_to_{relatedItemLabel}");
            lineObj.transform.SetParent(this.transform, false);

            var lr = lineObj.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.SetPosition(0, sourceAnchor.position);
            lr.SetPosition(1, targetAnchor.position);
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.material = lineMaterial;
            lr.useWorldSpace = true;
            lr.material.color = Color.cyan;

            // Optionally, create a label in the midpoint
            if (labelPrefab != null && !string.IsNullOrEmpty(relationText))
            {
                // Calculate direction and offset the midpoint slightly upward
                Vector3 direction = (targetAnchor.position - sourceAnchor.position).normalized;
                var midpoint = (sourceAnchor.position + targetAnchor.position) * 0.5f + Vector3.up * 0.025f;
                
                // Calculate rotation to align with the line direction
                Quaternion rotation = Quaternion.LookRotation(direction);
                // Rotate 90 degrees around the right vector to make it face up
                rotation *= Quaternion.Euler(0, -90f, 0f);

                // Check if the label is facing away from the camera
                Vector3 cameraForward = Camera.main.transform.forward;
                Vector3 labelForward = rotation * Vector3.forward;
                float dotProduct = Vector3.Dot(cameraForward, labelForward);
                
                // If the label is facing the same direction as the camera (dot product > 0),
                // rotate it 180 degrees so it faces the camera
                if (dotProduct < 0)
                {
                    rotation *= Quaternion.Euler(0, 180f, 0);
                }
                
                var labelObj = Instantiate(labelPrefab, midpoint, rotation, lineObj.transform);
                labelObj.name = $"RelLabel_{sourceAnchor.label}_to_{relatedItemLabel}";
                labelObj.transform.localScale = Vector3.one * labelScale;

                // If it's a 3D text or a small canvas with TextMeshPro
                var tmp = labelObj.GetComponentInChildren<TextMeshPro>();
                if (tmp != null)
                {
                    tmp.text = relationText;
                }
            }

            // Store in active list for easy clearing
            activeLines.Add(new LineConnection
            {
                lineRenderer = lr,
                source = sourceAnchor,
                target = targetAnchor
            });
        }
    }

    /// <summary>
    /// Clears all relationship lines currently displayed.
    /// </summary>
    public void ClearAllLines()
    {
        foreach (var c in activeLines)
        {
            if (c.lineRenderer != null)
            {
                Destroy(c.lineRenderer.gameObject);
            }
        }
        activeLines.Clear();
    }

    private class LineConnection
    {
        public LineRenderer lineRenderer;
        public SceneObjectAnchor source;
        public SceneObjectAnchor target;
    }
}
