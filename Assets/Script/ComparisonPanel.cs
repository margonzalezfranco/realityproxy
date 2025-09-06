using UnityEngine;
using TMPro;

/// <summary>
/// UI component for displaying object comparison results in 3D space.
/// Displays structured comparison data from Gemini API in a floating panel.
/// </summary>
public class ComparisonPanel : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("Title text showing what objects are being compared")]
    public TextMeshPro titleText;
    
    [Tooltip("Main content text showing all comparison information")]
    public TextMeshPro contentText;
    
    [Header("Display Settings")]
    [Tooltip("Maximum number of items to show per section")]
    public int maxItemsPerSection = 5;
    
    [Tooltip("Enable debug logging")]
    public bool enableDebugLog = false;
    
    private ObjectComparisonManager.ComparisonData currentData;
    
    void Start()
    {
        // Panel will auto-hide when selection changes - no manual close needed
    }
    
    public void SetComparisonData(ObjectComparisonManager.ComparisonData data)
    {
        currentData = data;
        PopulatePanel();
    }
    
    private void PopulatePanel()
    {
        if (currentData?.comparison == null) return;
        
        var comparison = currentData.comparison;
        
        // Set title
        if (titleText != null && currentData.items != null && currentData.items.Length >= 2)
        {
            titleText.text = $"Comparing: {currentData.items[0]} vs {currentData.items[1]}";
        }
        
        // Format all content into a single string
        string content = FormatComparisonContent(comparison);
        
        // Set content text
        if (contentText != null)
        {
            contentText.text = content;
        }
        
        if (enableDebugLog) Debug.Log("ComparisonPanel populated with simplified content");
    }
    
    private string FormatComparisonContent(ObjectComparisonManager.ComparisonResult comparison)
    {
        if (comparison == null) return "No comparison data available.";
        
        // If we have raw/original text, use that
        if (!string.IsNullOrEmpty(comparison.original))
        {
            return comparison.original;
        }
        
        // Otherwise format the structured data
        System.Text.StringBuilder content = new System.Text.StringBuilder();
        
        // Add similarities
        if (comparison.similarities != null && comparison.similarities.Length > 0)
        {
            content.AppendLine("SIMILARITIES:");
            foreach (string similarity in comparison.similarities)
            {
                if (!string.IsNullOrEmpty(similarity))
                    content.AppendLine($"• {similarity}");
            }
            content.AppendLine();
        }
        
        // Add differences  
        if (comparison.differences != null && comparison.differences.Length > 0)
        {
            content.AppendLine("DIFFERENCES:");
            foreach (string difference in comparison.differences)
            {
                if (!string.IsNullOrEmpty(difference))
                    content.AppendLine($"• {difference}");
            }
            content.AppendLine();
        }
        
        // Add functions
        if (comparison.functions != null && comparison.functions.Count > 0)
        {
            content.AppendLine("FUNCTIONS:");
            foreach (var kvp in comparison.functions)
            {
                content.AppendLine($"• {kvp.Key}: {kvp.Value}");
            }
            content.AppendLine();
        }
        
        // Add relationship
        if (!string.IsNullOrEmpty(comparison.relationship))
        {
            content.AppendLine("RELATIONSHIP:");
            content.AppendLine(comparison.relationship);
            content.AppendLine();
        }
        
        // Add usage
        if (!string.IsNullOrEmpty(comparison.usage))
        {
            content.AppendLine("USAGE:");
            content.AppendLine(comparison.usage);
        }
        
        return content.ToString().TrimEnd();
    }
    
    // No manual close needed - panel auto-hides when selection changes
    
    void Update()
    {
        // Make panel always face the main camera
        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            transform.LookAt(mainCamera.transform);
            transform.Rotate(0, 180, 0); // Flip to face camera correctly
        }
    }
    
    // No cleanup needed - no button events to unsubscribe
}