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

    [SerializeField] private string systemPromptTemplate = "You are a helpful AI assistant responding to the user's question about objects they are looking at. IMPORTANT GUIDELINES:\n1. You can see what the user is looking at, but DON'T JUST RELY on what's visible in the image\n2. NEVER say 'I can't see X clearly in the image' or 'The image doesn't show X'\n3. If something isn't visible or clear, still provide helpful information about what would typically be expected\n4. Draw on your general knowledge about the object to provide complete, accurate information\n5. Always provide a helpful, informative response rather than admitting limitations\n6. Respond concisely about this object without markdown formatting\n\nUser question: {0}";

    [Header("User Study Logging")]
    [SerializeField] private bool enableUserStudyLogging = true;

    /// <summary>
    /// Request an answer from Gemini for a specific question about the current camera frame
    /// </summary>
    /// <param name="questionContent">The question to ask about the image</param>
    public void RequestAnswer(string questionContent)
    {
        LogUserStudy($"[QUESTION_ANSWERER] QUESTION_REQUESTED: Question=\"{questionContent}\"");
        StartCoroutine(QuestionAnswerRoutine(questionContent));
    }

    private IEnumerator QuestionAnswerRoutine(string questionContent)
    {
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);
        LogUserStudy($"[QUESTION_ANSWERER] FRAME_CAPTURED: Resolution=\"{frameTex.width}x{frameTex.height}\"");

        // 2) Convert to base64 (PNG)
        string base64Image = ConvertTextureToBase64(frameTex);
        LogUserStudy($"[QUESTION_ANSWERER] IMAGE_ENCODED: Size={base64Image.Length} bytes");

        string formattedPrompt = string.Format(systemPromptTemplate, questionContent);

        // 3) Call Gemini with the question
        // This now uses the new RequestStatus system which supports concurrent API calls
        // from multiple components without interfering with each other
        LogUserStudy($"[QUESTION_ANSWERER] GEMINI_API_CALL_STARTED: Question=\"{questionContent}\"");
        var startTime = Time.realtimeSinceStartup;
        
        var request = MakeGeminiRequest(formattedPrompt, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        string response = request.Result;
        
        float duration = Time.realtimeSinceStartup - startTime;
        LogUserStudy($"[QUESTION_ANSWERER] GEMINI_API_CALL_COMPLETED: Duration={duration:F2}s, ResponseLength={response.Length}");

        Debug.Log($"Question: {formattedPrompt}\nResponse: {response}");

        // 4) Parse the response
        string answer = ParseQuestionResponse(response);
        LogUserStudy($"[QUESTION_ANSWERER] RESPONSE_PARSED: Length={answer.Length}, Success={(answer != "Error parsing response" && answer != "Failed to parse response")}");
        
        // 5) Update UI if available
        if (answerText != null && !string.IsNullOrEmpty(answer))
        {
            answerText.text = answer;
            LogUserStudy($"[QUESTION_ANSWERER] ANSWER_DISPLAYED: Length={answer.Length}");
        }

        // 6) Invoke event with the answer
        onAnswerReceived?.Invoke(answer);
        LogUserStudy($"[QUESTION_ANSWERER] ANSWER_EVENT_INVOKED: Listeners={onAnswerReceived?.GetPersistentEventCount() ?? 0}");

        // Clean up texture
        Destroy(frameTex);
    }

    private string ParseQuestionResponse(string response)
    {
        try
        {
            string parsedText = ParseGeminiRawResponse(response);
            if (string.IsNullOrEmpty(parsedText))
            {
                LogUserStudy($"[QUESTION_ANSWERER] PARSING_FAILED: Reason=\"empty_response\"");
                return "Failed to parse response";
            }

            // Remove any markdown formatting if present
            parsedText = parsedText.Trim();
            if (parsedText.StartsWith("```") && parsedText.EndsWith("```"))
            {
                parsedText = parsedText.Substring(3, parsedText.Length - 6).Trim();
                LogUserStudy($"[QUESTION_ANSWERER] MARKDOWN_REMOVED: Type=\"code_block\"");
            }

            return parsedText;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error parsing question response: {ex}");
            LogUserStudy($"[QUESTION_ANSWERER] PARSING_ERROR: Exception=\"{ex.GetType().Name}\", Message=\"{ex.Message}\"");
            return "Error parsing response";
        }
    }
    
    // Helper method for creating timestamped user study logs
    private void LogUserStudy(string message)
    {
        if (!enableUserStudyLogging) return;
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Debug.Log($"[USER_STUDY_LOG][{timestamp}] {message}");
    }
} 