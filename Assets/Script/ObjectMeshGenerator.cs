using UnityEngine;
using System.Collections;
using Newtonsoft.Json;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Generates custom meshes for basic 3D shapes with specified dimensions.
/// Can also estimate object dimensions using Gemini.
/// </summary>
public class ObjectMeshGenerator : GeminiGeneral
{
    [Header("Material Settings")]
    public Material defaultMaterial;

    [Header("Mesh Generation Settings")]
    [Tooltip("Number of segments around the cylinder (more = smoother)")]
    public int cylinderSegments = 24;

    [Header("Debug Settings")]
    [SerializeField] private bool showDebugLogs = true;

    [System.Serializable]
    public class ObjectDimensions
    {
        public string shape;           // "cylinder" or "cube"
        public float diameterCm;       // for cylinder
        public float lengthCm;         // for cube
        public float widthCm;         // for cube
        public float heightCm;        // for both
    }

    /// <summary>
    /// Estimates the dimensions of an object in the camera view using Gemini
    /// </summary>
    public IEnumerator EstimateObjectDimensions(System.Action<ObjectDimensions> onComplete)
    {
        // 1) Capture frame from RenderTexture
        Texture2D frameTex = CaptureFrame(cameraRenderTex);

        // 2) Convert to base64
        string base64Image = ConvertTextureToBase64(frameTex);

        // 3) Build prompt for dimension estimation
        string prompt = @"Look at this image and estimate the dimensions of the main object being held or focused on. 
        Determine if it's closer to a cylinder or a rectangular shape (cube/cuboid).
        Provide the dimensions in centimeters in the following JSON format:

        For cylinder:
        {
            ""shape"": ""cylinder"",
            ""diameterCm"": <estimated_diameter>,
            ""heightCm"": <estimated_height>
        }

        For rectangular objects:
        {
            ""shape"": ""cube"",
            ""lengthCm"": <estimated_length>,
            ""widthCm"": <estimated_width>,
            ""heightCm"": <estimated_height>
        }

        Consider any visual cues for scale (like hands, known objects). Round measurements to nearest 0.5 cm.
        If uncertain, provide best estimate based on typical sizes of similar objects.";

        // 4) Call Gemini
        var request = geminiClient.GenerateContent(prompt, base64Image);
        while (!request.IsCompleted)
        {
            yield return null;
        }
        string response = request.Result;

        // 5) Parse the response
        ObjectDimensions dimensions = ParseDimensionResponse(response);
        
        // 6) Clean up
        Destroy(frameTex);

        // 7) Return result via callback
        onComplete?.Invoke(dimensions);
    }

