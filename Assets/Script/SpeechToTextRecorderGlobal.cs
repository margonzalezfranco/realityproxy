using System;
using UnityEngine.XR.Hands;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if !UNITY_VISIONOS
using Microsoft.CSharp; // Required for dynamic type binding
#endif
using Newtonsoft.Json; // Added for JSON parsing
using Newtonsoft.Json.Linq; // Added for JObject
using PolySpatial.Template; // Added for SceneObjectManager etc.
using System.Linq;
using UnityEngine.XR.Interaction.Toolkit.UI;
using System.Text;
public class SpeechToTextRecorderGlobal : SpeechToTextRecorder
{
    public override void TalkToGemini(string userQuery)
    {
        string finalPrompt;
        string contextType = "ENVIRONMENT_LEVEL";
        string currentSceneContext = "unknown environment";
        List<string> itemLabels = new List<string>();
        string objectListString = "none";

        // Get Scene Context
        if (sceneContextManager != null && sceneContextManager.GetCurrentAnalysis() != null)
        {
            var analysis = sceneContextManager.GetCurrentAnalysis();
            currentSceneContext = analysis.sceneType ?? "unknown environment";
            // Could potentially add task context here too if needed later
        }

        // Get Object List
        if (sceneObjectManager != null)
        {
            var anchors = sceneObjectManager.GetAllAnchors();
            foreach (var a in anchors)
            {
                itemLabels.Add(a.label);
            }
            if (itemLabels.Count > 0)
            {
                objectListString = string.Join(", ", itemLabels);
            }
        }
        
        // Log context for user study
        LogUserStudy($"[VOICE_INPUT] [ENV] CONTEXT: Type=\"{contextType}\", SceneType=\"{currentSceneContext}\", Objects=\"{objectListString}\"");
        
        // Format the Global Prompt
        finalPrompt = string.Format(environmentLevelPromptTemplate, currentSceneContext, objectListString, userQuery);
        Debug.Log($"[SpeechRecorder] Using ENVIRONMENT context prompt.");
        Debug.Log($"[SpeechRecorder] Scene: {currentSceneContext}, Objects: {objectListString}");

        Debug.Log($"[SpeechRecorder] Final prompt: {finalPrompt}");

        // Use MakeGeminiRequest for concurrency management inherited from GeminiGeneral
        var request = MakeGeminiRequest(finalPrompt, null); // Assuming no image needed for these prompts

        // Start the coroutine to wait for the response and handle it
        StartCoroutine(GeminiQueryRoutine(request, false, userQuery));
    }
}