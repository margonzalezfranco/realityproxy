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
            lr.SetPosition(0, sourceAnchor.sphereObj.transform.position);
            lr.SetPosition(1, targetAnchor.sphereObj.transform.position);
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.material = lineMaterial;
            lr.useWorldSpace = true;
            lr.material.color = Color.cyan;
            lr.material.color = new Color(lr.material.color.r, lr.material.color.g, lr.material.color.b, 0.2f);

            GameObject labelObj = null;
            if (labelPrefab != null && !string.IsNullOrEmpty(relationText))
            {
                Vector3 direction = (targetAnchor.sphereObj.transform.position - 
                                   sourceAnchor.sphereObj.transform.position).normalized;
                var midpoint = (sourceAnchor.sphereObj.transform.position + 
                               targetAnchor.sphereObj.transform.position) * 0.5f + 
                               Vector3.up * 0.025f;
                
                Quaternion rotation = Quaternion.LookRotation(direction);
                rotation *= Quaternion.Euler(0, -90f, 0f);

                labelObj = Instantiate(labelPrefab, midpoint, rotation, lineObj.transform);
                labelObj.name = $"RelLabel_{sourceAnchor.label}_to_{relatedItemLabel}";
                labelObj.transform.localScale = Vector3.one * labelScale;

                var tmp = labelObj.GetComponentInChildren<TextMeshPro>();
                if (tmp) tmp.text = relationText;
            }

            // Store the connection info
            activeLines.Add(new LineConnection
            {
                lineRenderer = lr,
                source = sourceAnchor,
                target = targetAnchor,
                labelObject = labelObj
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
        public GameObject labelObject;  // Add this to track the label
    }

    // Add Update method to continuously update line positions
    private void Update()
    {
        foreach (var connection in activeLines)
        {
            if (connection.source != null && connection.target != null)
            {
                // Update line positions
                connection.lineRenderer.SetPosition(0, connection.source.sphereObj.transform.position);
                connection.lineRenderer.SetPosition(1, connection.target.sphereObj.transform.position);

                // Update label position
                if (connection.labelObject != null)
                {
                    // Calculate new midpoint with upward offset
                    Vector3 midpoint = (connection.source.sphereObj.transform.position + 
                                      connection.target.sphereObj.transform.position) * 0.5f + 
                                      Vector3.up * 0.025f;
                    
                    connection.labelObject.transform.position = midpoint;

                    // Update label rotation to face camera
                    if (Camera.main != null)
                    {
                        Vector3 direction = (connection.target.sphereObj.transform.position - 
                                           connection.source.sphereObj.transform.position).normalized;
                        
                        // Calculate rotation to align with line direction
                        Quaternion rotation = Quaternion.LookRotation(direction);
                        rotation *= Quaternion.Euler(0, -90f, 0f);

                        // Check if label faces away from camera
                        Vector3 cameraForward = Camera.main.transform.forward;
                        Vector3 labelForward = rotation * Vector3.forward;
                        if (Vector3.Dot(cameraForward, labelForward) < 0)
                        {
                            rotation *= Quaternion.Euler(0, 180f, 0);
                        }

                        connection.labelObject.transform.rotation = rotation;
                    }
                }
            }
        }
    }
}
