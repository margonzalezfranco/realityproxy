using UnityEngine;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;
using System.Collections.Generic;

public class MyHandTracking : MonoBehaviour
{
    public XROrigin xrOrigin;
    private XRHandSubsystem handSubsystem;

    [Header("Hand Prefabs")]
    [SerializeField] GameObject m_LeftHandPrefab;
    [SerializeField] GameObject m_RightHandPrefab;

    private GameObject m_SpawnedLeftHand;
    private GameObject m_SpawnedRightHand;

    [Header("Debug Visualization")]
    public bool visualizeJoints = true;
    private GameObject[] leftHandVisualizers;
    private GameObject[] rightHandVisualizers;

    void Start()
    {
        // Get the hand subsystem
        var handSubsystems = new List<XRHandSubsystem>();

        if (m_LeftHandPrefab)
            m_SpawnedLeftHand = Instantiate(m_LeftHandPrefab, new Vector3(0, 1, 0), Quaternion.identity, xrOrigin.transform);

        if (m_RightHandPrefab)
            m_SpawnedRightHand = Instantiate(m_RightHandPrefab, new Vector3(0, 1, 0), Quaternion.identity, xrOrigin.transform);

        if (m_SpawnedLeftHand)
            m_SpawnedLeftHand.AddComponent<HandGrabTrigger>();

        if (m_SpawnedRightHand)
            m_SpawnedRightHand.AddComponent<HandGrabTrigger>();

        SubsystemManager.GetSubsystems(handSubsystems);
        
        if (handSubsystems.Count > 0)
        {
            handSubsystem = handSubsystems[0];
            Debug.Log("Hand tracking subsystem found!");

            if (visualizeJoints)
            {
                InitializeJointVisualizers();
            }
        }
        else
        {
            Debug.LogWarning("No hand tracking subsystem found!");
        }
    }

    void Update()
    {
        if (handSubsystem == null || !handSubsystem.running) return;

        // Get both hands
        var leftHand = handSubsystem.leftHand;
        var rightHand = handSubsystem.rightHand;

        // Check if hands are tracked
        if (leftHand.isTracked)
        {
            ProcessHand(leftHand, true);
        }

        if (rightHand.isTracked)
        {
            ProcessHand(rightHand, false);
        }
    }

    private void ProcessHand(XRHand hand, bool isLeft)
    {
        // Get root pose
        if (hand.GetJoint(XRHandJointID.MiddleDistal).TryGetPose(out Pose middleDistalPose))
        {
            string handName = isLeft ? "Left" : "Right";

            // Debug.Log($"{handName} hand position: {middleDistalPose.position}");

            if (isLeft && m_SpawnedLeftHand)
            {
                m_SpawnedLeftHand.transform.position = middleDistalPose.position;
                m_SpawnedLeftHand.transform.rotation = middleDistalPose.rotation;
            }
            else if (!isLeft && m_SpawnedRightHand)
            {
                m_SpawnedRightHand.transform.position = middleDistalPose.position;
                m_SpawnedRightHand.transform.rotation = middleDistalPose.rotation;
            }

            // Update joint visualizers if enabled
            if (visualizeJoints)
            {
                UpdateJointVisualizers(hand, isLeft);
            }
        }
    }

    private void InitializeJointVisualizers()
    {
        int jointCount = System.Enum.GetValues(typeof(XRHandJointID)).Length;
        leftHandVisualizers = new GameObject[jointCount];
        rightHandVisualizers = new GameObject[jointCount];

        for (int i = 0; i < jointCount; i++)
        {
            var leftVisualizer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            leftVisualizer.transform.localScale = Vector3.one * 0.01f; // 1cm spheres
            leftVisualizer.SetActive(false);
            leftVisualizer.layer = LayerMask.NameToLayer("Ignore Raycast");
            leftHandVisualizers[i] = leftVisualizer;

            var rightVisualizer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rightVisualizer.transform.localScale = Vector3.one * 0.01f; // 1cm spheres
            rightVisualizer.SetActive(false);
            rightVisualizer.layer = LayerMask.NameToLayer("Ignore Raycast");
            rightHandVisualizers[i] = rightVisualizer;

            // Debug log to confirm initialization
            // Debug.Log($"Initialized visualizer for joint index {i}");
        }
    }

    private void UpdateJointVisualizers(XRHand hand, bool isLeft)
    {
        var visualizers = isLeft ? leftHandVisualizers : rightHandVisualizers;

        foreach (XRHandJointID jointId in System.Enum.GetValues(typeof(XRHandJointID)))
        {
            if (hand.GetJoint(jointId).TryGetPose(out Pose pose))
            {
                int idx = (int)jointId;
                visualizers[idx].transform.position = pose.position;
                visualizers[idx].transform.rotation = pose.rotation;
                visualizers[idx].SetActive(true);

                // Debug log to confirm position update
                // Debug.Log($"Updating joint {jointId} for {(isLeft ? "Left" : "Right")} hand at position {pose.position}");
            }
            else
            {
                visualizers[(int)jointId].SetActive(false);
            }
        }
    }

    void OnDisable()
    {
        if (leftHandVisualizers != null)
        {
            foreach (var visualizer in leftHandVisualizers)
            {
                if (visualizer != null)
                {
                    Destroy(visualizer);
                }
            }
        }

        if (rightHandVisualizers != null)
        {
            foreach (var visualizer in rightHandVisualizers)
            {
                if (visualizer != null)
                {
                    Destroy(visualizer);
                }
            }
        }
    }
} 