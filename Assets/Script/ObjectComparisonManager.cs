using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

/// <summary>
/// Manages object comparison functionality when exactly 2 objects are selected.
/// Triggers Gemini API calls to generate structured comparisons and displays results in a 3D floating panel.
/// </summary>
public class ObjectComparisonManager : MonoBehaviour
{
    [Header("Comparison Settings")]
    [Tooltip("Reference to GeminiGeneral for API calls")]
    public GeminiGeneral geminiGeneral;
    
    [Tooltip("Prefab for the 3D comparison panel")]
    public GameObject comparisonPanelPrefab;
    
    [Tooltip("Distance from center point to position panel")]
    public float panelDistance = 0.3f;
    
    [Tooltip("Panel size scaling factor")]
    public float panelScale = 1.0f;
    
    [Header("Debug Settings")]
    [Tooltip("Enable debug logging")]
    public bool enableDebugLog = false;
    
    // Current comparison state
    private ComparisonData currentComparison = null;
    private GameObject activePanelInstance = null;
    private List<SphereToggleScript> lastSelectedObjects = new List<SphereToggleScript>();
    
    // Comparison data structure matching the 2D web version
    [System.Serializable]
    public class ComparisonData
    {
        public string[] items;
        public ComparisonResult comparison;
        public Vector3 centerPosition;
    }
    
    [System.Serializable]
    public class ComparisonResult
    {
        public string[] similarities;
        public string[] differences;
        public Dictionary<string, string> functions;
        public string relationship;
        public string usage;
        public string original; // Fallback for unparsed responses
    }
    
    void Start()
    {
        if (geminiGeneral == null)
        {
            geminiGeneral = FindObjectOfType<GeminiGeneral>();
        }
        
        if (geminiGeneral == null)
        {
            Debug.LogError("ObjectComparisonManager: Could not find GeminiGeneral component!");
            enabled = false;
            return;
        }
        
        if (comparisonPanelPrefab == null)
        {
            Debug.LogError("ObjectComparisonManager: Comparison panel prefab not assigned!");
        }
        
        if (enableDebugLog) Debug.Log("ObjectComparisonManager: Initialized successfully");
    }
    
    void Update()
    {
        // Monitor selected objects and trigger comparison when exactly 2 are selected
        List<SphereToggleScript> currentSelected = SphereToggleScript.SelectedToggles.Where(t => t != null && t.isOn).ToList();
        
        // Check if selection changed
        if (!ListsEqual(currentSelected, lastSelectedObjects))
        {
            lastSelectedObjects = new List<SphereToggleScript>(currentSelected);
            
            if (currentSelected.Count == 2)
            {
                // Exactly 2 objects selected - trigger comparison
                StartCoroutine(CompareObjects(currentSelected[0], currentSelected[1]));
            }
            else
            {
                // Not exactly 2 objects - hide comparison panel
                HideComparisonPanel();
            }
        }
    }
    
    private bool ListsEqual(List<SphereToggleScript> list1, List<SphereToggleScript> list2)
    {
        if (list1.Count != list2.Count) return false;
        
        for (int i = 0; i < list1.Count; i++)
        {
            if (list1[i] != list2[i]) return false;
        }
        
        return true;
    }
    