    private ObjectDimensions ParseDimensionResponse(string response)
    {
        try
        {
            string jsonText = ParseGeminiRawResponse(response);
            if (string.IsNullOrEmpty(jsonText)) return null;

            var dimensions = JsonConvert.DeserializeObject<ObjectDimensions>(jsonText);
            
            // Validate the parsed data
            if (dimensions.shape == "cylinder")
            {
                if (dimensions.diameterCm <= 0 || dimensions.heightCm <= 0)
                {
                    Debug.LogError("Invalid cylinder dimensions");
                    return null;
                }
            }
            else if (dimensions.shape == "cube")
            {
                if (dimensions.lengthCm <= 0 || dimensions.widthCm <= 0 || dimensions.heightCm <= 0)
                {
                    Debug.LogError("Invalid cube dimensions");
                    return null;
                }
            }
            else
            {
                Debug.LogError($"Unknown shape type: {dimensions.shape}");
                return null;
            }

            return dimensions;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error parsing dimension response: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Convenience method to estimate and generate the object in one call
    /// </summary>
    public IEnumerator EstimateAndGenerateObject(Material material = null)
    {
        yield return EstimateObjectDimensions((dimensions) =>
        {
            if (dimensions != null)
            {
                GameObject generatedObj = null;
                
                if (dimensions.shape == "cylinder")
                {
                    generatedObj = CreateCylinder(dimensions.diameterCm, dimensions.heightCm, material);
                    Debug.Log($"Generated cylinder: diameter={dimensions.diameterCm}cm, height={dimensions.heightCm}cm");
                }
                else if (dimensions.shape == "cube")
                {
                    generatedObj = CreateCube(dimensions.lengthCm, dimensions.widthCm, dimensions.heightCm, material);
                    Debug.Log($"Generated cube: {dimensions.lengthCm}x{dimensions.widthCm}x{dimensions.heightCm}cm");
                }

                if (generatedObj != null)
                {
                    // Position the object in front of the camera
                    generatedObj.transform.position = Camera.main.transform.position + 
                                                    Camera.main.transform.forward * 0.3f;
                }
            }
        });
    }

    /// <summary>
    /// Creates a cylinder GameObject with specified dimensions
    /// </summary>
    /// <param name="diameterCm">Diameter of the cylinder base in centimeters</param>
    /// <param name="heightCm">Height of the cylinder in centimeters</param>
    /// <param name="material">Optional material (uses defaultMaterial if null)</param>
    /// <returns>Generated cylinder GameObject</returns>
    public GameObject CreateCylinder(float diameterCm, float heightCm, Material material = null)
    {
        // Convert cm to meters
        float diameterMeters = diameterCm * 0.01f;
        float heightMeters = heightCm * 0.01f;

        // Create GameObject and components
        GameObject cylinderObj = new GameObject($"Generated_Cylinder_{diameterCm}cm_x_{heightCm}cm");
        MeshFilter meshFilter = cylinderObj.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = cylinderObj.AddComponent<MeshRenderer>();
        meshRenderer.material = material ?? defaultMaterial;

        // Generate mesh using radius (diameter/2) in meters
        meshFilter.mesh = GenerateCylinderMesh(diameterMeters * 0.5f, heightMeters);

        return cylinderObj;
    }

    /// <summary>
    /// Creates a cube GameObject with specified dimensions
    /// </summary>
    /// <param name="lengthCm">Length in centimeters (X axis)</param>
    /// <param name="widthCm">Width in centimeters (Z axis)</param>
    /// <param name="heightCm">Height in centimeters (Y axis)</param>
    /// <param name="material">Optional material (uses defaultMaterial if null)</param>
    /// <returns>Generated cube GameObject</returns>
    public GameObject CreateCube(float lengthCm, float widthCm, float heightCm, Material material = null)
    {
        // Convert cm to meters
        float lengthMeters = lengthCm * 0.01f;
        float widthMeters = widthCm * 0.01f;
        float heightMeters = heightCm * 0.01f;

        GameObject cubeObj = new GameObject($"Generated_Cube_{lengthCm}cm_x_{widthCm}cm_x_{heightCm}cm");
        MeshFilter meshFilter = cubeObj.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = cubeObj.AddComponent<MeshRenderer>();
        meshRenderer.material = material ?? defaultMaterial;

        meshFilter.mesh = GenerateCubeMesh(lengthMeters, widthMeters, heightMeters);

        return cubeObj;
    }

    private Mesh GenerateCylinderMesh(float radius, float height)
    {
        Mesh mesh = new Mesh();
        
        // Calculate vertices needed
        int numVertices = (cylinderSegments + 1) * 2 + cylinderSegments * 2;
        Vector3[] vertices = new Vector3[numVertices];
        Vector2[] uv = new Vector2[numVertices];
        
        // Generate vertices for top and bottom circles
        for (int i = 0; i <= cylinderSegments; i++)
        {
            float angle = 2 * Mathf.PI * i / cylinderSegments;
            float x = Mathf.Cos(angle) * radius;
            float z = Mathf.Sin(angle) * radius;

            // Bottom vertices
            vertices[i] = new Vector3(x, 0, z);
            uv[i] = new Vector2((float)i / cylinderSegments, 0);

            // Top vertices
            vertices[i + cylinderSegments + 1] = new Vector3(x, height, z);
            uv[i + cylinderSegments + 1] = new Vector2((float)i / cylinderSegments, 1);
        }

        // Generate side vertices
        int sideOffset = (cylinderSegments + 1) * 2;
        for (int i = 0; i < cylinderSegments; i++)
        {
            float angle = 2 * Mathf.PI * i / cylinderSegments;
            float x = Mathf.Cos(angle) * radius;
            float z = Mathf.Sin(angle) * radius;

            vertices[sideOffset + i * 2] = new Vector3(x, 0, z);
            vertices[sideOffset + i * 2 + 1] = new Vector3(x, height, z);

            uv[sideOffset + i * 2] = new Vector2((float)i / cylinderSegments, 0);
            uv[sideOffset + i * 2 + 1] = new Vector2((float)i / cylinderSegments, 1);
        }

        // Generate triangles - corrected size calculation
        int numTriangles = cylinderSegments * 12; // 2 triangles per side * 3 vertices * cylinderSegments (for sides, top, and bottom)
        int[] triangles = new int[numTriangles];
        
        // Bottom cap
        for (int i = 0; i < cylinderSegments; i++)
        {
            int triIndex = i * 3;
            triangles[triIndex] = 0;
            triangles[triIndex + 1] = i + 1;
            triangles[triIndex + 2] = i + 2 > cylinderSegments ? 1 : i + 2;
        }

        // Top cap
        int topOffset = cylinderSegments + 1;
        for (int i = 0; i < cylinderSegments; i++)
        {
            int triIndex = cylinderSegments * 3 + i * 3;
            triangles[triIndex] = topOffset;
            triangles[triIndex + 1] = topOffset + i + 2 > topOffset + cylinderSegments ? topOffset + 1 : topOffset + i + 2;
            triangles[triIndex + 2] = topOffset + i + 1;
        }

        // Sides
        int sideTriOffset = cylinderSegments * 6;
        for (int i = 0; i < cylinderSegments; i++)
        {
            int current = sideOffset + i * 2;
            int next = sideOffset + ((i + 1) % cylinderSegments) * 2;
            int triIndex = sideTriOffset + i * 6;

            // First triangle
            triangles[triIndex] = current;
            triangles[triIndex + 1] = current + 1;
            triangles[triIndex + 2] = next + 1;

            // Second triangle
            triangles[triIndex + 3] = current;
            triangles[triIndex + 4] = next + 1;
            triangles[triIndex + 5] = next;
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uv;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    private Mesh GenerateCubeMesh(float length, float width, float height)
    {
        Mesh mesh = new Mesh();

        // Define the 8 vertices of the cube
        Vector3[] vertices = new Vector3[24];  // 4 vertices per face * 6 faces
        int[] triangles = new int[36];         // 2 triangles per face * 3 vertices * 6 faces
        Vector2[] uvs = new Vector2[24];       // 4 UVs per face * 6 faces

        float xl = length * 0.5f;
        float yw = width * 0.5f;
        float zh = height * 0.5f;

        // Front face
        vertices[0] = new Vector3(-xl, -zh, yw);
        vertices[1] = new Vector3(xl, -zh, yw);
        vertices[2] = new Vector3(xl, zh, yw);
        vertices[3] = new Vector3(-xl, zh, yw);

        // Back face
        vertices[4] = new Vector3(xl, -zh, -yw);
        vertices[5] = new Vector3(-xl, -zh, -yw);
        vertices[6] = new Vector3(-xl, zh, -yw);
        vertices[7] = new Vector3(xl, zh, -yw);

        // Top face
        vertices[8] = new Vector3(-xl, zh, yw);
        vertices[9] = new Vector3(xl, zh, yw);
        vertices[10] = new Vector3(xl, zh, -yw);
        vertices[11] = new Vector3(-xl, zh, -yw);

        // Bottom face
        vertices[12] = new Vector3(-xl, -zh, -yw);
        vertices[13] = new Vector3(xl, -zh, -yw);
        vertices[14] = new Vector3(xl, -zh, yw);
        vertices[15] = new Vector3(-xl, -zh, yw);

        // Right face
        vertices[16] = new Vector3(xl, -zh, yw);
        vertices[17] = new Vector3(xl, -zh, -yw);
        vertices[18] = new Vector3(xl, zh, -yw);
        vertices[19] = new Vector3(xl, zh, yw);

        // Left face
        vertices[20] = new Vector3(-xl, -zh, -yw);
        vertices[21] = new Vector3(-xl, -zh, yw);
        vertices[22] = new Vector3(-xl, zh, yw);
        vertices[23] = new Vector3(-xl, zh, -yw);

        // Triangles
        int[] tris = new int[]
        {
            // Front
            0, 1, 2, 0, 2, 3,
            // Back
            4, 5, 6, 4, 6, 7,
            // Top
            8, 9, 10, 8, 10, 11,
            // Bottom
            12, 13, 14, 12, 14, 15,
            // Right
            16, 17, 18, 16, 18, 19,
            // Left
            20, 21, 22, 20, 22, 23
        };
        System.Array.Copy(tris, triangles, tris.Length);

        // UVs
        for (int i = 0; i < 6; i++) // 6 faces
        {
            uvs[i * 4 + 0] = new Vector2(0, 0);
            uvs[i * 4 + 1] = new Vector2(1, 0);
            uvs[i * 4 + 2] = new Vector2(1, 1);
            uvs[i * 4 + 3] = new Vector2(0, 1);
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    // Add this method to be called from the inspector
    public void EditorEstimateAndGenerate()
    {
        StartCoroutine(EstimateAndGenerateObject());
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(ObjectMeshGenerator))]
public class ObjectMeshGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        ObjectMeshGenerator generator = (ObjectMeshGenerator)target;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Debug Actions", EditorStyles.boldLabel);
        
        if (GUILayout.Button("Estimate & Generate Object", GUILayout.Height(30)))
        {
            if (Application.isPlaying)
            {
                generator.EditorEstimateAndGenerate();
            }
            else
            {
                EditorUtility.DisplayDialog("Play Mode Required", 
                    "This action requires the game to be in Play Mode to access the camera and Gemini API.", 
                    "OK");
            }
        }

        // Add a helpful note
        EditorGUILayout.HelpBox(
            "Note: Estimation requires Play Mode and a valid camera feed to analyze.", 
            MessageType.Info);
    }
}
#endif 