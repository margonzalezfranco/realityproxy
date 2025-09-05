using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PolySpatial.Template;

/// <summary>
/// UI component for displaying object comparison results in 3D space.
/// Displays structured comparison data from Gemini API in a floating panel.
/// </summary>
public class ComparisonPanel : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("Title text showing what objects are being compared")]
    public TextMeshProUGUI titleText;
    
    [Tooltip("Container for similarities section")]
    public GameObject similaritiesContainer;
    
    [Tooltip("Template for similarity list items")]
    public GameObject similarityItemTemplate;
    
    [Tooltip("Container for differences section")]
    public GameObject differencesContainer;
    
    [Tooltip("Template for difference list items")]
    public GameObject differenceItemTemplate;
    
    [Tooltip("Container for functions section")]
    public GameObject functionsContainer;
    
    [Tooltip("Template for function description items")]
    public GameObject functionItemTemplate;
    
    [Tooltip("Text for relationship description")]
    public TextMeshProUGUI relationshipText;
    
    [Tooltip("Text for usage description")]
    public TextMeshProUGUI usageText;
    
    [Tooltip("Fallback text for raw responses")]
    public TextMeshProUGUI fallbackText;
    
    [Tooltip("Close button")]
    public SpatialUIButton closeButton;
    
    [Header("Display Settings")]
    [Tooltip("Maximum number of items to show per section")]
    public int maxItemsPerSection = 5;
    
    [Tooltip("Enable debug logging")]
    public bool enableDebugLog = false;
    
    private ObjectComparisonManager.ComparisonData currentData;
    
    void Start()
    {
        // Set up close button if available
        if (closeButton != null)
        {
            closeButton.WasPressed += OnCloseButtonPressed;
        }
        
        // Hide templates
        if (similarityItemTemplate != null) similarityItemTemplate.SetActive(false);
        if (differenceItemTemplate != null) differenceItemTemplate.SetActive(false);
        if (functionItemTemplate != null) functionItemTemplate.SetActive(false);
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
        
        // Check if we have structured data or fallback to original text
        if (!string.IsNullOrEmpty(comparison.original))
        {
            // Use fallback display for unparsed responses
            DisplayFallbackContent(comparison.original);
        }
        else
        {
            // Display structured comparison data
            DisplayStructuredContent(comparison);
        }
        
        if (enableDebugLog) Debug.Log("ComparisonPanel populated with data");
    }
    
    private void DisplayStructuredContent(ObjectComparisonManager.ComparisonResult comparison)
    {
        // Hide fallback text
        if (fallbackText != null) fallbackText.gameObject.SetActive(false);
        
        // Populate similarities
        PopulateListSection(similaritiesContainer, similarityItemTemplate, comparison.similarities, "Similarities");
        
        // Populate differences
        PopulateListSection(differencesContainer, differenceItemTemplate, comparison.differences, "Differences");
        
        // Populate functions
        if (comparison.functions != null && functionsContainer != null && functionItemTemplate != null)
        {
            ClearContainer(functionsContainer, functionItemTemplate);
            
            foreach (var kvp in comparison.functions)
            {
                CreateFunctionItem(functionsContainer, functionItemTemplate, kvp.Key, kvp.Value);
            }
        }
        
        // Set relationship text
        if (relationshipText != null)
        {
            relationshipText.text = !string.IsNullOrEmpty(comparison.relationship) ? 
                $"Relationship: {comparison.relationship}" : "";
            relationshipText.gameObject.SetActive(!string.IsNullOrEmpty(comparison.relationship));
        }
        
        // Set usage text
        if (usageText != null)
        {
            usageText.text = !string.IsNullOrEmpty(comparison.usage) ? 
                $"Usage: {comparison.usage}" : "";
            usageText.gameObject.SetActive(!string.IsNullOrEmpty(comparison.usage));
        }
    }
    
    private void DisplayFallbackContent(string originalText)
    {
        // Hide structured content containers
        if (similaritiesContainer != null) similaritiesContainer.SetActive(false);
        if (differencesContainer != null) differencesContainer.SetActive(false);
        if (functionsContainer != null) functionsContainer.SetActive(false);
        if (relationshipText != null) relationshipText.gameObject.SetActive(false);
        if (usageText != null) usageText.gameObject.SetActive(false);
        
        // Show fallback text
        if (fallbackText != null)
        {
            fallbackText.text = originalText;
            fallbackText.gameObject.SetActive(true);
        }
    }
    
    private void PopulateListSection(GameObject container, GameObject template, string[] items, string sectionName)
    {
        if (container == null || template == null || items == null) return;
        
        // Clear existing items (except template)
        ClearContainer(container, template);
        
        // Add new items
        int itemCount = Mathf.Min(items.Length, maxItemsPerSection);
        for (int i = 0; i < itemCount; i++)
        {
            if (!string.IsNullOrEmpty(items[i]))
            {
                CreateListItem(container, template, items[i]);
            }
        }
        
        // Show/hide container based on whether we have items
        container.SetActive(itemCount > 0);
        
        if (enableDebugLog) Debug.Log($"Populated {sectionName} with {itemCount} items");
    }
    
    private void CreateListItem(GameObject container, GameObject template, string text)
    {
        GameObject item = Instantiate(template, container.transform);
        item.SetActive(true);
        
        // Find and set text component
        TextMeshProUGUI textComponent = item.GetComponentInChildren<TextMeshProUGUI>();
        if (textComponent != null)
        {
            textComponent.text = $"• {text}";
        }
    }
    
    private void CreateFunctionItem(GameObject container, GameObject template, string itemName, string functionDescription)
    {
        GameObject item = Instantiate(template, container.transform);
        item.SetActive(true);
        
        // Find text components - assume template has multiple text elements
        TextMeshProUGUI[] textComponents = item.GetComponentsInChildren<TextMeshProUGUI>();
        
        if (textComponents.Length >= 2)
        {
            // First text for item name, second for description
            textComponents[0].text = $"{itemName}:";
            textComponents[1].text = functionDescription;
        }
        else if (textComponents.Length == 1)
        {
            // Single text component - combine both
            textComponents[0].text = $"{itemName}: {functionDescription}";
        }
    }
    
    private void ClearContainer(GameObject container, GameObject template)
    {
        // Remove all children except the template
        for (int i = container.transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = container.transform.GetChild(i).gameObject;
            if (child != template && child.activeInHierarchy)
            {
                Destroy(child);
            }
        }
    }
    
    private void OnCloseButtonPressed(string buttonText, MeshRenderer meshRenderer, int buttonIndex)
    {
        // Find and notify the comparison manager to hide the panel
        ObjectComparisonManager comparisonManager = FindObjectOfType<ObjectComparisonManager>();
        if (comparisonManager != null)
        {
            comparisonManager.ForceHidePanel();
        }
        else
        {
            // Fallback - just destroy this panel
            Destroy(gameObject);
        }
        
        if (enableDebugLog) Debug.Log("Comparison panel closed by user");
    }
    
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
    
    void OnDestroy()
    {
        // Clean up button listener
        if (closeButton != null)
        {
            closeButton.WasPressed -= OnCloseButtonPressed;
        }
    }
}