    private IEnumerator CompareObjects(SphereToggleScript obj1, SphereToggleScript obj2)
    {
        if (obj1?.labelUnderSphere?.text == null || obj2?.labelUnderSphere?.text == null)
        {
            Debug.LogWarning("ObjectComparisonManager: One or both objects have no valid labels");
            yield break;
        }
        
        string item1 = obj1.labelUnderSphere.text;
        string item2 = obj2.labelUnderSphere.text;
        
        if (enableDebugLog) Debug.Log($"Starting comparison between: {item1} and {item2}");
        
        // Get scene context (similar to 2D version)
        string sceneContext = GetSceneContext();
        string taskContext = GetTaskContext();
        
        // Build the comparison prompt (matching 2D web version exactly)
        string prompt = $@"Given this scene context: ""{sceneContext}"",
        and the potential task: ""{taskContext}"",
        
        Compare {item1} and {item2} in a structured format. Output a JSON object with the following structure:
        {{
          ""similarities"": [""similarity 1"", ""similarity 2""],
          ""differences"": [""difference 1"", ""difference 2""],
          ""functions"": {{
            ""{item1}"": ""primary function description"",
            ""{item2}"": ""primary function description""
          }},
          ""relationship"": ""how they relate to each other in this context"",
          ""usage"": ""how they might be used together or separately""
        }}
        
        Keep each point concise (max 15 words per point).";
        
        if (enableDebugLog) Debug.Log($"Comparison prompt: {prompt}");
        
        // Call Gemini API
        yield return StartCoroutine(CallGeminiForComparison(prompt, item1, item2, obj1.transform.position, obj2.transform.position));
    }
    
    private IEnumerator CallGeminiForComparison(string prompt, string item1, string item2, Vector3 pos1, Vector3 pos2)
    {
        // Calculate center position for panel placement
        Vector3 centerPosition = (pos1 + pos2) / 2f;
        
        // Use GeminiGeneral to make the API call
        bool isComplete = false;
        string responseText = "";
        
        // Set up a callback to capture the response
        System.Action<string> onComplete = (response) => {
            responseText = response;
            isComplete = true;
        };
        
        // Make the API call using GeminiGeneral
        StartCoroutine(MakeGeminiAPICall(prompt, onComplete));
        
        // Wait for response
        while (!isComplete)
        {
            yield return null;
        }
        
        if (enableDebugLog) Debug.Log($"Comparison response: {responseText}");
        
        // Parse the response
        ComparisonResult comparisonResult = ParseComparisonResponse(responseText);
        
        // Create comparison data
        currentComparison = new ComparisonData
        {
            items = new string[] { item1, item2 },
            comparison = comparisonResult,
            centerPosition = centerPosition
        };
        
        // Show the comparison panel
        ShowComparisonPanel();
    }
    
    private IEnumerator MakeGeminiAPICall(string prompt, System.Action<string> onComplete)
    {
        if (geminiGeneral == null)
        {
            onComplete("Error: GeminiGeneral not available");
            yield break;
        }
        
        // Use GeminiGeneral's MakeGeminiRequest method
        var requestStatus = geminiGeneral.MakeGeminiRequest(prompt);
        
        // Poll for completion
        while (!requestStatus.IsCompleted)
        {
            yield return null;
        }
        
        // Check for errors
        if (requestStatus.Error != null)
        {
            Debug.LogError($"Gemini API error: {requestStatus.Error}");
            onComplete($"Error: {requestStatus.Error.Message}");
        }
        else
        {
            // Since ParseGeminiRawResponse is protected, we'll parse the response ourselves
            string parsedResponse = ParseGeminiResponse(requestStatus.Result);
            onComplete(parsedResponse);
        }
    }
    
    private string ParseGeminiResponse(string rawResponse)
    {
        try
        {
            // Parse the Gemini response structure similar to GeminiGeneral
            var responseObj = JsonConvert.DeserializeObject<GeminiGeneral.GeminiRoot>(rawResponse);
            
            if (responseObj?.candidates != null && responseObj.candidates.Count > 0 &&
                responseObj.candidates[0].content?.parts != null && responseObj.candidates[0].content.parts.Count > 0)
            {
                string textWithBackticks = responseObj.candidates[0].content.parts[0].text ?? "";
                
                // Remove code block formatting if present
                if (textWithBackticks.Contains("```json"))
                {
                    var splitted = textWithBackticks.Split(new[] { "```json" }, StringSplitOptions.None);
                    if (splitted.Length > 1)
                    {
                        var splitted2 = splitted[1].Split(new[] { "```" }, StringSplitOptions.None);
                        return splitted2[0].Trim();
                    }
                }
                
                return textWithBackticks;
            }
        }
        catch (System.Exception e)
        {
            if (enableDebugLog) Debug.LogWarning($"Error parsing Gemini raw response: {e.Message}");
        }
        
        // Fallback to original response
        return rawResponse;
    }
    
