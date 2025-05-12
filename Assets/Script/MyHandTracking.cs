using UnityEngine;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;
using System.Collections.Generic;

public class MyHandTracking : MonoBehaviour
{
    public XROrigin xrOrigin;
    public XRHandSubsystem handSubsystem;

    [Header("Hand Prefabs")]
    [SerializeField] GameObject m_LeftHandPrefab;
    [SerializeField] GameObject m_RightHandPrefab;

    public GameObject m_SpawnedLeftHand;
    public GameObject m_SpawnedRightHand;

    [Header("Pinch Detection")]
    [Tooltip("Maximum distance between thumb and index finger to register as a pinch (in meters)")]
    [SerializeField] float pinchThreshold = 0.005f;
    [Tooltip("Minimum distance between thumb and index finger to register as a pinch release (in meters)")]
    [SerializeField] float pinchReleaseThreshold = 0.01f;

    [Header("Middle Finger Pinch Detection")]
    [Tooltip("Maximum distance between thumb and middle finger to register as a middle pinch (in meters)")]
    [SerializeField] float middlePinchThreshold = 0.007f;
    [Tooltip("Minimum distance between thumb and middle finger to register as a middle pinch release (in meters)")]
    [SerializeField] float middlePinchReleaseThreshold = 0.02f;
    
    [Header("Ring Finger Pinch Detection")]
    [Tooltip("Maximum distance between thumb and ring finger to register as a ring pinch (in meters)")]
    [SerializeField] float ringPinchThreshold = 0.007f;
    [Tooltip("Minimum distance between thumb and ring finger to register as a ring pinch release (in meters)")]
    [SerializeField] float ringPinchReleaseThreshold = 0.02f;

    [Header("Toggle GameObjects")]
    [Tooltip("GameObject to toggle visibility when left hand middle finger pinch is detected")]
    public GameObject toggleObject;
    [Tooltip("GameObject to toggle visibility when left hand ring finger pinch is detected")]
    public GameObject ringToggleObject;

    // Events for pinch detection
    public delegate void PinchEventHandler(bool isLeft);
    public static event PinchEventHandler OnPinchStarted;
    public static event PinchEventHandler OnPinchEnded;

    // Events for middle finger pinch detection
    public delegate void MiddlePinchEventHandler(bool isLeft);
    public static event MiddlePinchEventHandler OnMiddlePinchStarted;
    public static event MiddlePinchEventHandler OnMiddlePinchEnded;
    
    // Events for ring finger pinch detection
    public delegate void RingPinchEventHandler(bool isLeft);
    public static event RingPinchEventHandler OnRingPinchStarted;
    public static event RingPinchEventHandler OnRingPinchEnded;
    
    // Track pinch state for each hand
    private bool leftHandPinching = false;
    private bool rightHandPinching = false;

    // Track middle finger pinch state for each hand
    private bool leftHandMiddlePinching = false;
    private bool rightHandMiddlePinching = false;
    
    // Track ring finger pinch state for each hand
    private bool leftHandRingPinching = false;
    private bool rightHandRingPinching = false;

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
        {
            // Add HandGrabTrigger and explicitly set it to left hand
            HandGrabTrigger leftHandGrabber = m_SpawnedLeftHand.AddComponent<HandGrabTrigger>();
            if (leftHandGrabber != null)
            {
                leftHandGrabber.handType = "left";
                // Debug.Log("Added HandGrabTrigger to left hand with handType = left");
            }
        }

        if (m_SpawnedRightHand)
        {
            // Add HandGrabTrigger and explicitly set it to right hand
            HandGrabTrigger rightHandGrabber = m_SpawnedRightHand.AddComponent<HandGrabTrigger>();
            if (rightHandGrabber != null)
            {
                rightHandGrabber.handType = "right";
                // Debug.Log("Added HandGrabTrigger to right hand with handType = right");
            }
        }

        SubsystemManager.GetSubsystems(handSubsystems);
        
        if (handSubsystems.Count > 0)
        {
            handSubsystem = handSubsystems[0];
            // Debug.Log("Hand tracking subsystem found!");

            if (visualizeJoints)
            {
                InitializeJointVisualizers();
            }
        }
        else
        {
            #if !UNITY_EDITOR
            Debug.LogWarning("No hand tracking subsystem found!");
            #endif
        }
        
