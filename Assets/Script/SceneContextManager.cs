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

    [Header("User Study Logging")]
    [SerializeField] private bool enableUserStudyLogging = true;

    // Event that other components can subscribe to
    public System.Action<SceneContext> OnSceneContextComplete;

    private SceneContext currentAnalysis;
    private int analysisCount = 0;

    private void Start()
    {
        LogUserStudy($"SCENE_CONTEXT_MANAGER_STARTED: Period={analysisPeriod}s, AutoAnalysis={isAnalyzing}");
        StartCoroutine(PeriodicAnalysisRoutine());
    }

    private IEnumerator PeriodicAnalysisRoutine()
    {
        while (true)
        {
            if (isAnalyzing)
            {
                LogUserStudy("PERIODIC_ANALYSIS_TRIGGERED: Automatic=true");
                yield return StartCoroutine(AnalyzeSceneRoutine());
            }
            yield return new WaitForSeconds(analysisPeriod);
        }
    }

    private IEnumerator AnalyzeSceneRoutine()
    {
        analysisCount++;
        int currentAnalysisId = analysisCount;
        LogUserStudy($"SCENE_ANALYSIS_STARTED: ID={currentAnalysisId}");
        
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        LogUserStudy($"FRAME_CAPTURED: ID={currentAnalysisId}, Resolution=\"{frameTex.width}x{frameTex.height}\"");

        // 2) Convert to base64
        string base64Image = ConvertTextureToBase64(frameTex);
        LogUserStudy($"IMAGE_ENCODED: ID={currentAnalysisId}, Size={base64Image.Length} bytes");

        // 3) Build context-aware prompt
        string contextPrompt = BuildContextAwarePrompt();
        int detectedObjectCount = 0;
        if (sceneManager != null)
        {
            var anchors = sceneManager.GetAllAnchors();
            detectedObjectCount = anchors?.Count ?? 0;
        }
        LogUserStudy($"CONTEXT_PROMPT_BUILT: ID={currentAnalysisId}, DetectedObjects={detectedObjectCount}");

        // 4) Call Gemini
        // This now uses the new RequestStatus system which supports concurrent API calls
        // from multiple components without blocking or interfering with each other
        LogUserStudy($"GEMINI_API_CALL_STARTED: ID={currentAnalysisId}");
        var startTime = Time.realtimeSinceStartup;
        
        var request = MakeGeminiRequest(contextPrompt, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        string response = request.Result;
        
        float duration = Time.realtimeSinceStartup - startTime;
        LogUserStudy($"GEMINI_API_CALL_COMPLETED: ID={currentAnalysisId}, Duration={duration:F2}s, ResponseLength={response.Length}");

        // 5) Parse response
        SceneContext analysis = ParseAnalysisResponse(response);
        bool parseSuccess = analysis != null;
        LogUserStudy($"SCENE_ANALYSIS_PARSED: ID={currentAnalysisId}, Success={parseSuccess}");
        
        // 6) Update inspector and notify subscribers
        HandleSceneAnalysis(analysis);
        if (analysis != null)
        {
            int taskCount = analysis.possibleTasks?.Count ?? 0;
            int objectCount = analysis.relevantObjects?.Count ?? 0;
            
            LogUserStudy($"SCENE_ANALYSIS_COMPLETED: ID={currentAnalysisId}, SceneType=\"{analysis.sceneType}\", TaskCount={taskCount}, ObjectCount={objectCount}");
            
            OnSceneContextComplete?.Invoke(analysis);
            LogUserStudy($"CONTEXT_EVENT_INVOKED: Listeners={OnSceneContextComplete?.GetInvocationList().Length ?? 0}");
        }
        else
        {
            LogUserStudy($"SCENE_ANALYSIS_FAILED: ID={currentAnalysisId}");
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
            if (string.IsNullOrEmpty(jsonText))
            {
                LogUserStudy($"PARSING_FAILED: Reason=\"empty_response\"");
                return null;
            }

            return JsonConvert.DeserializeObject<SceneContext>(jsonText);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error parsing scene analysis response: {ex}");
            LogUserStudy($"PARSING_ERROR: Exception=\"{ex.GetType().Name}\", Message=\"{ex.Message}\"");
            return null;
        }
    }

    /// <summary>
    /// Manually trigger a scene analysis outside the periodic routine
    /// </summary>
    public void TriggerAnalysis()
    {
        LogUserStudy("MANUAL_ANALYSIS_TRIGGERED: User=true");
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
            
            LogUserStudy($"SCENE_CONTEXT_UPDATED: SceneType=\"{currentSceneType}\", Tasks={currentPossibleTasks.Length}, Objects={currentRelevantObjects.Length}");
        }
        else
        {
            currentAnalysis = null;
            currentSceneType = "Analysis failed";
            currentPossibleTasks = new string[0];
            currentRelevantObjects = new string[0];
            
            LogUserStudy("SCENE_CONTEXT_UPDATE_FAILED");
        }
    }

    public SceneContext GetCurrentAnalysis()
    {
        return currentAnalysis;
    }
    
    // Helper method for creating timestamped user study logs
    private void LogUserStudy(string message)
    {
        if (!enableUserStudyLogging) return;
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
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