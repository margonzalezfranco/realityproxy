using UnityEngine;
using UnityEngine.UI; 
using TMPro;
using PolySpatial.Template;
using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;

// this is the script that will be attached to each sphere button raycasted to the scene
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

    private void Start()
    {
        geminiClient = new GeminiAPI(modelName, geminiApiKey);
        // Subscribe to the toggle's onValueChanged event
        if (spatialUIToggle != null)
        {
            spatialUIToggle.m_ToggleChanged.AddListener(OnSphereToggled);
        }
    }

    private void OnSphereToggled(bool isOn)
    {
        if (isOn)
        {
            InfoPanel.SetActive(true);

            // We just toggled ON this sphere: tell the menu to update the title
            if (labelUnderSphere != null)
            {
                string labelContent = labelUnderSphere.text;
                menuScript.SetMenuTitle(labelContent);

                // ------------------------------------------------------------
                // 1) Call Gemini here with the label text + camera frame
                // ------------------------------------------------------------
                StartCoroutine(GenerateQuestionsRoutine(labelContent));
            }
        }
        else
        {
            InfoPanel.SetActive(false);
            answerPanel.SetActive(false);
        }
    }

    /// <summary>
    /// Coroutine that captures the current camera frame, sends it to Gemini,
    /// parses the returned JSON for "questions", and instantiates new text lines.
    /// </summary>
    private IEnumerator GenerateQuestionsRoutine(string labelContent)
    {
        // 1) Capture the camera frame -> Base64
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        string base64Image = ConvertTextureToBase64(frameTex);
        Destroy(frameTex);  // free the temporary texture

        // 2) Build a simple prompt that references the label Content
        // Adjust as needed; this is just a minimal example.
        string prompt = $@"
            You are provided an image (inline data) plus the label: '{labelContent}'.
            Please return a JSON list of possible user questions about this product/item.
            Return only the most possible questions, up to 5 maximum.
            In the format: 
            json
            [
            ""Question 1"",
            ""Question 2"",
            ...
            ]
            ";

        // 3) Call Gemini (assuming geminiClient has been set in the Inspector)
        var requestTask = geminiClient.GenerateContent(prompt, base64Image);

        // Wait for the async Task to complete
        while (!requestTask.IsCompleted) 
            yield return null;

        string geminiResponse = requestTask.Result;
        Debug.Log("Gemini Re-Call Response:\n" + geminiResponse);

        // 4) Parse the JSON from the first candidate. 
        //    For brevity, let's assume your API returns something 
        //    akin to:
        //    {
        //      "candidates": [
        //         { "content": { "parts": [{ "text": "...the json..." }] } }
        //      ]
        //    }
        // Use whichever JSON approach you prefer:
        string extractedJson = TryExtractJson(geminiResponse);

        if (string.IsNullOrEmpty(extractedJson))
        {
            Debug.LogWarning("Could not find valid JSON block in Gemini response.");
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
            float spacing = 5f;  // Space between questions, adjust as needed

            foreach (var q in questionsList)
            {
                // Instantiate your question prefab 
                var go = Instantiate(questionPrefab, questionsParent);
                go.name = "GeminiQuestion";

                // Position using regular Transform
                Transform transform = go.transform;
                if (transform != null)
                {
                    transform.localPosition = new Vector3(0f, -currentY, 0f);
                    currentY += questionHeight + spacing;
                }

                // Set the text inside
                TextMeshPro txt = go.GetComponentInChildren<TextMeshPro>();
                if (txt != null)
                {
                    txt.text = q;
                }

                // Add button press handling
                var button = go.GetComponent<SpatialUIButton>();
                if (button != null)
                {
                    string questionText = q; // Capture the current question in closure
                    button.WasPressed += (buttonText, renderer, index) =>
                    {
                        if (questionAnswerer != null)
                        {
                            questionAnswerer.RequestAnswer(questionText);
                            answerPanel.SetActive(true);
                        }
                        else
                        {
                            Debug.LogWarning("QuestionAnswerer reference not set in SphereToggleScript");
                        }
                    };
                }
                else
                {
                    Debug.LogWarning("Question prefab is missing SpatialUIButton component");
                }
            }
        }
    }

    /// <summary>
    /// Example helper to extract the JSON portion from the Gemini response
    /// which might contain ```json ...```.
    /// If your response structure is different, adjust accordingly.
    /// </summary>
    private string TryExtractJson(string fullResponse)
    {
        // 1) Suppose we parse the top-level to see the first candidate text
        //    (Mirroring the approach in Gemini2DBoundingBoxDetector).
        // You could do something more robust here:
        try
        {
            // We'll do a quick partial extraction
            var root = JsonConvert.DeserializeObject<GeminiRoot>(fullResponse);
            if (root?.candidates == null || root.candidates.Count == 0)
                return null;

            string rawText = root.candidates[0].content.parts[0].text;
            if (string.IsNullOrEmpty(rawText)) 
                return null;

            // 2) If rawText includes "```json", let's attempt to split
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
            return null;
        }
    }

    /// <summary>
    /// A minimal version of the same root/candidate structure found in your code.
    /// </summary>
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

    /// <summary>
    /// Clears previously spawned question lines under questionsParent.
    /// You can refine this to only remove objects with a certain name, etc.
    /// </summary>
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