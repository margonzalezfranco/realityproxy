using UnityEngine;
using UnityEngine.UI; 
using TMPro;
using PolySpatial.Template;
using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;

/// <summary>
/// Script attached to each sphere toggled in the scene. 
/// It calls Gemini to (A) generate questions about the object, and (B) show relationships with other items.
/// </summary>
public class SphereToggleScript : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The Toggle component on this sphere.")]
    [SerializeField]
    private SpatialUIToggle spatialUIToggle;

    [Tooltip("TextMeshPro label that holds the sphere's 'name' or 'content'.")]
    public TextMeshPro labelUnderSphere;

    [Tooltip("Reference to the scene's menu. We call SetMenuTitle(...) on it.")]
    public MenuScript menuScript;

    public GameObject InfoPanel;

    // -----------------------------
    // New Fields for Gemini Re-Call
    // -----------------------------
    [Header("Gemini Re-Call Settings")]
    [Tooltip("Your Gemini model name, e.g. 'gemini-2.0-flash-exp'")]
    public string modelName = "gemini-2.0-flash-exp";

    [Tooltip("Your API key")]
    public string geminiApiKey = "AIzaSyAoYU7ZM-AImpfA0faIBBz8ovLb_7n0QF4";

    [Tooltip("A reference to your Gemini API client script. Make sure it's initialized.")]
    public GeminiAPI geminiClient;

    [Tooltip("A RenderTexture from the camera feed (like a VisionPro or other XR camera).")]
    public RenderTexture cameraRenderTex;

    [Tooltip("Parent transform/container for the newly created question lines.")]
    [HideInInspector]
    public Transform questionsParent;

    [Tooltip("Prefab that displays each question. Should have a TextMeshProUGUI or Text inside.")]
    public GameObject questionPrefab;

    [Header("Question Answering")]
    [Tooltip("Reference to the GeminiQuestionAnswerer component")]
    public GeminiQuestionAnswerer questionAnswerer;

    public GameObject answerPanel;

    [Header("Level 2 Relationship")]
    [Tooltip("Manager that draws lines between related items.")]
    public RelationshipLineManager relationLineManager;

    [Tooltip("Manager that tracks all recognized objects in the scene.")]
    public SceneObjectManager sceneObjManager;

    private bool isOn = false;

    private void Start()
    {
        if (geminiClient == null)
        {
            geminiClient = new GeminiAPI(modelName, geminiApiKey);
        }

        // Subscribe to the toggle's onValueChanged event
        if (spatialUIToggle != null)
        {
            spatialUIToggle.m_ToggleChanged.AddListener(OnSphereToggled);
        }
    }

    private void OnSphereToggled(bool toggledOn)
    {
        isOn = toggledOn;

        if (isOn)
        {
            InfoPanel.SetActive(true);

            // We just toggled ON this sphere: tell the menu to update the title
            if (labelUnderSphere != null)
            {
                string labelContent = labelUnderSphere.text;
                menuScript.SetMenuTitle(labelContent);

                // 1) Generate possible user questions for this object (Granularity Lv1-style)
                StartCoroutine(GenerateQuestionsRoutine(labelContent));

                // 2) Also generate relationships with other items (Granularity Lv2)
                StartCoroutine(GenerateRelationshipsRoutine(labelContent));
            }
        }
        else
        {
            // Turn OFF
            InfoPanel.SetActive(false);
            answerPanel.SetActive(false);

            // Clear any existing relationship lines
            if (relationLineManager != null)
            {
                relationLineManager.ClearAllLines();
            }
        }
    }

    /// <summary>
    /// (Granularity Lv1) Coroutine that captures the camera frame, sends it to Gemini,
    /// parses a JSON array of user questions, and spawns UI lines for each question.
    /// </summary>
    private IEnumerator GenerateQuestionsRoutine(string labelContent)
    {
        // 1) Capture the camera frame -> Base64
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        string base64Image = ConvertTextureToBase64(frameTex);
        Destroy(frameTex);  // free the temporary texture

        // 2) Build a simple prompt that references the label Content
        //    "Ask up to 5 questions about this item"
        string prompt = $@"
            You are provided an image (inline data) plus the label: '{labelContent}'.
            Please return a JSON list of possible user questions about this product/item.
            Return only the most likely questions, up to 5 maximum.
            In the format:
            json
            [
            ""Question 1"",
            ""Question 2"",
            ...
            ]
            ";

        // 3) Call Gemini
        var requestTask = geminiClient.GenerateContent(prompt, base64Image);

        while (!requestTask.IsCompleted)
            yield return null;

        string geminiResponse = requestTask.Result;
        Debug.Log("Gemini Questions Response:\n" + geminiResponse);

        // 4) Extract JSON
        string extractedJson = TryExtractJson(geminiResponse);
        Debug.Log("Gemini Questions Response - Extracted JSON:\n" + extractedJson);

        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("Could not find valid JSON block in Gemini question response.");
            yield break;
        }

        // This is our final array of question strings
        List<string> questionsList = null;
        try
        {
            questionsList = JsonConvert.DeserializeObject<List<string>>(extractedJson);
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to parse question array: " + e);
            yield break;
        }

        // 5) Instantiate UI elements for each question
        ClearPreviousQuestions();

        if (questionsList != null && questionsList.Count > 0)
        {
            float currentY = -60f;  // Start at the top
            float questionHeight = 60f;  // Height of each question block, adjust as needed
            float spacing = 5f;  // Space between questions

            foreach (var q in questionsList)
            {
                // Instantiate your question prefab 
                var go = Instantiate(questionPrefab, questionsParent);
                go.name = "GeminiQuestion";

                // Position 
                Transform t = go.transform;
                if (t != null)
                {
                    t.localPosition = new Vector3(0f, -currentY, 0f);
                    currentY += questionHeight + spacing;
                }

                // Set the text inside
                TextMeshPro txt = go.GetComponentInChildren<TextMeshPro>();
                if (txt != null) txt.text = q;

                // Add button press handling
                var button = go.GetComponent<SpatialUIButton>();
                if (button != null)
                {
                    string questionText = q; // closure
                    button.WasPressed += (buttonText, renderer, index) =>
                    {
                        if (questionAnswerer != null)
                        {
                            questionAnswerer.RequestAnswer(questionText);
                            answerPanel.SetActive(true);
                        }
                        else
                        {
                            Debug.LogWarning("No QuestionAnswerer reference set.");
                        }
                    };
                }
                else
                {
                    Debug.LogWarning("Question prefab is missing SpatialUIButton component.");
                }
            }
        }
    }

    /// <summary>
    /// (Granularity Lv2) Coroutine that calls Gemini to find relationships among scene items,
    /// draws lines from the toggled object to each related item.
    /// </summary>
    private IEnumerator GenerateRelationshipsRoutine(string inHandLabel)
    {
        // 1) Gather all recognized anchors from sceneObjManager
        var anchors = sceneObjManager.GetAllAnchors();
        List<string> itemLabels = new List<string>();
        foreach (var a in anchors)
        {
            itemLabels.Add(a.label);
        }
        // remove the "in-hand" label so it doesn't appear in the "others"
        itemLabels.Remove(inHandLabel);

        // 2) Build the prompt
        string prompt = $@"
        Given the user is currently holding: {inHandLabel}.
        Other objects in the scene: {string.Join(", ", itemLabels)}.

        Please return a JSON object where each key is one of the above items 
        and each value is a short relationship to '{inHandLabel}' (max 5 words).
        For example:
        {{ ""milk"": ""added to coffee mug"" }}
        Return an empty object if nothing is relevant.
        ";

        // 3) Call Gemini (not using an image here, but you could if relevant)
        var requestTask = geminiClient.GenerateContent(prompt, null);
        
        while (!requestTask.IsCompleted)
            yield return null;

        string rawResponse = requestTask.Result;
        Debug.Log($"Relationships raw response:\n{rawResponse}");

        // 4) Extract JSON portion
        string extractedJson = TryExtractJson(rawResponse);
        Debug.Log("Relationships - Extracted JSON:\n" + extractedJson);

        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("No valid JSON found in relationships response.");
            yield break;
        }

        // 5) Parse to dictionary
        Dictionary<string, string> relationshipsDict = null;
        try
        {
            relationshipsDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(extractedJson);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Failed to parse relationships JSON: " + e);
        }

        if (relationshipsDict == null || relationshipsDict.Count == 0)
        {
            Debug.Log($"No relationships found for '{inHandLabel}'. Possibly no relevant items.");
            yield break;
        }

        // 6) Show lines from this anchor to each related anchor
        var myAnchor = sceneObjManager.GetAnchorByLabel(inHandLabel);
        if (myAnchor == null)
        {
            Debug.LogWarning($"No anchor found for label '{inHandLabel}'?!");
            yield break;
        }
        relationLineManager.ShowRelationships(myAnchor, relationshipsDict, anchors);
    }

    /// <summary>
    /// Example helper to extract the JSON portion from the Gemini response 
    /// which might contain ```json ...```.
    /// Adjust to match your actual response format.
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
                var splitted = rawText.Split(new[] { "```json" }, StringSplitOptions.None);
                if (splitted.Length > 1)
                {
                    var splitted2 = splitted[1].Split(new[] { "```" }, StringSplitOptions.None);
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

    [Serializable]
    public class GeminiRoot
    {
        public List<Candidate> candidates;
    }

    [Serializable]
    public class Candidate
    {
        public Content content;
    }

    [Serializable]
    public class Content
    {
        public List<Part> parts;
    }

    [Serializable]
    public class Part
    {
        public string text;
    }

    private void ClearPreviousQuestions()
    {
        if (questionsParent == null) return;

        foreach (Transform child in questionsParent)
        {
            if (child.name == "GeminiQuestion")
            {
                Destroy(child.gameObject);
            }
        }
    }

    // -------------------------------------------------------
    // Methods to capture the camera feed & convert to Base64
    // -------------------------------------------------------
    private Texture2D CaptureFrame(RenderTexture rt)
    {
        if (rt == null)
        {
            Debug.LogWarning("No cameraRenderTex assigned.");
            return null;
        }
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        return tex;
    }

    private string ConvertTextureToBase64(Texture2D tex)
    {
        if (tex == null) return null;
        var bytes = tex.EncodeToPNG();
        return Convert.ToBase64String(bytes);
    }
}
