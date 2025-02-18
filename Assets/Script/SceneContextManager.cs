using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using Newtonsoft.Json;

/// <summary>
/// Periodically analyzes the scene using Gemini to identify possible tasks and scene context.
/// </summary>
public class SceneContextManager : GeminiGeneral
{
    [Header("Analysis Settings")]
    [Tooltip("Time between scene analysis calls (in seconds)")]
    public float analysisPeriod = 10f;

    [Tooltip("Whether the analyzer is currently running")]
    public bool isAnalyzing = true;

    [Header("Optional References")]
    [Tooltip("SceneObjectManager to get current objects in scene")]
    public SceneObjectManager sceneManager;

    [Header("Current Analysis Results")]
    [SerializeField, ReadOnly]
    private string currentSceneType = "Not analyzed yet";
    
    [SerializeField, ReadOnly]
    private string[] currentPossibleTasks = new string[0];
    
    [SerializeField, ReadOnly]
    private string[] currentRelevantObjects = new string[0];
    
    [SerializeField, ReadOnly]
    private string lastAnalysisTime = "Never";

    // Event that other components can subscribe to
    public System.Action<SceneContext> OnSceneContextComplete;

    private SceneContext currentAnalysis;

    private void Start()
    {
        StartCoroutine(PeriodicAnalysisRoutine());
    }

    private IEnumerator PeriodicAnalysisRoutine()
    {
        while (true)
        {
            if (isAnalyzing)
            {
                yield return StartCoroutine(AnalyzeSceneRoutine());
            }
            yield return new WaitForSeconds(analysisPeriod);
        }
    }

    private IEnumerator AnalyzeSceneRoutine()
    {
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);

        // 2) Convert to base64
        string base64Image = ConvertTextureToBase64(frameTex);

        // 3) Build context-aware prompt
        string contextPrompt = BuildContextAwarePrompt();

        // 4) Call Gemini
        var request = geminiClient.GenerateContent(contextPrompt, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        string response = request.Result;

        // 5) Parse response
        SceneContext analysis = ParseAnalysisResponse(response);
        
        // 6) Update inspector and notify subscribers
        HandleSceneAnalysis(analysis);
        if (analysis != null)
        {
            OnSceneContextComplete?.Invoke(analysis);
            // Debug.Log($"Scene Analysis Complete - Detected Scene: {analysis.sceneType}\n" +
            //          $"Possible Tasks: {string.Join(", ", analysis.possibleTasks)}");
        }

        // Clean up
        Destroy(frameTex);
    }

    private string BuildContextAwarePrompt()
    {
        string objectContext = "";
        if (sceneManager != null)
        {
            var anchors = sceneManager.GetAllAnchors();
            if (anchors != null && anchors.Count > 0)
            {
                objectContext = "Currently detected objects: " + 
                              string.Join(", ", anchors.ConvertAll(a => a.label));
            }
        }

        return $"Analyze this scene and provide a JSON response with the following structure:\n" +
               "{\n" +
               "  \"sceneType\": \"[kitchen/office/living room/etc]\",\n" +
               "  \"possibleTasks\": [\"task1\", \"task2\", ...],\n" +
               "  \"relevantObjects\": [\"object1\", \"object2\", ...]\n" +
               "}\n\n" +
               $"{objectContext}\n" +
               "Focus on practical tasks that could be performed in this environment. " +
               "Consider object relationships and common activities in this type of space.";
    }

    private SceneContext ParseAnalysisResponse(string response)
    {
        try
        {
            string jsonText = ParseGeminiRawResponse(response);
            if (string.IsNullOrEmpty(jsonText)) return null;

            return JsonConvert.DeserializeObject<SceneContext>(jsonText);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error parsing scene analysis response: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Manually trigger a scene analysis outside the periodic routine
    /// </summary>
    public void TriggerAnalysis()
    {
        StartCoroutine(AnalyzeSceneRoutine());
    }

    private void HandleSceneAnalysis(SceneContext analysis)
    {
        currentAnalysis = analysis;
        
        if (analysis != null)
        {
            // Update inspector-visible fields
            currentSceneType = analysis.sceneType ?? "Unknown";
            currentPossibleTasks = analysis.possibleTasks?.ToArray() ?? new string[0];
            currentRelevantObjects = analysis.relevantObjects?.ToArray() ?? new string[0];
            lastAnalysisTime = System.DateTime.Now.ToString("HH:mm:ss");
        }
        else
        {
            currentAnalysis = null;
            currentSceneType = "Analysis failed";
            currentPossibleTasks = new string[0];
            currentRelevantObjects = new string[0];
        }
    }

    public SceneContext GetCurrentAnalysis()
    {
        return currentAnalysis;
    }
}

[System.Serializable]
public class SceneContext
{
    public string sceneType;
    public List<string> possibleTasks;
    public List<string> relevantObjects;
}

public class ReadOnlyAttribute : PropertyAttribute { }

#if UNITY_EDITOR
namespace UnityEditor
{
    [CustomPropertyDrawer(typeof(ReadOnlyAttribute))]
    public class ReadOnlyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            GUI.enabled = false;
            EditorGUI.PropertyField(position, property, label, true);
            GUI.enabled = true;
        }
    }
}
#endif 