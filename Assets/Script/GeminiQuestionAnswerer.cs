using System;
using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// Specialized implementation for question answering using Gemini API.
/// Takes an image and a question as input, returns a text response.
/// </summary>
public class GeminiQuestionAnswerer : GeminiGeneral
{
    [Header("UI Settings")]
    [Tooltip("Optional - TextMeshPro component to display the answer")]
    public TextMeshPro answerText;

    [Tooltip("Optional - Event to be called when answer is received")]
    public UnityEngine.Events.UnityEvent<string> onAnswerReceived;

    /// <summary>
    /// Request an answer from Gemini for a specific question about the current camera frame
    /// </summary>
    /// <param name="questionContent">The question to ask about the image</param>
    public void RequestAnswer(string questionContent)
    {
        StartCoroutine(QuestionAnswerRoutine(questionContent));
    }

    private IEnumerator QuestionAnswerRoutine(string questionContent)
    {
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);

        // 2) Convert to base64 (PNG)
        string base64Image = ConvertTextureToBase64(frameTex);

        // 3) Call Gemini with the question
        var request = geminiClient.GenerateContent(questionContent, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        string response = request.Result;

        Debug.Log($"Question: {questionContent}\nResponse: {response}");

        // 4) Parse the response
        string answer = ParseQuestionResponse(response);
        
        // 5) Update UI if available
        if (answerText != null && !string.IsNullOrEmpty(answer))
        {
            answerText.text = answer;
        }

        // 6) Invoke event with the answer
        onAnswerReceived?.Invoke(answer);

        // Clean up texture
        Destroy(frameTex);
    }

    private string ParseQuestionResponse(string response)
    {
        try
        {
            string parsedText = ParseGeminiRawResponse(response);
            if (string.IsNullOrEmpty(parsedText)) return "Failed to parse response";

            // Remove any markdown formatting if present
            parsedText = parsedText.Trim();
            if (parsedText.StartsWith("```") && parsedText.EndsWith("```"))
            {
                parsedText = parsedText.Substring(3, parsedText.Length - 6).Trim();
            }

            return parsedText;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error parsing question response: {ex}");
            return "Error parsing response";
        }
    }
} 