        // Register for middle pinch events to toggle object visibility
        OnMiddlePinchStarted += HandleMiddlePinchStarted;
        // Register for ring pinch events to toggle object visibility
        OnRingPinchStarted += HandleRingPinchStarted;
    }
    
    void OnDestroy()
    {
        // Unregister event listeners when component is destroyed
        OnMiddlePinchStarted -= HandleMiddlePinchStarted;
        OnRingPinchStarted -= HandleRingPinchStarted;
    }
    
    private void HandleMiddlePinchStarted(bool isLeft)
    {
        // Only toggle for left hand middle pinch
        if (isLeft && toggleObject != null)
        {
            // Toggle the object's active state
            toggleObject.SetActive(!toggleObject.activeSelf);
            Debug.Log($"Toggled {toggleObject.name} visibility to {toggleObject.activeSelf}");
        }
    }
    
    private void HandleRingPinchStarted(bool isLeft)
    {
        // Only toggle for left hand ring pinch
        if (isLeft && ringToggleObject != null)
        {
            // Toggle the object's active state
            ringToggleObject.SetActive(!ringToggleObject.activeSelf);
            Debug.Log($"Toggled {ringToggleObject.name} visibility to {ringToggleObject.activeSelf}");
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
        // Get both MiddleDistal and Wrist poses
        if (hand.GetJoint(XRHandJointID.MiddleDistal).TryGetPose(out Pose middleDistalPose) &&
            hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wristPose))
        {
            string handName = isLeft ? "Left" : "Right";

            // Calculate the midpoint between MiddleDistal and Wrist
            Vector3 midPoint = Vector3.Lerp(middleDistalPose.position, wristPose.position, 0.5f);
            
            // Use wrist rotation as the base rotation
            Quaternion handRotation = wristPose.rotation;

            // Debug.Log($"{handName} hand position: {midPoint}");

            if (isLeft && m_SpawnedLeftHand)
            {
                m_SpawnedLeftHand.transform.position = midPoint;
                m_SpawnedLeftHand.transform.rotation = handRotation;
                
                // Update the hand pose in the HandGrabTrigger
                HandGrabTrigger grabber = m_SpawnedLeftHand.GetComponent<HandGrabTrigger>();
                if (grabber != null)
                {
                    grabber.UpdateHandPose(hand);
                }
            }
            else if (!isLeft && m_SpawnedRightHand)
            {
                m_SpawnedRightHand.transform.position = midPoint;
                m_SpawnedRightHand.transform.rotation = handRotation;
                
                // Update the hand pose in the HandGrabTrigger
                HandGrabTrigger grabber = m_SpawnedRightHand.GetComponent<HandGrabTrigger>();
                if (grabber != null)
                {
                    grabber.UpdateHandPose(hand);
                }
            }

            // Check for pinch gesture
            DetectPinchGesture(hand, isLeft);
            
            // Check for middle finger pinch gesture
            DetectMiddlePinchGesture(hand, isLeft);
            
            // Check for ring finger pinch gesture
            DetectRingPinchGesture(hand, isLeft);

            // Update joint visualizers if enabled
            if (visualizeJoints)
            {
                UpdateJointVisualizers(hand, isLeft);
            }
        }
    }

    private void DetectPinchGesture(XRHand hand, bool isLeft)
    {
        // Get thumb tip and index tip poses
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexTipPose))
        {
            // Calculate distance between thumb tip and index tip
            float distance = Vector3.Distance(thumbTipPose.position, indexTipPose.position);
            
            // Check if we're currently pinching
            bool isPinching = isLeft ? leftHandPinching : rightHandPinching;
            
            // Detect pinch start (using hysteresis to prevent flickering)
            if (!isPinching && distance < pinchThreshold)
            {
                // Start pinching
                if (isLeft)
                    leftHandPinching = true;
                else
                    rightHandPinching = true;
                
                // Invoke pinch started event
                OnPinchStarted?.Invoke(isLeft);
                
                // Debug.Log($"{(isLeft ? "Left" : "Right")} hand pinch started. Distance: {distance:F3}m");
            }
            // Detect pinch end (using a larger threshold for release to prevent flickering)
            else if (isPinching && distance > pinchReleaseThreshold)
            {
                // End pinching
                if (isLeft)
                    leftHandPinching = false;
                else
                    rightHandPinching = false;
                
                // Invoke pinch ended event
                OnPinchEnded?.Invoke(isLeft);
                
                // Debug.Log($"{(isLeft ? "Left" : "Right")} hand pinch ended. Distance: {distance:F3}m");
            }
        }
    }
    private void DetectMiddlePinchGesture(XRHand hand, bool isLeft)
    {
        // Get thumb tip and middle tip poses
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out Pose middleTipPose))
        {
            // Calculate distance between thumb tip and middle tip
            float distance = Vector3.Distance(thumbTipPose.position, middleTipPose.position);
            
            // Check if we're currently pinching with middle finger
            bool isMiddlePinching = isLeft ? leftHandMiddlePinching : rightHandMiddlePinching;
            
            // Detect middle finger pinch start (using hysteresis to prevent flickering)
            if (!isMiddlePinching && distance < middlePinchThreshold)
            {
                // Start middle finger pinching
                if (isLeft)
                    leftHandMiddlePinching = true;
                else
                    rightHandMiddlePinching = true;
                
                // Invoke middle finger pinch started event
                OnMiddlePinchStarted?.Invoke(isLeft);
                
                // Debug.Log($"{(isLeft ? "Left" : "Right")} hand middle finger pinch started. Distance: {distance:F3}m");
            }
            // Detect middle finger pinch end (using a larger threshold for release to prevent flickering)
            else if (isMiddlePinching && distance > middlePinchReleaseThreshold)
            {
                // End middle finger pinching
                if (isLeft)
                    leftHandMiddlePinching = false;
                else
                    rightHandMiddlePinching = false;
                
                // Invoke middle finger pinch ended event
                OnMiddlePinchEnded?.Invoke(isLeft);
                
                // Debug.Log($"{(isLeft ? "Left" : "Right")} hand middle finger pinch ended. Distance: {distance:F3}m");
            }
        }
    }

    private void DetectRingPinchGesture(XRHand hand, bool isLeft)
    {
        // Get thumb tip and ring tip poses
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.RingTip).TryGetPose(out Pose ringTipPose))
        {
            // Calculate distance between thumb tip and ring tip
            float distance = Vector3.Distance(thumbTipPose.position, ringTipPose.position);
            
            // Check if we're currently pinching with ring finger
            bool isRingPinching = isLeft ? leftHandRingPinching : rightHandRingPinching;
            
            // Detect ring finger pinch start (using hysteresis to prevent flickering)
            if (!isRingPinching && distance < ringPinchThreshold)
            {
                // Start ring finger pinching
                if (isLeft)
                    leftHandRingPinching = true;
                else
                    rightHandRingPinching = true;
                
                // Invoke ring finger pinch started event
                OnRingPinchStarted?.Invoke(isLeft);
                
                // Debug.Log($"{(isLeft ? "Left" : "Right")} hand ring finger pinch started. Distance: {distance:F3}m");
            }
            // Detect ring finger pinch end (using a larger threshold for release to prevent flickering)
            else if (isRingPinching && distance > ringPinchReleaseThreshold)
            {
                // End ring finger pinching
                if (isLeft)
                    leftHandRingPinching = false;
                else
                    rightHandRingPinching = false;
                
                // Invoke ring finger pinch ended event
                OnRingPinchEnded?.Invoke(isLeft);
                
                // Debug.Log($"{(isLeft ? "Left" : "Right")} hand ring finger pinch ended. Distance: {distance:F3}m");
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
            leftVisualizer.layer = LayerMask.NameToLayer("PolySpatial");
            leftHandVisualizers[i] = leftVisualizer;

            var rightVisualizer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rightVisualizer.transform.localScale = Vector3.one * 0.01f; // 1cm spheres
            rightVisualizer.SetActive(false);
            rightVisualizer.layer = LayerMask.NameToLayer("PolySpatial");
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
    
    // Public methods to check pinch state
    
    /// <summary>
    /// Returns true if the specified hand is currently pinching
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <returns>True if pinching, false otherwise</returns>
    public bool IsPinching(bool isLeft)
    {
        return isLeft ? leftHandPinching : rightHandPinching;
    }
    
    /// <summary>
    /// Attempts to get the current pinch position (midpoint between thumb and index finger)
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <param name="position">Output position of the pinch point</param>
    /// <returns>True if position was successfully retrieved, false otherwise</returns>
    public bool TryGetPinchPosition(bool isLeft, out Vector3 position)
    {
        position = Vector3.zero;
        
        if (handSubsystem == null || !handSubsystem.running)
            return false;
            
        var hand = isLeft ? handSubsystem.leftHand : handSubsystem.rightHand;
        
        if (!hand.isTracked)
            return false;
            
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexTipPose))
        {
            // Calculate midpoint between thumb tip and index tip
            position = Vector3.Lerp(thumbTipPose.position, indexTipPose.position, 0.5f);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Gets the current distance between thumb tip and index finger tip
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <param name="distance">Output distance between thumb and index finger</param>
    /// <returns>True if distance was successfully calculated, false otherwise</returns>
    public bool TryGetPinchDistance(bool isLeft, out float distance)
    {
        distance = float.MaxValue;
        
        if (handSubsystem == null || !handSubsystem.running)
            return false;
            
        var hand = isLeft ? handSubsystem.leftHand : handSubsystem.rightHand;
        
        if (!hand.isTracked)
            return false;
            
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexTipPose))
        {
            distance = Vector3.Distance(thumbTipPose.position, indexTipPose.position);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Gets the hand position (midpoint between thumb and index tips) regardless of pinch state
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <param name="position">Output position of the hand midpoint</param>
    /// <returns>True if position was successfully retrieved, false otherwise</returns>
    public bool TryGetHandPosition(bool isLeft, out Vector3 position)
    {
        position = Vector3.zero;
        
        if (handSubsystem == null || !handSubsystem.running)
            return false;
            
        var hand = isLeft ? handSubsystem.leftHand : handSubsystem.rightHand;
        
        if (!hand.isTracked)
            return false;
            
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexTipPose))
        {
            // Calculate midpoint between thumb tip and index tip
            position = Vector3.Lerp(thumbTipPose.position, indexTipPose.position, 0.5f);
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Returns true if the specified hand is currently performing a middle finger pinch
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <returns>True if middle finger pinching, false otherwise</returns>
    public bool IsMiddlePinching(bool isLeft)
    {
        return isLeft ? leftHandMiddlePinching : rightHandMiddlePinching;
    }

    /// <summary>
    /// Attempts to get the current middle finger pinch position (midpoint between thumb and middle fingertips)
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <param name="position">Output position of the middle finger pinch point</param>
    /// <returns>True if position was successfully retrieved, false otherwise</returns>
    public bool TryGetMiddlePinchPosition(bool isLeft, out Vector3 position)
    {
        position = Vector3.zero;
        
        if (handSubsystem == null || !handSubsystem.running)
            return false;
            
        var hand = isLeft ? handSubsystem.leftHand : handSubsystem.rightHand;
        
        if (!hand.isTracked)
            return false;
            
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out Pose middleTipPose))
        {
            // Calculate midpoint between thumb tip and middle tip
            position = Vector3.Lerp(thumbTipPose.position, middleTipPose.position, 0.5f);
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Gets the current distance between thumb tip and middle fingertip
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <param name="distance">Output distance between the thumb and middle fingertip</param>
    /// <returns>True if distance was successfully calculated, false otherwise</returns>
    public bool TryGetMiddlePinchDistance(bool isLeft, out float distance)
    {
        distance = float.MaxValue;
        
        if (handSubsystem == null || !handSubsystem.running)
            return false;
            
        var hand = isLeft ? handSubsystem.leftHand : handSubsystem.rightHand;
        
        if (!hand.isTracked)
            return false;
            
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out Pose middleTipPose))
        {
            distance = Vector3.Distance(thumbTipPose.position, middleTipPose.position);
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Returns true if the specified hand is currently performing a ring finger pinch
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <returns>True if ring finger pinching, false otherwise</returns>
    public bool IsRingPinching(bool isLeft)
    {
        return isLeft ? leftHandRingPinching : rightHandRingPinching;
    }

    /// <summary>
    /// Attempts to get the current ring finger pinch position (midpoint between thumb and ring fingertips)
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <param name="position">Output position of the ring finger pinch point</param>
    /// <returns>True if position was successfully retrieved, false otherwise</returns>
    public bool TryGetRingPinchPosition(bool isLeft, out Vector3 position)
    {
        position = Vector3.zero;
        
        if (handSubsystem == null || !handSubsystem.running)
            return false;
            
        var hand = isLeft ? handSubsystem.leftHand : handSubsystem.rightHand;
        
        if (!hand.isTracked)
            return false;
            
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.RingTip).TryGetPose(out Pose ringTipPose))
        {
            // Calculate midpoint between thumb tip and ring tip
            position = Vector3.Lerp(thumbTipPose.position, ringTipPose.position, 0.5f);
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Gets the current distance between thumb tip and ring fingertip
    /// </summary>
    /// <param name="isLeft">True for left hand, false for right hand</param>
    /// <param name="distance">Output distance between the thumb and ring fingertip</param>
    /// <returns>True if distance was successfully calculated, false otherwise</returns>
    public bool TryGetRingPinchDistance(bool isLeft, out float distance)
    {
        distance = float.MaxValue;
        
        if (handSubsystem == null || !handSubsystem.running)
            return false;
            
        var hand = isLeft ? handSubsystem.leftHand : handSubsystem.rightHand;
        
        if (!hand.isTracked)
            return false;
            
        if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbTipPose) &&
            hand.GetJoint(XRHandJointID.RingTip).TryGetPose(out Pose ringTipPose))
        {
            distance = Vector3.Distance(thumbTipPose.position, ringTipPose.position);
            return true;
        }
        
        return false;
    }
} 