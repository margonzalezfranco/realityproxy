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
    [SerializeField] private float labelScale = 0.002f;  // Changed back to original value

    [Header("Highlight Settings")]
    [Tooltip("Time in seconds before highlights are automatically cleared. Set to 0 to disable auto-clear.")]
    public float highlightTimeout = 10f;
    public float highlightTimer { get; set; } = 0f;
    public bool hasActiveHighlights { get; set; } = false;

    [Header("Scene Dependencies")]
    [Tooltip("Manager that tracks all recognized objects in the scene.")]
    public SceneObjectManager sceneObjectManager;

    // We'll store active lines so we can remove them later
    private List<LineConnection> activeLines = new List<LineConnection>();

    private void Awake()
    {
        // Find SceneObjectManager if not assigned
        if (sceneObjectManager == null)
        {
            sceneObjectManager = FindFirstObjectByType<SceneObjectManager>();
            if (sceneObjectManager == null)
            {
                Debug.LogError("SceneObjectManager not found! Please assign it in the inspector.");
                return;
            }
        }
    }

    /// <summary>
    /// Clears all highlights and relationship lines from the previous generation.
    /// Call this before starting a new round of relationship or highlight generation.
    /// </summary>
    public void ClearAllHighlightsAndLines()
    {
        // Default color to restore anchors to when clearing relationships (#5E5E5E)
        Color defaultSphereColor = new Color(
            r: 0.369f,  // 94/255
            g: 0.369f,  // 94/255
            b: 0.369f,  // 94/255
            a: 1.0f     // 100% alpha
        );
        
        // First, clear all existing relationship lines and their associated highlights
        ClearAllLines();
        
        // Then, find and reset ALL scene anchors (in case some were highlighted but not connected by lines)
        if (sceneObjectManager != null)
        {
            var allAnchors = sceneObjectManager.GetAllAnchors();
            foreach (var anchor in allAnchors)
            {
                if (anchor != null && anchor.sphereObj != null)
                {
                    // Reset sphere color
                    var renderer = anchor.sphereObj.GetComponent<Renderer>();
                    if (renderer != null && renderer.material != null)
                    {
                        renderer.material.color = defaultSphereColor;
                    }

                    // Reset label color
                    var labelObj = anchor.sphereObj.transform.GetChild(0)?.gameObject;
                    if (labelObj != null)
                    {
                        var labelRenderer = labelObj.GetComponent<Renderer>();
                        if (labelRenderer != null && labelRenderer.material != null)
                        {
                            labelRenderer.material.color = defaultSphereColor;
                        }
                    }
                }
            }
        }
        
        // Clear response text and hide chatbox if they exist
        var speechRecorder = FindFirstObjectByType<SpeechToTextRecorder>();
        if (speechRecorder != null)
        {
            if (speechRecorder.responseTextOnObject != null)
            {
                speechRecorder.responseTextOnObject.text = "";
            }
            if (speechRecorder.chatboxOnObject != null)
            {
                speechRecorder.chatboxOnObject.SetActive(false);
            }
        }
        
        Debug.Log("Cleared all highlights, relationship lines, and response text");
    }

    /// <summary>
    /// Shows relationships from 'sourceAnchor' to each 'targetAnchor', with a text label describing the relationship.
    /// </summary>
    public void ShowRelationships(
        SceneObjectAnchor sourceAnchor, 
        Dictionary<string, string> relationships, 
        List<SceneObjectAnchor> allAnchors)
    {
        // Reset the highlight timer when showing new relationships
        highlightTimer = 0f;
        hasActiveHighlights = true;

        if (sourceAnchor == null)
        {
            Debug.LogError("ShowRelationships: sourceAnchor is null!");
            return;
        }

        if (relationships == null || relationships.Count == 0)
        {
            Debug.LogWarning("ShowRelationships: No relationships provided!");
            return;
        }

        if (allAnchors == null || allAnchors.Count == 0)
        {
            Debug.LogWarning("ShowRelationships: No anchors provided!");
            return;
        }

        // Define relationship line color (hex: #3089CF)
        Color lineColor = new Color(
            r: 0.188f,  // 48/255
            g: 0.537f,  // 137/255
            b: 0.812f,  // 207/255
            a: 0.2f     // Changed back to original alpha
        );

        // Color for source anchor highlight (hex: #2096F3 with 100% alpha)
        Color sourceHighlightColor = new Color(
            r: 0.125f,  // 32/255
            g: 0.588f,  // 150/255
            b: 0.953f,  // 243/255
            a: 1.0f     // 100% alpha
        );

        // Color for target anchor highlight (hex: #2096F3 with 50% alpha)
        Color targetHighlightColor = new Color(
            r: 0.125f,  // 32/255
            g: 0.588f,  // 150/255
            b: 0.953f,  // 243/255
            a: 0.5f     // 50% alpha
        );

        // Highlight the source anchor and its label
        if (sourceAnchor.sphereObj != null)
        {
            // Highlight the sphere
            var renderer = sourceAnchor.sphereObj.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
            {
                renderer.material.color = sourceHighlightColor;
                Debug.Log($"Highlighted source anchor: {sourceAnchor.label}");
            }

            // Highlight the child label object
            var sourceLabelObj = sourceAnchor.sphereObj.transform.GetChild(0)?.gameObject;
            if (sourceLabelObj != null)
            {
                var labelRenderer = sourceLabelObj.GetComponent<Renderer>();
                if (labelRenderer != null && labelRenderer.material != null)
                {
                    labelRenderer.material.color = sourceHighlightColor;
                    Debug.Log($"Highlighted source anchor label: {sourceAnchor.label}");
                }
            }
        }

        // Log that we're starting to create relationship lines
        Debug.Log($"Creating relationship lines from '{sourceAnchor.label}' to {relationships.Count} targets");

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

            // Highlight the target anchor and its label
            if (targetAnchor.sphereObj != null)
            {
                // Highlight the sphere
                var renderer = targetAnchor.sphereObj.GetComponent<Renderer>();
                if (renderer != null && renderer.material != null)
                {
                    renderer.material.color = targetHighlightColor;
                    Debug.Log($"Highlighted target anchor: {targetAnchor.label}");
                }

                // Highlight the child label object
                var targetLabelObj = targetAnchor.sphereObj.transform.GetChild(0)?.gameObject;
                if (targetLabelObj != null)
                {
                    var labelRenderer = targetLabelObj.GetComponent<Renderer>();
                    if (labelRenderer != null && labelRenderer.material != null)
                    {
                        labelRenderer.material.color = targetHighlightColor;
                        Debug.Log($"Highlighted target anchor label: {targetAnchor.label}");
                    }
                }
            }

            if (sourceAnchor.sphereObj == null)
            {
                Debug.LogError($"Source anchor '{sourceAnchor.label}' has no sphereObj!");
                continue;
            }

            if (targetAnchor.sphereObj == null)
            {
                Debug.LogError($"Target anchor '{targetAnchor.label}' has no sphereObj!");
                continue;
            }

            // Check if a line already exists between these two anchors
            bool lineExists = false;
            foreach (var connection in activeLines)
            {
                if ((connection.source == sourceAnchor && connection.target == targetAnchor) ||
                    (connection.source == targetAnchor && connection.target == sourceAnchor))
                {
                    lineExists = true;
                    
                    // Update existing line's label if the connection is in the same direction
                    if (connection.source == sourceAnchor && connection.target == targetAnchor)
                    {
                        var tmp = connection.labelObject?.GetComponentInChildren<TextMeshPro>();
                        if (tmp != null)
                        {
                            tmp.text = relationText;
                            Debug.Log($"Updated existing connection: {sourceAnchor.label} → {targetAnchor.label} with text: '{relationText}'");
                        }
                    }
                    break;
                }
            }

            // Skip if line already exists
            if (lineExists) continue;

            // Create a line from sourceAnchor -> targetAnchor
            var lineObj = new GameObject($"RelLine_{sourceAnchor.label}_to_{relatedItemLabel}");
            lineObj.transform.SetParent(this.transform, false);

            var lr = lineObj.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.SetPosition(0, sourceAnchor.sphereObj.transform.position);
            lr.SetPosition(1, targetAnchor.sphereObj.transform.position);
            lr.startWidth = lineWidth; // Removed the multiplier
            lr.endWidth = lineWidth;
            
            // Make sure the material is assigned
            if (lineMaterial == null)
            {
                Debug.LogWarning("Line material is null, creating default material");
                lineMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                lineMaterial.color = lineColor;
            }
            
            // Create a new material instance to avoid shared material issues
            lr.material = new Material(lineMaterial);
            lr.material.color = lineColor; // Explicitly set color
            lr.useWorldSpace = true;
            lr.startColor = lineColor;
            lr.endColor = lineColor;

            Debug.Log($"Created line from '{sourceAnchor.label}' to '{targetAnchor.label}'");

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
                if (tmp) 
                {
                    tmp.text = relationText;
                    Debug.Log($"Created label: '{relationText}'");
                }
                else
                {
                    Debug.LogWarning("TextMeshPro component not found on label prefab!");
                }
            }
            else
            {
                if (labelPrefab == null)
                    Debug.LogWarning("LabelPrefab is null!");
                if (string.IsNullOrEmpty(relationText))
                    Debug.LogWarning($"RelationText is empty for '{relatedItemLabel}'!");
            }

            // Store the connection info
            activeLines.Add(new LineConnection
            {
                lineRenderer = lr,
                source = sourceAnchor,
                target = targetAnchor,
                labelObject = labelObj
            });
            
            Debug.Log($"Added line connection: {sourceAnchor.label} → {targetAnchor.label}");
        }
        
        // Log the total number of active lines
        Debug.Log($"Total active lines: {activeLines.Count}");
    }

    /// <summary>
    /// Shows bidirectional relationships between multiple objects from a list of explicit relationships.
    /// Each relationship specifies source, target, and a description.
    /// </summary>
    /// <param name="relationships">List of relationships with explicit source and target objects</param>
    /// <param name="allAnchors">List of all available anchors in the scene</param>
    public void ShowBidirectionalRelationships(List<RelationshipInfo> relationships, List<SceneObjectAnchor> allAnchors)
    {
        if (relationships == null || relationships.Count == 0)
        {
            Debug.LogWarning("ShowBidirectionalRelationships: No relationships provided!");
            return;
        }

        if (allAnchors == null || allAnchors.Count == 0)
        {
            Debug.LogWarning("ShowBidirectionalRelationships: No anchors provided!");
            return;
        }

        Debug.Log($"ShowBidirectionalRelationships: Processing {relationships.Count} relationships with {allAnchors.Count} available anchors");
        
        // Log all available anchors for debugging
        foreach (var anchor in allAnchors)
        {
            Debug.Log($"Available anchor: {anchor.label} (has sphereObj: {anchor.sphereObj != null})");
        }

        // First clear any existing lines
        ClearAllLines();

        // Group relationships by source object
        Dictionary<string, Dictionary<string, string>> groupedRelationships = new Dictionary<string, Dictionary<string, string>>();
        
        foreach (var relation in relationships)
        {
            string sourceObj = relation.SourceObject;
            string targetObj = relation.TargetObject;
            string relationLabel = relation.RelationLabel;
            
            Debug.Log($"Processing relationship: {sourceObj} -> {targetObj}: '{relationLabel}'");
            
            if (string.IsNullOrEmpty(sourceObj) || string.IsNullOrEmpty(targetObj))
            {
                Debug.LogWarning("Found relationship with missing source or target object");
                continue;
            }
            
            if (!groupedRelationships.ContainsKey(sourceObj))
            {
                groupedRelationships[sourceObj] = new Dictionary<string, string>();
            }
            
            groupedRelationships[sourceObj][targetObj] = relationLabel;
        }
        
        // Visualize each group of relationships
        foreach (var sourceObjKvp in groupedRelationships)
        {
            string sourceObjectLabel = sourceObjKvp.Key;
            Dictionary<string, string> targetRelationships = sourceObjKvp.Value;
            
            var sourceAnchor = FindBestMatchingAnchor(sourceObjectLabel, allAnchors);
            if (sourceAnchor != null)
            {
                Debug.Log($"Visualizing relationships from {sourceObjectLabel} to {targetRelationships.Count} other objects");
                ShowRelationships(sourceAnchor, targetRelationships, allAnchors);
            }
            else
            {
                Debug.LogWarning($"Could not find anchor for source object: {sourceObjectLabel}");
                foreach (var anchor in allAnchors)
                {
                    Debug.Log($"  Available: '{anchor.label}'");
                }
            }
        }
    }

    /// <summary>
    /// Finds the best matching anchor for a given label, trying different matching techniques
    /// </summary>
    private SceneObjectAnchor FindBestMatchingAnchor(string label, List<SceneObjectAnchor> anchors)
    {
        // Step 1: Try exact match (case-insensitive)
        var exactMatch = anchors.Find(a => string.Equals(a.label, label, System.StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
        {
            Debug.Log($"Found exact match for '{label}'");
            return exactMatch;
        }
        
        // Step 2: Try contains match
        var containsMatch = anchors.Find(a => 
            a.label.IndexOf(label, System.StringComparison.OrdinalIgnoreCase) >= 0 || 
            label.IndexOf(a.label, System.StringComparison.OrdinalIgnoreCase) >= 0);
            
        if (containsMatch != null)
        {
            Debug.Log($"Found contains match: '{containsMatch.label}' for '{label}'");
            return containsMatch;
        }
        
        // Step 3: Try word-by-word match for multi-word labels
        string[] words = label.Split(' ', '-', '_');
        if (words.Length > 1)
        {
            foreach (var word in words)
            {
                if (word.Length < 3) continue; // Skip short words
                
                var wordMatch = anchors.Find(a => 
                    a.label.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    
                if (wordMatch != null)
                {
                    Debug.Log($"Found word match: '{wordMatch.label}' for word '{word}' from '{label}'");
                    return wordMatch;
                }
            }
        }
        
        Debug.LogWarning($"No matching anchor found for '{label}' among {anchors.Count} anchors");
        return null;
    }

    /// <summary>
    /// Clears all relationship lines currently displayed.
    /// </summary>
    public void ClearAllLines()
    {
        // Default color to restore anchors to when clearing relationships (#5E5E5E)
        Color defaultSphereColor = new Color(
            r: 0.369f,  // 94/255
            g: 0.369f,  // 94/255
            b: 0.369f,  // 94/255
            a: 1.0f     // 100% alpha
        );
        
        // Restore original colors of source and target anchors
        HashSet<SceneObjectAnchor> processedAnchors = new HashSet<SceneObjectAnchor>();
        
        foreach (var connection in activeLines)
        {
            // Reset source anchor color if it exists and hasn't been processed
            if (connection.source != null && connection.source.sphereObj != null && !processedAnchors.Contains(connection.source))
            {
                // Reset sphere color
                var renderer = connection.source.sphereObj.GetComponent<Renderer>();
                if (renderer != null && renderer.material != null)
                {
                    renderer.material.color = defaultSphereColor;
                }

                // Reset label color
                var sourceLabelObj = connection.source.sphereObj.transform.GetChild(0)?.gameObject;
                if (sourceLabelObj != null)
                {
                    var labelRenderer = sourceLabelObj.GetComponent<Renderer>();
                    if (labelRenderer != null && labelRenderer.material != null)
                    {
                        labelRenderer.material.color = defaultSphereColor;
                    }
                }

                processedAnchors.Add(connection.source);
                Debug.Log($"Restored color for source anchor and label: {connection.source.label}");
            }
            
            // Reset target anchor color if it exists and hasn't been processed
            if (connection.target != null && connection.target.sphereObj != null && !processedAnchors.Contains(connection.target))
            {
                // Reset sphere color
                var renderer = connection.target.sphereObj.GetComponent<Renderer>();
                if (renderer != null && renderer.material != null)
                {
                    renderer.material.color = defaultSphereColor;
                }

                // Reset label color
                var targetLabelObj = connection.target.sphereObj.transform.GetChild(0)?.gameObject;
                if (targetLabelObj != null)
                {
                    var labelRenderer = targetLabelObj.GetComponent<Renderer>();
                    if (labelRenderer != null && labelRenderer.material != null)
                    {
                        labelRenderer.material.color = defaultSphereColor;
                    }
                }

                processedAnchors.Add(connection.target);
                Debug.Log($"Restored color for target anchor and label: {connection.target.label}");
            }
            
            // Destroy the line renderer object
            if (connection.lineRenderer != null)
            {
                Destroy(connection.lineRenderer.gameObject);
            }
        }
        
        Debug.Log($"Cleared {activeLines.Count} relationship lines");
        activeLines.Clear();
    }

    /// <summary>
    /// Clears only the relationship line between two specific anchors.
    /// </summary>
    /// <param name="sourceAnchor">The source anchor of the line</param>
    /// <param name="targetAnchor">The target anchor of the line</param>
    /// <returns>True if the line was found and removed, false otherwise</returns>
    public bool ClearSpecificLine(SceneObjectAnchor sourceAnchor, SceneObjectAnchor targetAnchor)
    {
        if (sourceAnchor == null || targetAnchor == null)
        {
            Debug.LogWarning("Cannot clear specific line: Invalid anchor reference");
            return false;
        }

        // Find the line connecting these two anchors
        LineConnection lineToRemove = null;
        foreach (var connection in activeLines)
        {
            if ((connection.source == sourceAnchor && connection.target == targetAnchor) ||
                (connection.source == targetAnchor && connection.target == sourceAnchor))
            {
                lineToRemove = connection;
                break;
            }
        }
        
        // If found, remove it
        if (lineToRemove != null)
        {
            if (lineToRemove.lineRenderer != null)
            {
                Destroy(lineToRemove.lineRenderer.gameObject);
            }
            activeLines.Remove(lineToRemove);
            Debug.Log($"Cleared relationship line between '{sourceAnchor.label}' and '{targetAnchor.label}'");
            return true;
        }
        
        // Line not found
        Debug.Log($"No active relationship line found between '{sourceAnchor.label}' and '{targetAnchor.label}'");
        return false;
    }

    private class LineConnection
    {
        public LineRenderer lineRenderer;
        public SceneObjectAnchor source;
        public SceneObjectAnchor target;
        public GameObject labelObject;  // Add this to track the label
    }

    // Update method simplified to match the original code
    private void Update()
    {
        // Check if we should clear highlights due to timeout
        if (hasActiveHighlights && highlightTimeout > 0)
        {
            highlightTimer += Time.deltaTime;
            if (highlightTimer >= highlightTimeout)
            {
                ClearAllHighlightsAndLines();
                highlightTimer = 0f;
                hasActiveHighlights = false;
                Debug.Log($"Cleared highlights after {highlightTimeout} seconds timeout");
            }
        }

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

    // Class to match the relationship info structure from SpeechToTextRecorder
    [System.Serializable]
    public class RelationshipInfo
    {
        public string SourceObject;
        public string TargetObject;
        public string RelationLabel;
    }
}
