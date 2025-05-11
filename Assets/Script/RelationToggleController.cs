using UnityEngine;
using PolySpatial.Template;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Newtonsoft.Json;
using System.Text;

/// <summary>
/// Controller for the RelationToggle UI element.
/// This script handles toggling relationship lines between objects in the scene.
/// Attach to the RelationToggle GameObject.
/// </summary>
public class RelationToggleController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Reference to the SpatialUIToggle component on this GameObject")]
    public SpatialUIToggle toggle;

    [Tooltip("Reference to the Sphere Toggle Script that owns this toggle")]
    public SphereToggleScript ownerSphereToggle;

    [Tooltip("Reference to the relationship line manager that draws the lines")]
    public RelationshipLineManager relationshipLineManager;

    [Tooltip("Manager that tracks all recognized objects in the scene")]
    public SceneObjectManager sceneObjectManager;

    [Header("Gemini API Settings")]
    [Tooltip("Your Gemini model name, e.g. 'gemini-2.0-flash'")]
    public string modelName = "gemini-2.0-flash";

    [Tooltip("Your API key")]
    public string geminiApiKey = "AIzaSyAoYU7ZM-AImpfA0faIBBz8ovLb_7n0QF4";

    [Tooltip("Reference to a GeminiAPI client script. Make sure it's initialized.")]
    public GeminiAPI geminiClient;

    [Tooltip("Reference to a GeminiGeneral component to use for API requests")]
    public GeminiGeneral geminiGeneral;

    // Keep track of toggle state
    private bool relationLinesAreActive = false;

    // Reference to active coroutines
    private Coroutine activeGenerationCoroutine;

    [Header("User Study Logging")]
    [SerializeField] private bool enableUserStudyLogging = true;

    // Public property to access owner's isOn state
    public bool OwnerIsActive => ownerSphereToggle != null && ownerSphereToggle.isOn;

    private void Awake()
    {
        // Find components if not assigned
        if (toggle == null)
        {
            toggle = GetComponent<SpatialUIToggle>();
        }

        if (relationshipLineManager == null)
        {
            relationshipLineManager = FindObjectOfType<RelationshipLineManager>();
        }

        if (sceneObjectManager == null)
        {
            sceneObjectManager = FindObjectOfType<SceneObjectManager>();
        }

        if (geminiGeneral == null)
        {
            geminiGeneral = FindObjectOfType<GeminiGeneral>();
        }
    }

    private void Start()
    {
        if (geminiClient == null)
        {
            geminiClient = new GeminiAPI(modelName, geminiApiKey);
        }

        // Set up the toggle listener
        if (toggle != null)
        {
            // Clear any existing listeners to avoid duplicates
            toggle.m_ToggleChanged.RemoveAllListeners();
            // Add listener to control the relationship lines
            toggle.m_ToggleChanged.AddListener(ToggleRelationshipLines);
            
            // Initialize toggle to off state - relationships start hidden
            if (toggle.m_Active)
            {
                toggle.PassiveToggleWithoutInvokeOff();
            }
        }
    }

    public void UpdateOwner(SphereToggleScript newOwner)
    {
        if (newOwner == ownerSphereToggle) return;
        
        if (ownerSphereToggle != null && relationLinesAreActive)
        {
            if (relationshipLineManager != null)
            {
                relationshipLineManager.ClearAllLines(true);
            }
            
            if (activeGenerationCoroutine != null)
            {
                StopCoroutine(activeGenerationCoroutine);
                activeGenerationCoroutine = null;
            }
        }
        
        ownerSphereToggle = newOwner;
        
        if (relationLinesAreActive && ownerSphereToggle != null && ownerSphereToggle.labelUnderSphere != null)
        {
            string labelContent = ownerSphereToggle.labelUnderSphere.text;
            activeGenerationCoroutine = StartCoroutine(GenerateRelationshipsRoutine(labelContent));
        }
    }
    
    private void OnTransformParentChanged()
    {
        if (transform.parent != null)
        {
            SphereToggleScript newOwner = transform.parent.GetComponent<SphereToggleScript>();
            if (newOwner != null && newOwner != ownerSphereToggle)
            {
                UpdateOwner(newOwner);
            }
        }
        else
        {
            if (relationLinesAreActive)
            {
                if (relationshipLineManager != null)
                {
                    relationshipLineManager.ClearAllLines(true);
                }
                
                if (toggle != null && toggle.m_Active)
                {
                    toggle.PassiveToggleWithoutInvokeOff();
                    relationLinesAreActive = false;
                }
                
                if (activeGenerationCoroutine != null)
                {
                    StopCoroutine(activeGenerationCoroutine);
                    activeGenerationCoroutine = null;
                }
            }
        }
    }

    private void OnDestroy()
    {
        // Clean up listeners
        if (toggle != null)
        {
            toggle.m_ToggleChanged.RemoveListener(ToggleRelationshipLines);
        }

        // Stop any active coroutines
        if (activeGenerationCoroutine != null)
        {
            StopCoroutine(activeGenerationCoroutine);
            activeGenerationCoroutine = null;
        }
    }

    /// <summary>
    /// Toggles the visibility of relationship lines in the scene.
    /// </summary>
    /// <param name="showLines">True to show relationship lines, false to hide them</param>
    public void ToggleRelationshipLines(bool showLines)
    {
        bool stateActuallyChanged = relationLinesAreActive != showLines;
        relationLinesAreActive = showLines;

        if (showLines)
        {
            Debug.Log("ToggleRelationshipLines: showLines = true");
            
            // Only generate relationships if we have a valid owner sphere toggle that is active
            if (OwnerIsActive && ownerSphereToggle.labelUnderSphere != null)
            {
                if (stateActuallyChanged) // Being turned ON
                {
                    // Handle exclusivity with other toggles
                    HandleToggleExclusivity();
                }

                string labelContent = ownerSphereToggle.labelUnderSphere.text;
                
                // Stop any existing relationship generation coroutine
                if (activeGenerationCoroutine != null)
                {
                    StopCoroutine(activeGenerationCoroutine);
                    activeGenerationCoroutine = null;
                }
                
                // Generate relationships with other items
                activeGenerationCoroutine = StartCoroutine(GenerateRelationshipsRoutine(labelContent));
                
                // Log the action
                LogUserStudy($"[RELATION] RELATIONSHIP_LINES_SHOWN: Object=\"{labelContent}\"");
            }
        }
        else
        {
            // Clear any existing relationship lines
            if (relationshipLineManager != null)
            {
                relationshipLineManager.ClearAllLines(true);
                
                // Log the action if state changed to OFF
                if (ownerSphereToggle != null && ownerSphereToggle.labelUnderSphere != null && stateActuallyChanged)
                {
                    LogUserStudy($"[RELATION] RELATIONSHIP_LINES_HIDDEN: Object=\"{ownerSphereToggle.labelUnderSphere.text}\"");
                }
            }
        }
    }

    /// <summary>
    /// Ensure only one toggle function is active at a time
    /// </summary>
    private void HandleToggleExclusivity()
    {
        if (ownerSphereToggle == null) return;

        // Deactivate Question Toggle if it's active
        if (ownerSphereToggle.InfoPanel != null && 
            ownerSphereToggle.InfoPanel.activeSelf && 
            ownerSphereToggle.questionToggle != null)
        {
            // Always simulate the toggle press to ensure visual state is updated
            SpatialUIToggle qt = ownerSphereToggle.questionToggle.GetComponent<SpatialUIToggle>();
            if (qt != null)
            {
                Debug.Log("Simulating questionToggle press to turn it off");
                qt.PressStart();
                qt.PressEnd();
            }
            else
            {
                // If for some reason we can't find the toggle, fall back to using the controller directly
                var infoController = ownerSphereToggle.questionToggle.GetComponent<InfoPanelToggleController>();
                if (infoController != null)
                {
                    Debug.Log("Falling back to direct InfoPanelToggleController call");
                    infoController.SetInfoPanelVisibility(false);
                }
            }
        }

        // Deactivate Recorder Toggle if it's active
        if (IsRecorderOn() && ownerSphereToggle.recorderToggle != null)
        {
            SpatialUIToggle recT = ownerSphereToggle.recorderToggle.GetComponent<SpatialUIToggle>();
            if (recT != null)
            {
                recT.PressStart(); // This will trigger OnRecorderFunctionToggleChanged(false)
                recT.PressEnd();
            }
        }
    }

    /// <summary>
    /// Coroutine that calls Gemini to find relationships among scene items,
    /// draws lines from the toggled object to each related item.
    /// </summary>
    private IEnumerator GenerateRelationshipsRoutine(string inHandLabel)
    {
        // Check for required components
        if (sceneObjectManager == null || relationshipLineManager == null || ownerSphereToggle == null)
        {
            Debug.LogError("Missing required components for generating relationships");
            yield break;
        }

        // 1) Gather all recognized anchors from sceneObjManager
        var anchors = sceneObjectManager.GetAllAnchors();
        List<string> itemLabels = new List<string>();
        foreach (var a in anchors)
        {
            itemLabels.Add(a.label);
        }
        // remove the "in-hand" label so it doesn't appear in the "others"
        itemLabels.Remove(inHandLabel);

        // Get context information from SphereToggleScript
        string currentSceneContext = "unknown environment";
        string currentTaskContext = "no specific task";
        
        if (ownerSphereToggle.sceneContextManager != null && 
            ownerSphereToggle.sceneContextManager.GetCurrentAnalysis() != null)
        {
            var analysis = ownerSphereToggle.sceneContextManager.GetCurrentAnalysis();
            currentSceneContext = analysis.sceneType ?? "unknown environment";
            if (analysis.possibleTasks != null && analysis.possibleTasks.Count > 0)
            {
                currentTaskContext = string.Join(", ", analysis.possibleTasks);
            }
        }
        
        string prompt = $@"
        Given this scene context: {currentSceneContext},
        the potential tasks: {currentTaskContext},
        and that the user is holding / selecting this item: {inHandLabel},

        Find objects that are most related to this {inHandLabel} in the current scene, considering:
        1. The overall scene context and task
        2. Spatial relationships
        3. Functional relationships in the context of the task
        4. Common usage patterns

        Reminder: Don't include unrelated items in the output which are not related to the current task. It should be functionally related to the {inHandLabel}.

        Choose only from these detected items: {string.Join(", ", itemLabels)}.

        Output a JSON object where each key is a related object and its value is a brief relationship description (max 5 words).
        Example format:
        {{
          ""object1"": ""used together for cooking"",
          ""object2"": ""located next to item"",
          ""object3"": ""complements main task""
        }}

        if you don't find any meaningful relationships between the {inHandLabel} and other items in the current scene, return an empty JSON object:
        {{}}
        ";

        Debug.Log("Relationships prompt:\n" + prompt);

        // Call Gemini using the MakeGeminiRequest method from GeminiGeneral for concurrent API calls
        var request = geminiGeneral != null 
            ? geminiGeneral.MakeGeminiRequest(prompt, null)
            : new GeminiGeneral.RequestStatus(geminiClient.GenerateContent(prompt, null));
        
        while (!request.IsCompleted)
            yield return null;

        string rawResponse = request.Result;

        // Extract JSON portion
        string extractedJson = TryExtractJson(rawResponse);
        Debug.Log("Relationships - Extracted JSON:\n" + extractedJson);

        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("No valid JSON found in relationships response.");
            
            // Log failure to find relationships for user study
            LogUserStudy($"[ENV] [OBJECT_RELATIONSHIPS] RELATIONSHIPS_GENERATION_FAILED: Object=\"{inHandLabel}\"");
            
            yield break;
        }

        // Parse to dictionary
        Dictionary<string, string> relationshipsDict = null;
        try
        {
            relationshipsDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(extractedJson);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Failed to parse relationships JSON: " + e);
        }

        // Handle empty or null relationships
        if (relationshipsDict == null || relationshipsDict.Count == 0)
        {
            Debug.Log($"No meaningful relationships found for '{inHandLabel}' in the current context.");
            
            // Log no relationships found for user study
            LogUserStudy($"[ENV] [OBJECT_RELATIONSHIPS] NO_RELATIONSHIPS_FOUND: Object=\"{inHandLabel}\"");
            
            // Clear any existing relationship lines since there are no relationships
            if (relationshipLineManager != null)
            {
                relationshipLineManager.ClearAllLines();
            }

            yield break;
        }

        // Log relationships found for user study
        StringBuilder relationshipSb = new StringBuilder();
        foreach (var kvp in relationshipsDict)
        {
            relationshipSb.Append($"{kvp.Key}=\"{kvp.Value}\" | ");
        }
        LogUserStudy($"[ENV] [OBJECT_RELATIONSHIPS] RELATIONSHIPS_FOUND: Object=\"{inHandLabel}\", Count={relationshipsDict.Count}, Relationships=\"{relationshipSb.ToString().TrimEnd(' ', '|')}\"");
        
        // Get the anchor for this sphere
        var myAnchor = sceneObjectManager.GetAnchorByGameObject(ownerSphereToggle.gameObject);
        if (myAnchor == null)
        {
            Debug.LogWarning($"No anchor found for this sphere GameObject!");
            yield break;
        }
        
        // When relationships are generated from manually toggling a sphere, 
        // we set enableTimeout to false to prevent auto-clearing
        relationshipLineManager.ShowRelationships(myAnchor, relationshipsDict, anchors, false);
    }

    /// <summary>
    /// Example helper to extract the JSON portion from the Gemini response
    /// </summary>
    private string TryExtractJson(string fullResponse)
    {
        try
        {
            var root = JsonConvert.DeserializeObject<GeminiRoot>(fullResponse);
            if (root?.candidates == null || root.candidates.Count == 0)
                return null;

            string rawText = root.candidates[0].content.parts[0].text;
            if (string.IsNullOrEmpty(rawText)) 
                return null;

            if (rawText.Contains("```json"))
            {
                var splitted = rawText.Split(new[] { "```json" }, System.StringSplitOptions.None);
                if (splitted.Length > 1)
                {
                    var splitted2 = splitted[1].Split(new[] { "```" }, System.StringSplitOptions.None);
                    rawText = splitted2[0].Trim();
                }
            }
            return rawText;
        }
        catch
        {
            // fallback: raw entire text as-is
            return fullResponse;
        }
    }

    [System.Serializable]
    public class GeminiRoot
    {
        public List<Candidate> candidates;
    }

    [System.Serializable]
    public class Candidate
    {
        public Content content;
    }

    [System.Serializable]
    public class Content
    {
        public List<Part> parts;
    }

    [System.Serializable]
    public class Part
    {
        public string text;
    }

    // Helper method for IsRecorderOn
    private bool IsRecorderOn()
    {
        if (ownerSphereToggle == null || ownerSphereToggle.recorderToggle == null) return false;
        
        var recorderToggle = ownerSphereToggle.recorderToggle;
        SpeechToTextRecorder recorderComponent = recorderToggle.GetComponent<SpeechToTextRecorder>();
        if (recorderComponent == null && recorderToggle.transform.parent != null)
        {
            recorderComponent = recorderToggle.transform.parent.GetComponent<SpeechToTextRecorder>();
        }
        if (recorderComponent != null)
        {
            var isRecordingField = recorderComponent.GetType().GetField("isRecording",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            if (isRecordingField != null)
            {
                try { // Add try-catch for safety if field access fails
                    return (bool)isRecordingField.GetValue(recorderComponent);
                } catch (System.Exception ex) {
                    Debug.LogError($"Error accessing isRecording field: {ex.Message}");
                    return false;
                }
            }
        }
        return false; // Default if cannot determine
    }

    // Helper method for creating timestamped user study logs
    private void LogUserStudy(string message)
    {
        if (!enableUserStudyLogging) return;
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
    }
} 