    private ComparisonResult ParseComparisonResponse(string responseText)
    {
        ComparisonResult result = new ComparisonResult();
        
        try
        {
            // Extract JSON if wrapped in markdown code blocks
            string jsonText = responseText;
            if (responseText.Contains("```"))
            {
                int startIndex = responseText.IndexOf("```json") + 7;
                if (startIndex < 7) startIndex = responseText.IndexOf("```") + 3;
                int endIndex = responseText.LastIndexOf("```");
                
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    jsonText = responseText.Substring(startIndex, endIndex - startIndex).Trim();
                }
            }
            
            // Parse JSON
            result = JsonConvert.DeserializeObject<ComparisonResult>(jsonText);
            
            if (enableDebugLog) Debug.Log("Successfully parsed comparison JSON");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Error parsing comparison JSON: {e.Message}");
            // Fallback to original text
            result.original = responseText;
        }
        
        return result;
    }
    
    private void ShowComparisonPanel()
    {
        if (currentComparison == null || comparisonPanelPrefab == null) return;
        
        // Hide existing panel first
        HideComparisonPanel();
        
        // Calculate panel position (center point offset towards camera)
        Vector3 panelPosition = currentComparison.centerPosition;
        
        // Offset panel slightly towards the camera/user
        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            Vector3 cameraDirection = (mainCamera.transform.position - panelPosition).normalized;
            panelPosition += cameraDirection * panelDistance;
        }
        
        // Instantiate the panel
        activePanelInstance = Instantiate(comparisonPanelPrefab, panelPosition, Quaternion.identity);
        activePanelInstance.transform.localScale = Vector3.one * panelScale;
        
        // Configure the panel with comparison data
        ComparisonPanel panelComponent = activePanelInstance.GetComponent<ComparisonPanel>();
        if (panelComponent != null)
        {
            panelComponent.SetComparisonData(currentComparison);
        }
        
        // Make panel face the camera
        if (mainCamera != null)
        {
            activePanelInstance.transform.LookAt(mainCamera.transform);
            activePanelInstance.transform.Rotate(0, 180, 0); // Flip to face camera correctly
        }
        
        if (enableDebugLog) Debug.Log($"Comparison panel shown at position: {panelPosition}");
    }
    
    private void HideComparisonPanel()
    {
        if (activePanelInstance != null)
        {
            Destroy(activePanelInstance);
            activePanelInstance = null;
            if (enableDebugLog) Debug.Log("Comparison panel hidden");
        }
        
        currentComparison = null;
    }
    
    private string GetSceneContext()
    {
        // Try to get scene context from SceneContextManager if available
        SceneContextManager sceneContextManager = FindObjectOfType<SceneContextManager>();
        if (sceneContextManager != null)
        {
            // You'll need to add a public method to SceneContextManager to get current scene context
            return "Current scene environment"; // Placeholder
        }
        
        return "Unknown scene";
    }
    
    private string GetTaskContext()
    {
        // Try to get task context from SceneContextManager if available
        SceneContextManager sceneContextManager = FindObjectOfType<SceneContextManager>();
        if (sceneContextManager != null)
        {
            // You'll need to add a public method to SceneContextManager to get current task context
            return "Current task context"; // Placeholder
        }
        
        return "Unknown task";
    }
    
    // Public method to force hide panel (useful for UI buttons or external calls)
    public void ForceHidePanel()
    {
        HideComparisonPanel();
    }
    
    // Public method to get current comparison state
    public ComparisonData GetCurrentComparison()
    {
        return currentComparison;
    }
}