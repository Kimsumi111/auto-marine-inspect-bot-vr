using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Globalization;
using ShipRobot.LaneFollowing;
using UnityEngine;
using UnityEngine.Serialization;

namespace ShipRobot.Navigation
{
    [DisallowMultipleComponent]
    public sealed class NavigationCoordinator : MonoBehaviour
    {
        public enum MissionState
        {
            Idle, FollowingLane, ConfirmingNode, ApproachingTurnCenter,
            SearchingExitLane, VisualAlign, StraightThroughJunction,
            StraightToNextMarker, InspectingEquipment, Completed, Fault, AligningStartHeading, FollowingSegmentNfc
        }

        [Serializable]
        public struct ManeuverOverride
        {
            public PlantNodeId entryNode;
            public PlantNodeId junctionNode;
            public PlantNodeId exitNode;
            [Min(0f)] public float approachDistance;
            public bool useWorldTurnPosition;
            public Vector3 worldTurnPosition;
            [Range(0.05f, 1f)] public float approachCommand;
            [Range(0.05f, 1f)] public float searchTurnCommand;
        }

        private enum ActiveMission { None, SingleEdge, Perimeter, EquipmentA, EquipmentAAndB, EquipmentB }

        [Header("Connections")]
        [SerializeField] private PlantRouteGraph routeGraph;
        [SerializeField] private MissionRoutePlanner missionPlanner;
        [SerializeField] private SimulatedMarkerObservationSource markerSource;
        [SerializeField] private LaneFollowerController laneFollower;
        [SerializeField] private ShipRobot.ObstacleAvoidance.HumanAvoidanceAgent avoidanceAgent;

        [Header("Equipment A inspection")]
        [SerializeField] private Transform inspectionPointA1;
        [SerializeField] private Transform inspectionPointA2;
        [SerializeField] private Transform inspectionPointB1;
        [SerializeField] private Transform inspectionPointB2;
        [SerializeField, Min(0.1f)] private float inspectionReachDistance = 1.20f;

        [Header("Marker localization")]
        [SerializeField] private PlantNodeId initialNode = PlantNodeId.UnderMid;
        [FormerlySerializedAs("markerDecisionDistance")]
        [SerializeField, Min(0.1f)] private float markerDetectionDistance = 4.00f;
        [SerializeField, Min(0.1f)] private float junctionActionDistance = 1.20f;
        [SerializeField, Range(0f, 1f)] private float minimumMarkerConfidence = 0.45f;
        [SerializeField, Min(1)] private int requiredMarkerFrames = 3;

        [Header("Virtual NFC / ideal UWB simulation")]
        [SerializeField] private bool useIndoorSensorSimulation = true;
        [SerializeField] private SimulatedIndoorSensors indoorSensors;
        public bool UsesIndoorSensorSimulation => useIndoorSensorSimulation;

        [Header("Simulation world-coordinate turns")]
        [SerializeField] private bool useAbsoluteTurns = true;
        [SerializeField, Min(0.01f)] private float turnPositionTolerance = 0.35f;
        [SerializeField, Range(0.5f, 10f)] private float turnYawTolerance = 3f;
        [SerializeField, Min(1f)] private float absoluteTurnStageTimeout = 45f;
        private Vector3 absoluteTurnPosition;
        private NavigationMarker activeCentreMarker;
        private bool centreConfirmed;
        [Header("Tag transition pause")]
        [SerializeField, Min(0f)] private float entryTagDelay = 0.30f;
        [SerializeField, Min(0f)] private float centreTagDelay = 0.30f;
        private float entryTransitionAt = -1f;
        private float centreTransitionAt = -1f;
        private float pendingPathDeflection;
        private readonly FreshDetectionStreak entryQrStreak = new();
        private readonly FreshDetectionStreak centreQrStreak = new();
        private float absoluteStageStarted;
        [SerializeField, Range(0f, 1f)] private float turnExitConfidence = 0.60f;

        [Header("Approach to junction centre")]
        [SerializeField, Min(0f)] private float minimumApproachDistance = 0.10f;
        [SerializeField, Min(0f)] private float postAvoidanceMinimumApproachDistance = 0.50f;
        [SerializeField, Min(0.1f)] private float maximumApproachDistance = 1.35f;
        [SerializeField, Min(1)] private int requiredSideLossFrames = 30;
        [SerializeField, Range(0.05f, 1f)] private float approachCommand = 0.16f;

        [Header("Search for two exit boundaries")]
        [SerializeField, Range(0.05f, 1f)] private float searchTurnCommand = 0.20f;
        [SerializeField, Range(0f, 1f)] private float exitSearchMoveCommand = 0.40f;
        [SerializeField, Min(0f)] private float maximumExitSearchAdvanceDistance = 0.60f;
        [SerializeField, Range(0f, 90f)] private float minimumTurnBeforePair = 10f;
        [SerializeField, Range(45f, 175f)] private float maximumSearchTurn = 150f;
        [SerializeField, Range(5f, 90f)] private float exitHeadingTolerance = 35f;
        [SerializeField, Min(0.1f)] private float maximumExitLaneProbeDistance = 1.2f;
        [SerializeField, Range(0.05f, 1f)] private float exitLaneProbeCommand = 0.10f;
        [SerializeField, Range(0f, 1f)] private float minimumPairConfidence = 0.10f;
        [SerializeField, Min(1)] private int requiredPairFrames = 1;

        [Header("Virtual centre-line alignment")]
        [SerializeField, Min(0f)] private float visualLateralGain = 0.80f;
        [SerializeField, Min(0f)] private float visualHeadingGain = 0.70f;
        [SerializeField, Range(0.05f, 1f)] private float maximumVisualTurn = 0.40f;
        [SerializeField, Range(0f, 1f)] private float alignedLateralTolerance = 0.35f;
        [SerializeField, Range(0f, 1f)] private float alignedHeadingTolerance = 0.40f;
        [SerializeField, Range(0f, 1f)] private float boundaryAlignmentLateralTolerance = 0.08f;
        [SerializeField, Range(0f, 45f)] private float boundaryAlignmentAngleTolerance = 8f;
        [SerializeField, Range(0f, 1f)] private float boundaryAlignMoveCommand = 0.40f;
        [SerializeField, Range(0f, 1f)] private float partialAlignMoveCommand = 0.10f;
        [SerializeField, Range(0f, 1f)] private float maximumPartialAlignTurn = 0.20f;
        [SerializeField, Min(0f)] private float maximumPartialAlignTravel = 0.20f;
        [SerializeField, Min(0.1f)] private float alignmentObservationTimeout = 0.80f;
        [SerializeField, Min(0.1f)] private float maximumPartialAlignSeconds = 6f;
        [SerializeField, Min(1)] private int requiredPartialAlignedFrames = 3;
        [SerializeField, Range(5f, 45f)] private float alignmentExitHeadingTolerance = 15f;
        [SerializeField, Min(1)] private int requiredAlignedFrames = 2;
        [SerializeField, Min(0f)] private float minimumAlignTravel = 0.05f;
        [SerializeField, Min(0.1f)] private float maximumAlignTravel = 1.50f;
        [SerializeField, Min(1)] private int pairLostFrameLimit = 12;
        [SerializeField] private ManeuverOverride[] maneuverOverrides;

        [Header("Straight junction traversal")]
        [SerializeField, Range(0f, 45f)] private float straightDirectionTolerance = 25f;
        [SerializeField, Range(0.05f, 1f)] private float straightJunctionCommand = 0.14f;
        [SerializeField, Min(0f)] private float minimumStraightTravel = 0.20f;
        [SerializeField, Min(0.5f)] private float maximumStraightTravel = 3.0f;
        [SerializeField, Min(1)] private int requiredStraightLossFrames = 2;
        [SerializeField, Min(1)] private int requiredStraightReacquireFrames = 3;

        [Header("Equipment demo bottom crossing")]
        [SerializeField, Min(0f)] private float turnCentrePastMarkerDistance = 0.65f;
        [SerializeField, Min(2f)] private float rightBottomMaximumApproachDistance = 10f;

        [Header("No-lane marker fallback")]
        [SerializeField, Range(0.05f, 1f)] private float fallbackStraightCommand = 0.10f;
        [SerializeField, Min(1)] private int laneLostFramesBeforeFallback = 12;
        [SerializeField, Min(0.5f)] private float maximumFallbackDistance = 8f;
        [SerializeField, Min(1f)] private float maximumFallbackSeconds = 30f;

        [Header("UI")]
        [SerializeField] private bool showMissionPanel = true;
        [FormerlySerializedAs("autoStartEquipmentAMission")]
        [SerializeField] private bool autoStartEquipmentAAndBMission;

        public MissionState State { get; private set; }
        public PlantNodeId CurrentNode { get; private set; }
        public string StatusDetail => statusDetail;
        public event Action<string> InspectionCompleted;
        public string CurrentInspectionPointName => State == MissionState.InspectingEquipment && activeInspectionPoint != null ? activeInspectionPoint.name : null;
        public bool IsTrainingMission => avoidanceAgent != null && avoidanceAgent.IsTraining;
        public void UseDashboardStart() => autoStartEquipmentAAndBMission = false;
        public void StopFromDashboard()
        {
            avoidanceAgent?.CancelDemoAvoidance();
            ResetMission();
        }
        public bool IsFollowingEquipmentLeg(PlantNodeId from, PlantNodeId to) =>
            (activeMission == ActiveMission.EquipmentAAndB || activeMission == ActiveMission.EquipmentA || activeMission == ActiveMission.EquipmentB) &&
            (State == MissionState.FollowingLane || State == MissionState.StraightToNextMarker) &&
            CurrentNode == from &&
            targetRouteIndex < activeRoute.Count && activeRoute[targetRouteIndex] == to;
        public bool IsAlignedOnEquipmentLeg(PlantNodeId from, PlantNodeId to)
        {
            if (!IsFollowingEquipmentLeg(from, to) || State != MissionState.FollowingLane ||
                laneFollower == null || !laneFollower.HasUsableLane ||
                !laneFollower.TryGetBoundaryPair(minimumPairConfidence, out HsvLaneDetector.Detection detection))
                return false;

            return Mathf.Abs(detection.lateralError) <= alignedLateralTolerance &&
                   Mathf.Abs(detection.headingError) <= alignedHeadingTolerance;
        }
        public bool IsMotionRequested => State != MissionState.Idle &&
            State != MissionState.InspectingEquipment && State != MissionState.Completed &&
            State != MissionState.Fault;
        private bool avoidancePaused;
        private bool avoidanceInterruptedCurrentLeg;
        private Vector3 pausePosition;
        private float pauseStarted;

        private readonly List<PlantNodeId> activeRoute = new();
        private ActiveMission activeMission;
        private int targetRouteIndex;
        private int markerFrames;
        private int sideLossFrames;
        private int pairFrames;
        private int alignedFrames;
        private readonly FreshDetectionStreak exitPairStreak = new FreshDetectionStreak();
        private readonly FreshDetectionStreak alignmentStreak = new FreshDetectionStreak();
        private readonly FreshDetectionStreak alignmentLossStreak = new FreshDetectionStreak();
        private readonly FreshDetectionStreak partialAlignmentStreak = new FreshDetectionStreak();
        private float partialAlignmentStarted = -1f;
        private double lastAlignmentTimestamp = -1d;
        private float lastAlignmentImageTime;
        private int pairLostFrames;
        private int normalLaneLostFrames;
        private int straightLossFrames;
        private int straightReacquireFrames;
        private bool straightLaneGapObserved;
        private double lastSideObservationTimestamp = -1d;
        private double lastStraightObservationTimestamp = -1d;
        private float desiredExitYaw;
        private HsvLaneDetector.TrackingReference alignmentReference;
        private float searchStartYaw;
        private float minimumSearchAngle;
        private Vector3 exitLaneProbeStartPosition;
        private Vector3 exitSearchStartPosition;
        private bool exitLaneProbeStarted;
        private Vector3 motionStartPosition;
        private float fallbackStartTime;
        private float activeApproachDistance;
        private float activeMinimumApproachDistance;
        private float activeApproachCommand;
        private float activeSearchTurnCommand;
        private int activeInspectionIndex;
        private float inspectionTimeRemaining;
        private Transform activeInspectionPoint;
        private string statusDetail = "Ready";
        private NavigationCsvLog drivingLog;
        private bool drivingLogFailed;
        private float nextDrivingLogTime;
        private string lastLoggedState;
        private string nfcDecision = "not_checked";
        private string lastLoggedNfcDecision;
        [SerializeField, Min(0.02f)] private float diagnosticLogInterval = 0.05f;

        private static string LogNumber(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

        private void LateUpdate()
        {
            string state = State.ToString();
            bool changed = state != lastLoggedState;
            if (changed || nfcDecision != lastLoggedNfcDecision || Time.time >= nextDrivingLogTime)
                RecordDrivingLog(changed ? "state_change" : nfcDecision != lastLoggedNfcDecision ? "nfc_decision" : "sample");
        }

        private void RecordDrivingLog(string eventName)
        {
            if (drivingLogFailed || laneFollower == null) return;
            try
            {
                if (drivingLog == null)
                {
                    string root = Application.isEditor ? Path.GetDirectoryName(Application.dataPath) : Application.persistentDataPath;
                    string path = Path.Combine(root, "runtime", "navigation", "navigation-" +
                        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".csv");
                    drivingLog = new NavigationCsvLog(path, "utc", "simulation_time", "event", "source", "mission", "state",
                        "route", "node", "target", "x", "y", "z", "yaw", "desired_yaw", "yaw_error",
                        "reference", "lane_usable", "near_observed", "far_observed", "angle_valid", "angle_deg", "lateral", "confidence",
                        "pair", "pair_confidence", "image_timestamp", "image_age", "manual_control", "manual_move_request",
                        "manual_turn_request", "applied_move", "applied_turn", "drive_enabled", "safety_stop", "safety_scale",
                        "avoidance_paused", "align_travel", "search_advance", "lost_images", "align_frames",
                        "align_angle_gain", "align_lateral_gain", "align_turn_limit", "detail", "nfc_decision", "entry_delay_remaining", "centre_delay_remaining", "target_route_index", "indoor_mode", "absolute_mode", "centre_confirmed", "position_diagnostics_json");
                    Debug.Log($"Navigation driving log: {path}", this);
                }
                NavigationMarker diagnosticEntry = null;
                if (routeGraph != null && targetRouteIndex >= 0 && targetRouteIndex < activeRoute.Count)
                    routeGraph.TryGetEntry(CurrentNode, activeRoute[targetRouteIndex], out diagnosticEntry);
                string positionDiagnostics = NavigationPositionDiagnostics.Capture(laneFollower.transform, indoorSensors,
                    diagnosticEntry, diagnosticEntry != null ? diagnosticEntry.CentreMarker : null);
                bool usable = laneFollower.TryGetLaneDetection(out HsvLaneDetector.Detection d);
                Vector3 p = laneFollower.transform.position;
                float yaw = laneFollower.transform.eulerAngles.y;
                string target = targetRouteIndex >= 0 && targetRouteIndex < activeRoute.Count ? activeRoute[targetRouteIndex].ToString() : "";
                drivingLog.Write(DateTime.UtcNow.ToString("O"), LogNumber(Time.time), eventName, "unity_simulation",
                    activeMission.ToString(), State.ToString(), RouteText(), CurrentNode.ToString(), target,
                    LogNumber(p.x), LogNumber(p.y), LogNumber(p.z), LogNumber(yaw), LogNumber(desiredExitYaw),
                    LogNumber(Mathf.DeltaAngle(yaw, desiredExitYaw)), laneFollower.TrackingReferenceName, usable.ToString(), d.nearObserved.ToString(),
                    d.farObserved.ToString(), d.hasLineAngle.ToString(), d.hasLineAngle ? LogNumber(d.lineAngleRadians * Mathf.Rad2Deg) : "",
                    LogNumber(d.lateralError), LogNumber(d.confidence), d.hasBoundaryPair.ToString(), LogNumber(d.boundaryPairConfidence),
                    LogNumber(d.timestamp), LogNumber(Time.timeAsDouble - d.timestamp), laneFollower.IsManualControl.ToString(),
                    laneFollower.IsManualControl ? LogNumber(laneFollower.ManualMoveRequest) : "",
                    laneFollower.IsManualControl ? LogNumber(laneFollower.ManualTurnRequest) : "",
                    LogNumber(laneFollower.MoveCommand), LogNumber(laneFollower.TurnCommand), laneFollower.IsDriveEnabled.ToString(),
                    laneFollower.IsSafetyStopped.ToString(), LogNumber(laneFollower.SafetySpeedScale), avoidancePaused.ToString(),
                    LogNumber(PlanarDistance(motionStartPosition, p)), LogNumber(PlanarDistance(exitSearchStartPosition, p)),
                    pairLostFrames.ToString(), alignedFrames.ToString(), LogNumber(visualHeadingGain), LogNumber(visualLateralGain),
                    LogNumber(maximumVisualTurn), statusDetail, nfcDecision,
                    entryTransitionAt < 0f ? "" : LogNumber(Mathf.Max(0f, entryTransitionAt - Time.time)),
                    centreTransitionAt < 0f ? "" : LogNumber(Mathf.Max(0f, centreTransitionAt - Time.time)),
                    targetRouteIndex.ToString(), useIndoorSensorSimulation.ToString(), useAbsoluteTurns.ToString(),
                    centreConfirmed.ToString(), positionDiagnostics);
                lastLoggedState = State.ToString();
                lastLoggedNfcDecision = nfcDecision;
                nextDrivingLogTime = Time.time + (State == MissionState.Idle || State == MissionState.Fault || State == MissionState.Completed ? 1f : diagnosticLogInterval);
            }
            catch (Exception ex)
            {
                drivingLogFailed = true;
                try { drivingLog?.Dispose(); } catch { }
                drivingLog = null;
                Debug.LogWarning($"Navigation driving log disabled: {ex.GetType().Name}", this);
            }
        }

        private void OnDisable()
        {
            RecordDrivingLog("component_disabled");
            try { drivingLog?.Dispose(); } catch (Exception ex) { Debug.LogWarning($"Navigation log close failed: {ex.GetType().Name}", this); }
            drivingLog = null;
        }
        private GUIStyle titleStyle;
        private GUIStyle statusStyle;

        public bool AllowsMarkerDetection(NavigationMarker marker)
        {
            if (marker == null) return false;
            if (State == MissionState.ApproachingTurnCenter)
                return marker.Role == NavigationMarker.MarkerRole.Centre &&
                    marker == activeCentreMarker && marker.NodeId == CurrentNode;
            return marker.Role == NavigationMarker.MarkerRole.Entry;
        }

        private void PrepareIndoorSensors()
        {
            if (!useIndoorSensorSimulation || laneFollower == null) return;
            useAbsoluteTurns = true;
            if (indoorSensors == null)
                indoorSensors = laneFollower.GetComponent<SimulatedIndoorSensors>() ??
                    laneFollower.gameObject.AddComponent<SimulatedIndoorSensors>();
        }

        private void Awake()
        {
            PrepareIndoorSensors();
            markerSource?.BindNavigation(this);
            CurrentNode = initialNode;
            State = MissionState.Idle;
            laneFollower?.SetDriveEnabled(false);
        }

        private void Start()
        {
            if (autoStartEquipmentAAndBMission && (avoidanceAgent == null || !avoidanceAgent.IsTraining))
                StartEquipmentAAndBMission();
        }

        private void Update()
        {
            if (IsMotionRequested && avoidanceAgent != null && !avoidanceAgent.IsTraining &&
                (avoidanceAgent.HasAvoidanceControl || laneFollower.IsSafetyStopped))
            {
                if (avoidanceAgent.HasAvoidanceControl)
                    avoidanceInterruptedCurrentLeg = true;
                if (!avoidancePaused)
                {
                    avoidancePaused = true;
                    pausePosition = laneFollower.transform.position;
                    pauseStarted = Time.time;
                }
                return;
            }
            if (avoidancePaused)
            {
                // Avoidance travel/time is not junction approach/fallback progress.
                motionStartPosition += laneFollower.transform.position - pausePosition;
                fallbackStartTime += Time.time - pauseStarted;
                absoluteStageStarted += Time.time - pauseStarted;
                if (entryTransitionAt >= 0f) entryTransitionAt = Time.time + entryTagDelay;
                centreTransitionAt = -1f;
                exitPairStreak.Reset();
                entryQrStreak.Reset();
                centreQrStreak.Reset();
                markerFrames = sideLossFrames = pairFrames = alignedFrames = normalLaneLostFrames = 0;
                avoidancePaused = false;
            }
            if (entryTransitionAt >= 0f && IsMotionRequested)
            {
                laneFollower.SetManualCommand(0f, 0f);
                statusDetail = $"Entry sensor confirmed; transition in {Mathf.Max(0f, entryTransitionAt - Time.time):F2}s";
                if (Time.time >= entryTransitionAt)
                {
                    entryTransitionAt = -1f;
                    nfcDecision = "entry:delay_completed";
                    RecordDrivingLog("entry_transition_ready");
                    ArriveAtTargetNode();
                }
                return;
            }
            switch (State)
            {
                case MissionState.FollowingSegmentNfc:
                    UpdateDepartureSegments();
                    return;
                case MissionState.AligningStartHeading:
                    UpdateStartHeading();
                    return;
                case MissionState.ApproachingTurnCenter:
                    UpdateApproach();
                    return;
                case MissionState.SearchingExitLane:
                    UpdateExitLaneSearch();
                    return;
                case MissionState.VisualAlign:
                    UpdateVisualAlignment();
                    return;
                case MissionState.StraightThroughJunction:
                    UpdateStraightThroughJunction();
                    return;
                case MissionState.StraightToNextMarker:
                    UpdateStraightToNextMarker();
                    return;
                case MissionState.InspectingEquipment:
                    UpdateEquipmentInspection();
                    return;
            }

            if (State != MissionState.FollowingLane && State != MissionState.ConfirmingNode)
                return;
            if (!ConnectionsReady())
            {
                Fail("Navigation setup is incomplete");
                return;
            }

            if (TryBeginEquipmentInspection())
                return;

            if (useAbsoluteTurns && TryEnterQrJunction())
                return;
            if (!useAbsoluteTurns && TryArriveAtTargetAfterAvoidance())
                return;

            if (useIndoorSensorSimulation)
            {
                CountUnusableLaneFrames();
                statusDetail = $"SIM NFC: following to entry {activeRoute[targetRouteIndex]}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback) EnterStraightMarkerFallback();
                return;
            }

            PlantNodeId target = activeRoute[targetRouteIndex];
            bool targetVisible = markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                                 observation.nodeId == target &&
                                 observation.confidence >= minimumMarkerConfidence;
            if (!targetVisible)
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                CountUnusableLaneFrames();
                statusDetail = $"Following to ID {(int)target} ({target}), laneLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback();
                return;
            }

            float distance = observation.cameraRelativePosition.magnitude;
            statusDetail = $"ID {(int)target} visible at {distance:F2} m";
            if (distance > markerDetectionDistance)
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                CountUnusableLaneFrames();
                statusDetail = $"Target far at {distance:F2} m, laneLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback();
                return;
            }

            if (distance > junctionActionDistance || (useAbsoluteTurns && targetRouteIndex < activeRoute.Count - 1))
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                CountUnusableLaneFrames();
                statusDetail = $"ID {(int)target} detected at {distance:F2} m; action at {junctionActionDistance:F2} m, " +
                               $"laneLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback();
                return;
            }

            normalLaneLostFrames = 0;
            State = MissionState.ConfirmingNode;
            markerFrames++;
            if (markerFrames >= requiredMarkerFrames)
                ArriveAtTargetNode();
        }

        private void CountUnusableLaneFrames()
        {
            // The mission and motor controller must agree on whether lane following
            // can actually move the robot. A visible boundary pair alone is not enough.
            bool laneUsable = laneFollower.HasUsableLane &&
                              laneFollower.TryGetBoundaryPair(minimumPairConfidence, out _);
            normalLaneLostFrames = laneUsable ? 0 : normalLaneLostFrames + 1;
        }

        public void StartSingleEdgeMission() =>
            StartRoute(new[] { CurrentNode, PlantNodeId.UpperMid }, ActiveMission.SingleEdge);

        [ContextMenu("Start Perimeter Mission")]
        public void StartPerimeterMission()
        {
            if (missionPlanner == null)
            {
                Fail("Mission planner is missing");
                return;
            }
            if (!useIndoorSensorSimulation && markerSource != null && markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                observation.confidence >= minimumMarkerConfidence &&
                observation.cameraRelativePosition.magnitude <= junctionActionDistance * 1.5f)
                CurrentNode = observation.nodeId;
            StartRoute(missionPlanner.BuildPerimeterRoute(CurrentNode), ActiveMission.Perimeter);
        }

        [ContextMenu("Start Equipment A Mission")]
        public void StartEquipmentAMission()
        {
            StartSelectedEquipmentMission(ActiveMission.EquipmentA);
        }

        [ContextMenu("Start Equipment B Mission")]
        public void StartEquipmentBMission() => StartSelectedEquipmentMission(ActiveMission.EquipmentB);

        [ContextMenu("Start Equipment A And B Mission")]
        public void StartEquipmentAAndBMission() => StartSelectedEquipmentMission(ActiveMission.EquipmentAAndB);

        private void StartSelectedEquipmentMission(ActiveMission selection)
        {
            if (CurrentNode != PlantNodeId.UnderMid)
            {
                Fail("Inspection missions must start at node 6 (UnderMid). Restart Unity Play at the base.");
                return;
            }
            if (missionPlanner == null || routeGraph == null ||
                inspectionPointA1 == null || inspectionPointA2 == null)
            {
                Fail("Equipment A/B inspection setup is incomplete");
                return;
            }

            if (!routeGraph.TryGetMarker(PlantNodeId.UpperLeft, out NavigationMarker leftMarker) ||
                !routeGraph.TryGetMarker(PlantNodeId.UpperRight, out NavigationMarker rightMarker))
            {
                Fail("Equipment B placement requires both upper route markers");
                return;
            }

            // The A points sit under a translated waypoint parent, so use the
            // actual lane-to-lane offset rather than negating world X.
            float aisleOffsetX = rightMarker.transform.position.x - leftMarker.transform.position.x;
            if (inspectionPointB1 == null)
                inspectionPointB1 = CreateBInspectionPoint(inspectionPointA1, "inspect_point_B1", aisleOffsetX);
            if (inspectionPointB2 == null)
                inspectionPointB2 = CreateBInspectionPoint(inspectionPointA2, "inspect_point_B2", aisleOffsetX);

            PlantMission routeMission = selection switch
            {
                ActiveMission.EquipmentA => PlantMission.InspectEquipmentA,
                ActiveMission.EquipmentB => PlantMission.InspectEquipmentB,
                _ => PlantMission.InspectEquipmentAAndB
            };
            StartRoute(missionPlanner.BuildMissionRoute(routeMission, CurrentNode), selection);
            if (State == MissionState.Fault) return;
            if (!TryCalculateEdgeYaw(activeRoute[0], activeRoute[1], out desiredExitYaw))
            { Fail("Initial route heading unavailable"); return; }
            if (Mathf.Abs(Mathf.DeltaAngle(laneFollower.transform.eulerAngles.y, desiredExitYaw)) > 3f)
            {
                absoluteStageStarted = Time.time;
                State = MissionState.AligningStartHeading;
                laneFollower.SetManualCommand(0f, 0f);
                statusDetail = $"Aligning departure heading to {activeRoute[1]} ({desiredExitYaw:F1} deg)";
            }
        }

        private readonly List<NavigationMarker> departureZones = new();
        private int departureZoneIndex;
        private float departurePauseUntil = -1f;
        private GameObject departureZoneRoot;

        private void BeginDepartureSegments()
        {
            if (!routeGraph.TryGetMarker(activeRoute[1], out NavigationMarker destination))
            { Fail("Departure NFC destination missing"); return; }
            if (departureZoneRoot != null) Destroy(departureZoneRoot);
            departureZoneRoot = new GameObject("NFC_B_Departure_Segments_SIM");
            departureZones.Clear();
            Vector3 start = indoorSensors.ReaderPosition;
            Vector3 end = destination.transform.position;
            end.y = start.y;
            int count = Mathf.Max(1, Mathf.CeilToInt(PlanarDistance(start, end) / 1.5f));
            for (int i = 1; i < count; i++)
                departureZones.Add(NavigationMarker.CreateSegment(departureZoneRoot.transform,
                    $"NFC_B_Departure_{i:00}_SIM", activeRoute[1], Vector3.Lerp(start, end, (float)i / count)));
            departureZoneIndex = 0;
            departurePauseUntil = -1f;
            absoluteStageStarted = Time.time;
            State = MissionState.FollowingSegmentNfc;
            laneFollower.SetManualCommand(0f, 0f);
        }

        private void UpdateDepartureSegments()
        {
            if (Time.time - absoluteStageStarted > absoluteTurnStageTimeout)
            { Fail("Departure segment NFC timeout"); return; }
            if (departureZoneIndex >= departureZones.Count)
            {
                if (TryEnterQrJunction()) return;
                if (!routeGraph.TryGetMarker(activeRoute[1], out NavigationMarker entry))
                { Fail("Departure entry NFC missing"); return; }
                DriveWithIndoorPosition(entry.transform.position, approachCommand);
                statusDetail = "Departure final entry NFC; " + statusDetail;
                return;
            }
            NavigationMarker zone = departureZones[departureZoneIndex];
            if (indoorSensors.IsInside(zone))
            {
                laneFollower.SetManualCommand(0f, 0f);
                if (departurePauseUntil < 0f) departurePauseUntil = Time.time + entryTagDelay;
                nfcDecision = $"segment:{zone.name}:inside_delay_pending";
                statusDetail = nfcDecision;
                if (Time.time < departurePauseUntil) return;
                RecordDrivingLog("segment_nfc_confirmed");
                departureZoneIndex++;
                departurePauseUntil = -1f;
                absoluteStageStarted = Time.time;
                return;
            }
            departurePauseUntil = -1f;
            nfcDecision = $"segment:{zone.name}:outside_radius";
            DriveWithIndoorPosition(zone.transform.position, approachCommand);
            statusDetail = $"Segment {departureZoneIndex + 1}/{departureZones.Count}: {zone.name}; " + statusDetail;
        }

        private void UpdateStartHeading()
        {
            float error = Mathf.DeltaAngle(laneFollower.transform.eulerAngles.y, desiredExitYaw);
            if (Time.time - absoluteStageStarted > absoluteTurnStageTimeout)
            { Fail("Initial heading alignment timed out"); return; }
            if (Mathf.Abs(error) <= 3f)
            {
                laneFollower.SetManualCommand(0f, 0f);
                State = MissionState.FollowingLane;
                statusDetail = $"Following to {activeRoute[targetRouteIndex]}";
                laneFollower.ResumeLaneFollowing();
                return;
            }
            statusDetail = $"Aligning departure heading: error={error:F1} deg";
            laneFollower.SetManualCommand(0f, Mathf.Sign(error) * searchTurnCommand *
                Mathf.Clamp(Mathf.Abs(error) / 30f, 0.2f, 1f));
        }

        private static Transform CreateBInspectionPoint(Transform source, string pointName, float aisleOffsetX)
        {
            Transform point = Instantiate(source, source.parent);
            point.name = pointName;
            Vector3 position = source.position;
            point.position = new Vector3(position.x + aisleOffsetX, position.y, position.z);
            return point;
        }

        private void StartRoute(IReadOnlyList<PlantNodeId> route, ActiveMission mission)
        {
            PrepareIndoorSensors();
            if (!ConnectionsReady() || route == null || route.Count < 2)
            {
                Fail("Route is empty or setup is incomplete");
                return;
            }
            nfcDecision = "not_checked";
            entryTransitionAt = centreTransitionAt = -1f;
            activeRoute.Clear();
            for (int i = 0; i < route.Count; i++) activeRoute.Add(route[i]);
            if (useAbsoluteTurns)
            {
                for (int i = 1; i < activeRoute.Count - 1; i++)
                    if (TryGetDemoTurnCentre(activeRoute[i - 1], activeRoute[i], out Vector3 point, out _) &&
                        routeGraph.TryGetMarker(activeRoute[i], out NavigationMarker marker))
                    {
                        foreach (ManeuverOverride maneuver in maneuverOverrides ?? Array.Empty<ManeuverOverride>())
                            if (maneuver.entryNode == activeRoute[i - 1] && maneuver.junctionNode == activeRoute[i] &&
                                maneuver.exitNode == activeRoute[i + 1] && maneuver.useWorldTurnPosition)
                            { point = maneuver.worldTurnPosition; break; }
                        marker.EnsureCentre(point);
                    }
                markerSource?.RefreshMarkerList();
            }
            entryQrStreak.Reset();
            activeMission = mission;
            avoidanceInterruptedCurrentLeg = false;
            CurrentNode = activeRoute[0];
            targetRouteIndex = 1;
            markerFrames = 0;
            normalLaneLostFrames = 0;
            activeInspectionIndex = 0;
            activeInspectionPoint = null;
            State = MissionState.FollowingLane;
            statusDetail = $"Following to ID {(int)activeRoute[1]} ({activeRoute[1]})";
            laneFollower.ResumeLaneFollowing();
        }

        private bool TryBeginEquipmentInspection()
        {
            int inspectionCount = activeMission == ActiveMission.EquipmentAAndB ? 4 :
                (activeMission == ActiveMission.EquipmentA || activeMission == ActiveMission.EquipmentB) ? 2 : 0;
            if (activeInspectionIndex >= inspectionCount)
                return false;

            int pointIndex = activeMission == ActiveMission.EquipmentB ? 3 - activeInspectionIndex : activeInspectionIndex;
            Transform target = pointIndex switch
            {
                0 => inspectionPointA1,
                1 => inspectionPointA2,
                2 => inspectionPointB2,
                _ => inspectionPointB1
            };
            Collider footprint = laneFollower.GetComponent<Collider>();
            Vector3 robotCentre = footprint is BoxCollider box
                ? laneFollower.transform.TransformPoint(box.center)
                : laneFollower.transform.position;
            if (target == null || PlanarDistance(robotCentre, target.position) > inspectionReachDistance)
                return false;

            activeInspectionPoint = target;
            InspectionPoint point = target.GetComponent<InspectionPoint>();
            inspectionTimeRemaining = point != null ? Mathf.Max(0f, point.inspectionTime) : 3f;
            State = MissionState.InspectingEquipment;
            avoidanceAgent?.CancelDemoAvoidance();
            statusDetail = $"Inspecting {target.name}: {inspectionTimeRemaining:F1} s";
            laneFollower.SetDriveEnabled(false);
            return true;
        }

        private void UpdateEquipmentInspection()
        {
            inspectionTimeRemaining = Mathf.Max(0f, inspectionTimeRemaining - Time.deltaTime);
            string pointName = activeInspectionPoint != null ? activeInspectionPoint.name : "inspection point";
            statusDetail = $"Inspecting {pointName}: {inspectionTimeRemaining:F1} s";
            if (inspectionTimeRemaining > 0f)
                return;

            activeInspectionIndex++;
            activeInspectionPoint = null;
            State = MissionState.FollowingLane;
            PlantNodeId target = activeRoute[targetRouteIndex];
            statusDetail = $"Inspection complete; following ID {(int)target} ({target})";
            laneFollower.ResumeLaneFollowing();
            // Monitoring failures must not strand the navigation state machine.
            try { InspectionCompleted?.Invoke(pointName); }
            catch (Exception ex) { Debug.LogException(ex, this); }
        }

        private void ArriveAtTargetNode()
        {
            bool arrivedAfterAvoidance = avoidanceInterruptedCurrentLeg;
            avoidanceInterruptedCurrentLeg = false;
            CurrentNode = activeRoute[targetRouteIndex];
            markerFrames = 0;
            if (targetRouteIndex >= activeRoute.Count - 1)
            {
                CompleteMission();
                return;
            }

            PlantNodeId entry = activeRoute[targetRouteIndex - 1];
            PlantNodeId exit = activeRoute[targetRouteIndex + 1];
            if (!TryCalculateEdgeYaw(CurrentNode, exit, out desiredExitYaw))
            {
                Fail($"Missing graph direction for {CurrentNode} -> {exit}");
                return;
            }

            if (!TryCalculatePathDeflection(entry, CurrentNode, exit, out float pathDeflection))
            {
                Fail($"Missing graph direction for {entry} -> {CurrentNode} -> {exit}");
                return;
            }

            pendingPathDeflection = pathDeflection;
            if (!useAbsoluteTurns && pathDeflection <= straightDirectionTolerance)
            {
                BeginStraightThroughJunction(pathDeflection);
                return;
            }

            ResolveManeuver(entry, CurrentNode, exit);
            if (useAbsoluteTurns)
            {
                if (!routeGraph.TryGetEntry(entry, CurrentNode, out NavigationMarker entryMarker) ||
                    entryMarker.CentreMarker == null)
                {
                    Fail("Central QR is not configured");
                    return;
                }
                activeCentreMarker = entryMarker.CentreMarker;
                absoluteTurnPosition = activeCentreMarker.transform.position;
                centreTransitionAt = -1f;
                centreConfirmed = false;
                centreQrStreak.Reset();
                absoluteStageStarted = Time.time;
            }
            // Use the route's turn direction, not the remaining heading error on a re-search.
            TryCalculateEdgeYaw(entry, CurrentNode, out float incomingYaw);
            alignmentReference = Mathf.DeltaAngle(incomingYaw, desiredExitYaw) < 0f
                ? HsvLaneDetector.TrackingReference.RightBoundary
                : HsvLaneDetector.TrackingReference.LeftBoundary;
            activeMinimumApproachDistance = arrivedAfterAvoidance
                ? Mathf.Max(minimumApproachDistance,
                    Mathf.Min(postAvoidanceMinimumApproachDistance, activeApproachDistance - 0.1f))
                : minimumApproachDistance;
            sideLossFrames = 0;
            lastSideObservationTimestamp = -1d;
            motionStartPosition = laneFollower.transform.position;
            State = MissionState.ApproachingTurnCenter;
            laneFollower.SetManualCommand(activeApproachCommand, 0f);
        }

        private void UpdateApproach()
        {
            if (useAbsoluteTurns)
            {
                UpdateAbsoluteApproach();
                return;
            }
            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            bool observationFresh = laneFollower.TryGetBoundarySides(out HsvLaneDetector.Detection detection);
            bool bothSidesVisible = observationFresh &&
                                    detection.leftBoundaryVisible &&
                                    detection.rightBoundaryVisible;
            bool mayAcceptSideLoss = travelled >= activeMinimumApproachDistance;
            bool newObservation = observationFresh &&
                                  detection.timestamp > lastSideObservationTimestamp;
            if (newObservation)
            {
                lastSideObservationTimestamp = detection.timestamp;
                sideLossFrames = !bothSidesVisible
                    ? sideLossFrames + 1
                    : 0;
            }

            statusDetail =
                $"Approach {travelled:F2}/{activeApproachDistance:F2} m " +
                $"(turn after {activeMinimumApproachDistance:F2}), " +
                $"L={(detection.leftBoundaryVisible ? detection.leftBoundaryConfidence.ToString("F2") : "NO")}, " +
                $"R={(detection.rightBoundaryVisible ? detection.rightBoundaryConfidence.ToString("F2") : "NO")}, " +
                $"sideLost={sideLossFrames}/{requiredSideLossFrames}";

            if (mayAcceptSideLoss && sideLossFrames >= requiredSideLossFrames)
            {
                BeginExitLaneSearch();
                return;
            }

            if (travelled >= activeApproachDistance)
            {
                // Some corners keep both painted entry lines visible all the way
                // to the turn centre. Use the same bounded-distance fallback at
                // every corner instead of faulting before the turn can begin.
                BeginExitLaneSearch();
                return;
            }
            laneFollower.SetManualCommand(activeApproachCommand, 0f);
        }

        private bool TryEnterQrJunction()
        {
            if (useIndoorSensorSimulation)
            {
                if (targetRouteIndex >= activeRoute.Count) { nfcDecision = "entry:no_target"; return false; }
                if (!routeGraph.TryGetEntry(CurrentNode, activeRoute[targetRouteIndex], out NavigationMarker zone))
                { nfcDecision = "entry:zone_missing"; return false; }
                if (zone.Role != NavigationMarker.MarkerRole.Entry) { nfcDecision = "entry:wrong_role"; return false; }
                if (indoorSensors == null || !indoorSensors.isActiveAndEnabled) { nfcDecision = "entry:sensor_disabled"; return false; }
                if (!zone.isActiveAndEnabled) { nfcDecision = "entry:zone_disabled"; return false; }
                if (!indoorSensors.IsInside(zone)) { nfcDecision = "entry:outside_radius"; return false; }
                nfcDecision = "entry:detected_delay_pending";
                entryTransitionAt = Time.time + entryTagDelay;
                laneFollower.SetManualCommand(0f, 0f);
                statusDetail = $"SIM NFC entry {zone.NodeId}; pause {entryTagDelay:F2}s";
                return true;
            }
            if (targetRouteIndex >= activeRoute.Count - 1) return false;
            if (!routeGraph.TryGetEntry(CurrentNode, activeRoute[targetRouteIndex], out NavigationMarker entryMarker))
                return false;
            bool seen = markerSource.TryObserveMarker(entryMarker, out MarkerObservation observation) &&
                observation.confidence >= minimumMarkerConfidence &&
                observation.cameraRelativePosition.magnitude <= markerDetectionDistance;
            int count = entryQrStreak.Observe(observation.timestamp, seen);
            if (!seen) return false;
            // A visible entry QR owns motion even when the lane has already ended.
            laneFollower.SetManualCommand(approachCommand, 0f);
            statusDetail = $"Entry QR {entryMarker.NodeId}: {count}/{requiredMarkerFrames}; advancing";
            if (count >= requiredMarkerFrames)
            {
                entryQrStreak.Reset();
                entryTransitionAt = Time.time + entryTagDelay;
                laneFollower.SetManualCommand(0f, 0f);
                statusDetail = $"Entry QR confirmed; pause {entryTagDelay:F2}s";
            }
            return true;
        }

        // Simulation ground truth: the target stays in world space even after avoidance.
        private void UpdateAbsoluteApproach()
        {
            Vector3 position = laneFollower.transform.position;
            if (useIndoorSensorSimulation && !indoorSensors.TryGetPosition(out position))
            { Fail("SIM UWB position unavailable"); return; }
            Vector3 delta = Vector3.ProjectOnPlane(absoluteTurnPosition - position, Vector3.up);
            float distance = delta.magnitude;
            bool insideCentre = useIndoorSensorSimulation && activeCentreMarker != null &&
                activeCentreMarker.Role == NavigationMarker.MarkerRole.Centre &&
                activeCentreMarker.NodeId == CurrentNode && indoorSensors.IsInside(activeCentreMarker);
            if (useIndoorSensorSimulation)
            {
                centreConfirmed = insideCentre;
                nfcDecision = insideCentre ? "centre:inside_delay_pending" : "centre:not_inside_or_invalid";
            }
            else
            {
                bool seen = markerSource.TryObserveMarker(activeCentreMarker, out MarkerObservation observation) &&
                    observation.confidence >= minimumMarkerConfidence;
                if (centreQrStreak.Observe(observation.timestamp, seen) >= requiredMarkerFrames) centreConfirmed = true;
            }
            if (Time.time - absoluteStageStarted > absoluteTurnStageTimeout)
            {
                Fail($"World turn point not reached: target={absoluteTurnPosition}, remaining={distance:F2} m");
                return;
            }
            // Allow 15 cm of drift during the stop delay without restarting arrival.
            bool settlingAtCentre = centreTransitionAt >= 0f && distance <= turnPositionTolerance + 0.15f;
            if (useIndoorSensorSimulation ? insideCentre : distance <= turnPositionTolerance || settlingAtCentre)
            {
                laneFollower.SetManualCommand(0f, 0f);
                if (!centreConfirmed)
                {
                    statusDetail = "At central point; waiting for central QR confirmation";
                    return;
                }
                if (centreTransitionAt < 0f) centreTransitionAt = Time.time + centreTagDelay;
                if (Time.time < centreTransitionAt)
                {
                    statusDetail = $"Centre sensor reached; transition in {centreTransitionAt - Time.time:F2}s";
                    return;
                }
                centreTransitionAt = -1f;
                if (pendingPathDeflection <= straightDirectionTolerance)
                    FinishAlignment("central QR reached; straight exit");
                else
                    BeginExitLaneSearch();
                return;
            }
            centreTransitionAt = -1f;
            if (useIndoorSensorSimulation)
            {
                DriveWithIndoorPosition(absoluteTurnPosition, activeApproachCommand);
                return;
            }
            float error = Vector3.SignedAngle(laneFollower.transform.forward, delta, Vector3.up);
            float turn = Mathf.Clamp(error / 45f, -1f, 1f) * activeSearchTurnCommand;
            float move = Mathf.Abs(error) > 30f ? 0f : activeApproachCommand * Mathf.Clamp(distance / 0.6f, 0.2f, 1f);
            statusDetail = $"Central QR confirmed={centreConfirmed}, target={absoluteTurnPosition}, remaining={distance:F2} m, heading={error:F1} deg";
            laneFollower.SetManualCommand(move, turn);
        }

        private void DriveWithIndoorPosition(Vector3 goal, float speed)
        {
            if (!indoorSensors.TryGetPosition(out Vector3 position))
            { Fail("SIM UWB position unavailable"); return; }
            Vector3 delta = Vector3.ProjectOnPlane(goal - position, Vector3.up);
            float error = Vector3.SignedAngle(indoorSensors.HeadingForward, delta, Vector3.up);
            float turn = Mathf.Clamp(error / 45f, -1f, 1f) * searchTurnCommand;
            float move = Mathf.Abs(error) > 30f ? 0f : speed * Mathf.Clamp(delta.magnitude / 1.2f, 0.15f, 1f);
            laneFollower.SetManualCommand(move, turn);
            statusDetail = $"SIM UWB ideal position={position}, remaining={delta.magnitude:F2} m, heading={error:F1}; awaiting SIM NFC";
        }

        private void UpdateAbsoluteRotation()
        {
            Vector3 position = laneFollower.transform.position;
            if (useIndoorSensorSimulation && !indoorSensors.TryGetPosition(out position))
            { Fail("SIM UWB position unavailable"); return; }
            float distance = PlanarDistance(absoluteTurnPosition, position);
            float error = Mathf.DeltaAngle(laneFollower.transform.eulerAngles.y, desiredExitYaw);
            if (Time.time - absoluteStageStarted > absoluteTurnStageTimeout)
            {
                Fail($"World turn timeout: position error={distance:F2} m, yaw error={error:F1} deg");
                return;
            }
            // Do not chase a point while turning: stop if physics/avoidance displaced the robot.
            if (distance > Mathf.Max(0.4f, turnPositionTolerance * 2f))
            {
                Fail($"Robot left world turn point by {distance:F2} m; restart after checking the path");
                return;
            }
            bool usable = laneFollower.TryGetLaneDetection(out HsvLaneDetector.Detection detection);
            float threshold = Mathf.Max(turnExitConfidence, laneFollower.EffectiveMinimumConfidence);
            bool confident = usable && detection.confidence >= threshold;
            pairFrames = exitPairStreak.Observe(detection.timestamp, confident);
            statusDetail = $"World turn target={absoluteTurnPosition}, position error={distance:F2} m, " +
                $"confidence={detection.confidence:F2}/{threshold:F2}, usable={usable}, stable={pairFrames}/{requiredPairFrames}, " +
                $"yaw={laneFollower.transform.eulerAngles.y:F1}/{desiredExitYaw:F1}";
            // Hold as soon as confidence is high; require distinct fresh images before driving.
            // Absolute yaw only bounds the search; it no longer completes the turn.
            bool hold = confident || Mathf.Abs(error) <= turnYawTolerance;
            laneFollower.SetManualCommand(0f, hold ? 0f : Mathf.Clamp(error / 30f, -1f, 1f) * activeSearchTurnCommand);
            if (pairFrames >= requiredPairFrames)
                FinishAlignment("lane confidence confirmed; resume forward lane following");
        }

        private bool TryGetDemoTurnCentre(PlantNodeId entry, PlantNodeId junction,
            out Vector3 turnCentre, out Vector3 incomingDirection)
        {
            turnCentre = default;
            incomingDirection = default;
            if (!routeGraph.TryGetMarker(entry, out NavigationMarker entryMarker) ||
                !routeGraph.TryGetMarker(junction, out NavigationMarker cornerMarker))
                return false;

            incomingDirection = Vector3.ProjectOnPlane(
                cornerMarker.transform.position - entryMarker.transform.position, Vector3.up).normalized;
            if (incomingDirection.sqrMagnitude < 0.5f)
                return false;
            turnCentre = cornerMarker.transform.position + incomingDirection * turnCentrePastMarkerDistance;
            return true;
        }

        private void BeginExitLaneSearch()
        {
            searchStartYaw = laneFollower.transform.eulerAngles.y;
            exitSearchStartPosition = laneFollower.transform.position;
            float plannedAngle = Mathf.DeltaAngle(searchStartYaw, desiredExitYaw);
            minimumSearchAngle = Mathf.Min(minimumTurnBeforePair, Mathf.Abs(plannedAngle) * 0.45f);
            pairFrames = 0;
            exitPairStreak.Reset();
            exitLaneProbeStarted = false;
            State = MissionState.SearchingExitLane;
            Debug.Log($"Navigation transition: exit search node={CurrentNode}, target={activeRoute[targetRouteIndex]}, " +
                      $"yaw={searchStartYaw:F1}, desiredYaw={desiredExitYaw:F1}, position={laneFollower.transform.position}", this);
            if (useAbsoluteTurns)
            {
                absoluteStageStarted = Time.time;
                exitPairStreak.Reset();
                laneFollower.SetManualCommand(0f, 0f);
            }
            else
                laneFollower.SetManualCommand(GetExitSearchMoveCommand(), GetExitHeadingTurnCommand(plannedAngle));
        }

        private void UpdateExitLaneSearch()
        {
            if (useAbsoluteTurns)
            {
                UpdateAbsoluteRotation();
                return;
            }
            float turned = Mathf.Abs(Mathf.DeltaAngle(searchStartYaw, laneFollower.transform.eulerAngles.y));
            if (turned > maximumSearchTurn)
            {
                Fail($"No usable central exit lane within {maximumSearchTurn:F0} deg");
                return;
            }

            float signedHeadingError = Mathf.DeltaAngle(
                laneFollower.transform.eulerAngles.y, desiredExitYaw);
            float headingError = Mathf.Abs(signedHeadingError);
            bool headingReady = headingError <= Mathf.Min(exitHeadingTolerance, 12f);
            bool trackingUsable = laneFollower.TryGetLaneDetection(out HsvLaneDetector.Detection detection);
            // Vision can hand over at any rotation angle, as soon as tracking can use it.
            bool pairVisible = trackingUsable && laneFollower.TrackingReferenceName == HsvLaneDetector.TrackingReference.LaneCentre.ToString();
            pairFrames = exitPairStreak.Observe(detection.timestamp, pairVisible);
            if (headingReady && !exitLaneProbeStarted)
            {
                exitLaneProbeStarted = true;
                exitLaneProbeStartPosition = laneFollower.transform.position;
            }
            float probeDistance = exitLaneProbeStarted
                ? PlanarDistance(exitLaneProbeStartPosition, laneFollower.transform.position)
                : 0f;
            float effectivePairConfidence = Mathf.Max(
                detection.boundaryPairConfidence, detection.confidence);
            statusDetail =
                $"Search: angle {turned:F1}/{minimumSearchAngle:F1}, exit yaw error={headingError:F0} " +
                $"trackingReady={(pairVisible ? "YES" : "NO")}, trackingMin={laneFollower.EffectiveMinimumConfidence:F2}, " +
                $"pair={(detection.hasBoundaryPair ? "YES" : "NO")}, conf={detection.confidence:F2}, " +
                $"pairConf={detection.boundaryPairConfidence:F2}, effective={effectivePairConfidence:F2}, " +
                $"searchAdvance={PlanarDistance(exitSearchStartPosition, laneFollower.transform.position):F2}/{maximumExitSearchAdvanceDistance:F2} m, " +
                $"centralConfidenceStable={pairFrames}/{requiredPairFrames}, imageAge={Time.timeAsDouble - detection.timestamp:F2}s, " +
                $"probe={probeDistance:F2}/{maximumExitLaneProbeDistance:F2} m";

            if (pairFrames >= requiredPairFrames)
            {
                FinishAlignment("central lane confidence confirmed; boundary alignment skipped");
                return;
            }
            if (probeDistance >= maximumExitLaneProbeDistance)
            {
                Fail($"Exit lane not visible after {probeDistance:F2} m at the planned heading; " +
                     $"node={CurrentNode}, target={activeRoute[targetRouteIndex]}, " +
                     $"yaw={laneFollower.transform.eulerAngles.y:F1}, desiredYaw={desiredExitYaw:F1}; " + statusDetail);
                return;
            }

            if (pairVisible)
                laneFollower.SetManualCommand(0f, 0f); // Hold position while confirming new camera frames.
            else if (headingReady)
                laneFollower.SetManualCommand(exitLaneProbeCommand, 0f);
            else
                laneFollower.SetManualCommand(GetExitSearchMoveCommand(), GetExitHeadingTurnCommand(signedHeadingError));
        }

        private float GetExitSearchMoveCommand()
        {
            // Once the forward budget is used, finish the heading search by rotating in place.
            return PlanarDistance(exitSearchStartPosition, laneFollower.transform.position) < maximumExitSearchAdvanceDistance
                ? exitSearchMoveCommand : 0f;
        }

        private float GetExitHeadingTurnCommand(float headingError)
        {
            if (Mathf.Abs(headingError) <= 8f)
                return 0f;
            float magnitude = Mathf.Clamp(
                Mathf.Abs(headingError) / 45f * activeSearchTurnCommand,
                Mathf.Min(0.10f, activeSearchTurnCommand), activeSearchTurnCommand);
            return Mathf.Sign(headingError) * magnitude;
        }

        private void UpdateVisualAlignment()
        {
            // Legacy state handler retained for compatibility. Current exit search bypasses VisualAlign.
            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            if (travelled > maximumAlignTravel)
            {
                Fail($"Virtual-line alignment failed within {maximumAlignTravel:F2} m");
                return;
            }

            bool usable = laneFollower.TryGetLaneDetection(out HsvLaneDetector.Detection detection);
            bool fresh = !double.IsNaN(detection.timestamp) && !double.IsInfinity(detection.timestamp) &&
                         detection.timestamp > lastAlignmentTimestamp;
            if (fresh)
            {
                lastAlignmentTimestamp = detection.timestamp;
                lastAlignmentImageTime = Time.time;
            }
            bool timedOut = Time.time - lastAlignmentImageTime > alignmentObservationTimeout;
            float exitHeadingError = Mathf.DeltaAngle(laneFollower.transform.eulerAngles.y, desiredExitYaw);
            bool exitHeadingReady = Mathf.Abs(exitHeadingError) <= alignmentExitHeadingTolerance;
            if (!usable || !detection.hasLineAngle || timedOut)
            {
                alignedFrames = 0;
                alignmentStreak.Reset();
                bool partial = usable && !timedOut && !detection.hasLineAngle &&
                               (detection.nearObserved || detection.farObserved);
                if (partial)
                {
                    pairLostFrames = 0;
                    alignmentLossStreak.Reset();
                    if (partialAlignmentStarted < 0f) partialAlignmentStarted = Time.time;
                    bool lateralAligned = exitHeadingReady && detection.nearObserved && travelled >= minimumAlignTravel &&
                        Mathf.Abs(detection.lateralError) <= boundaryAlignmentLateralTolerance;
                    alignedFrames = partialAlignmentStreak.Observe(detection.timestamp, lateralAligned);
                    statusDetail = $"Partial align {alignmentReference}: near={detection.nearObserved}, far={detection.farObserved}, " +
                        $"lateral={detection.lateralError:F2}/{boundaryAlignmentLateralTolerance:F2}, " +
                        $"stable={alignedFrames}/{requiredPartialAlignedFrames}, travel={travelled:F2}/{maximumPartialAlignTravel:F2}, " +
                        $"exitYawError={exitHeadingError:F1}/{alignmentExitHeadingTolerance:F1}, " +
                        $"elapsed={Time.time - partialAlignmentStarted:F2}/{maximumPartialAlignSeconds:F2}";
                    if (alignedFrames >= requiredPartialAlignedFrames)
                    {
                        FinishAlignment("partial lateral alignment and exit heading confirmed; angle unavailable");
                        return;
                    }
                    if (Time.time - partialAlignmentStarted >= maximumPartialAlignSeconds)
                    {
                        Fail($"Partial alignment lateral tolerance not reached; {statusDetail}");
                        return;
                    }
                    float partialTurn = lateralAligned ? 0f : BoundaryAlignmentControl.ExitHeadingTurn(
                        exitHeadingError, maximumPartialAlignTurn);
                    float partialMove = !lateralAligned && travelled < maximumPartialAlignTravel ? partialAlignMoveCommand : 0f;
                    laneFollower.SetManualCommand(partialMove, partialTurn, alignmentReference);
                    return;
                }
                partialAlignmentStreak.Reset();
                if (fresh) pairLostFrames = alignmentLossStreak.Observe(detection.timestamp, true);
                statusDetail = $"Alignment {alignmentReference}: near={detection.nearObserved}, far={detection.farObserved}, " +
                    $"angle={detection.hasLineAngle}, usable={usable}, timeout={timedOut}, " +
                    $"lostImages={pairLostFrames}/{pairLostFrameLimit}, partial={partial}, travel={travelled:F2}";
                if (pairLostFrames > pairLostFrameLimit || timedOut)
                {
                    Fail($"Alignment observation unavailable; {statusDetail}");
                }
                else
                {
                    laneFollower.SetManualCommand(0f, 0f, alignmentReference);
                }
                return;
            }

            pairLostFrames = 0;
            alignmentLossStreak.Reset();
            partialAlignmentStreak.Reset();
            partialAlignmentStarted = -1f;
            var command = BoundaryAlignmentControl.Calculate(detection.lineAngleRadians, detection.lateralError,
                visualHeadingGain, visualLateralGain, maximumVisualTurn,
                boundaryAlignMoveCommand);
            float correction = BoundaryAlignmentControl.ConstrainToExitHeading(command.turn,
                exitHeadingError, alignmentExitHeadingTolerance, maximumVisualTurn);
            float move = command.move;
            float angleDegrees = detection.lineAngleRadians * Mathf.Rad2Deg;

            bool aligned = exitHeadingReady && travelled >= minimumAlignTravel &&
                           Mathf.Abs(detection.lateralError) <= boundaryAlignmentLateralTolerance &&
                           Mathf.Abs(angleDegrees) <= boundaryAlignmentAngleTolerance;
            alignedFrames = alignmentStreak.Observe(detection.timestamp, aligned);
            statusDetail = $"Align {alignmentReference} to blue: lateral={detection.lateralError:F2}, theta={angleDegrees:F1} deg, " +
                $"exitYawError={exitHeadingError:F1}/{alignmentExitHeadingTolerance:F1}, " +
                $"move={(aligned ? 0f : move):F2}, turn={(aligned ? 0f : correction):F2}, visualTurn={command.turn:F2}, " +
                $"confidence={detection.confidence:F2}, stable={alignedFrames}/{requiredAlignedFrames}";

            if (alignedFrames >= requiredAlignedFrames)
            {
                FinishAlignment("alignment confirmed");
                return;
            }
            laneFollower.SetManualCommand(aligned ? 0f : move, aligned ? 0f : correction, alignmentReference);
        }

        private void FinishAlignment(string reason)
        {
            Debug.Log($"Navigation transition: {State} -> lane following ({reason}); node={CurrentNode}, " +
                      $"next={activeRoute[targetRouteIndex + 1]}, yaw={laneFollower.transform.eulerAngles.y:F1}, " +
                      $"desiredYaw={desiredExitYaw:F1}; {statusDetail}", this);
            targetRouteIndex++;
            normalLaneLostFrames = 0;
            markerFrames = 0;
            State = MissionState.FollowingLane;
            PlantNodeId target = activeRoute[targetRouteIndex];
            statusDetail = $"Following ID {(int)target} ({target}); {reason}";
            laneFollower.ResumeLaneFollowing();
        }

        private void BeginStraightThroughJunction(float pathDeflection)
        {
            targetRouteIndex++;
            markerFrames = 0;
            straightLossFrames = 0;
            straightReacquireFrames = 0;
            straightLaneGapObserved = false;
            lastStraightObservationTimestamp = -1d;
            motionStartPosition = laneFollower.transform.position;
            State = MissionState.StraightThroughJunction;
            PlantNodeId target = activeRoute[targetRouteIndex];
            statusDetail = $"Straight junction ({pathDeflection:F1} deg); driving to ID {(int)target} ({target})";
            laneFollower.SetManualCommand(straightJunctionCommand, 0f);
        }

        private void UpdateStraightThroughJunction()
        {
            if (useAbsoluteTurns)
            {
                if (TryEnterQrJunction()) return;
                laneFollower.SetManualCommand(straightJunctionCommand, 0f);
                if (PlanarDistance(motionStartPosition, laneFollower.transform.position) > maximumFallbackDistance)
                    Fail("Entry QR missing after straight junction");
                return;
            }
            if ((activeMission == ActiveMission.EquipmentAAndB || activeMission == ActiveMission.EquipmentA || activeMission == ActiveMission.EquipmentB) &&
                CurrentNode == PlantNodeId.UnderMid &&
                activeRoute[targetRouteIndex] == PlantNodeId.UnderRight)
            {
                UpdateRightBottomMarkerApproach();
                return;
            }

            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            if (travelled >= maximumStraightTravel)
            {
                Fail($"Straight junction lane was not reacquired within {maximumStraightTravel:F2} m");
                return;
            }

            PlantNodeId target = activeRoute[targetRouteIndex];
            bool targetVisible = markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                                 observation.nodeId == target &&
                                 observation.confidence >= minimumMarkerConfidence;
            if (targetVisible && observation.cameraRelativePosition.magnitude <= junctionActionDistance)
            {
                markerFrames++;
                statusDetail = $"Straight marker ID {(int)target} at {observation.cameraRelativePosition.magnitude:F2} m, " +
                               $"confirm={markerFrames}/{requiredMarkerFrames}";
                laneFollower.SetManualCommand(straightJunctionCommand * 0.5f, 0f);
                if (markerFrames >= requiredMarkerFrames)
                    ArriveAtTargetNode();
                return;
            }
            markerFrames = 0;

            bool pairVisible = laneFollower.TryGetBoundaryPair(
                minimumPairConfidence, out HsvLaneDetector.Detection detection);
            bool newObservation = detection.timestamp > lastStraightObservationTimestamp;
            if (newObservation)
            {
                lastStraightObservationTimestamp = detection.timestamp;
                if (!straightLaneGapObserved)
                {
                    straightLossFrames = pairVisible ? 0 : straightLossFrames + 1;
                    straightLaneGapObserved = straightLossFrames >= requiredStraightLossFrames;
                }
                else
                {
                    straightReacquireFrames = pairVisible ? straightReacquireFrames + 1 : 0;
                }
            }

            statusDetail =
                $"Straight {travelled:F2}/{maximumStraightTravel:F2} m, " +
                $"gap={(straightLaneGapObserved ? "YES" : $"{straightLossFrames}/{requiredStraightLossFrames}")}, " +
                $"pair={(pairVisible ? "YES" : "NO")}, reacquire={straightReacquireFrames}/{requiredStraightReacquireFrames}";

            if (straightLaneGapObserved && travelled >= minimumStraightTravel &&
                straightReacquireFrames >= requiredStraightReacquireFrames)
            {
                State = MissionState.FollowingLane;
                normalLaneLostFrames = 0;
                statusDetail = $"Straight junction cleared; following ID {(int)target} ({target})";
                laneFollower.ResumeLaneFollowing();
                return;
            }

            laneFollower.SetManualCommand(straightJunctionCommand, 0f);
        }

        private void UpdateRightBottomMarkerApproach()
        {
            if (!TryGetDemoTurnCentre(PlantNodeId.UnderMid, PlantNodeId.UnderRight,
                    out Vector3 turnCentre, out Vector3 incomingDirection))
            {
                Fail("Right-bottom turn centre is missing");
                return;
            }

            Collider footprint = laneFollower.GetComponent<Collider>();
            Vector3 robotCentre = footprint is BoxCollider box
                ? laneFollower.transform.TransformPoint(box.center)
                : laneFollower.transform.position;
            Vector3 toCentre = turnCentre - robotCentre;
            toCentre.y = 0f;
            float travelled = PlanarDistance(motionStartPosition, robotCentre);
            if (travelled >= rightBottomMaximumApproachDistance)
            {
                Fail($"Right-bottom turn centre was not reached within {rightBottomMaximumApproachDistance:F1} m");
                return;
            }

            float remainingAlong = Vector3.Dot(toCentre, incomingDirection);
            float lateralDistance = (toCentre - incomingDirection * remainingAlong).magnitude;
            if (toCentre.magnitude <= junctionActionDistance ||
                (remainingAlong <= 0f && lateralDistance <= junctionActionDistance))
            {
                ArriveAtTargetNode();
                return;
            }

            float headingError = Vector3.SignedAngle(laneFollower.transform.forward, toCentre, Vector3.up);
            float turn = Mathf.Clamp(headingError / 70f * activeSearchTurnCommand,
                -activeSearchTurnCommand, activeSearchTurnCommand);
            float move = Mathf.Abs(headingError) > 40f
                ? 0f
                : straightJunctionCommand * Mathf.Lerp(1f, 0.45f, Mathf.Abs(headingError) / 40f);
            statusDetail = $"Guided to right-bottom turn centre: {toCentre.magnitude:F2} m, " +
                           $"heading {headingError:F0} deg, travelled {travelled:F1} m";
            laneFollower.SetManualCommand(move, turn);
        }

        private void EnterStraightMarkerFallback()
        {
            Debug.LogWarning($"Navigation transition: lane following -> marker fallback; node={CurrentNode}, " +
                             $"target={activeRoute[targetRouteIndex]}, yaw={laneFollower.transform.eulerAngles.y:F1}; {statusDetail}", this);
            markerFrames = 0;
            normalLaneLostFrames = 0;
            motionStartPosition = laneFollower.transform.position;
            fallbackStartTime = Time.time;
            State = MissionState.StraightToNextMarker;
            PlantNodeId target = activeRoute[targetRouteIndex];
            statusDetail = $"No lane; driving straight to marker ID {(int)target}";
            laneFollower.SetManualCommand(fallbackStraightCommand, 0f);
        }

        private void UpdateStraightToNextMarker()
        {
            if (useAbsoluteTurns && TryEnterQrJunction())
                return;
            if (!useAbsoluteTurns && TryArriveAtTargetAfterAvoidance())
                return;
            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            float elapsed = Time.time - fallbackStartTime;
            if (travelled >= maximumFallbackDistance || elapsed >= maximumFallbackSeconds)
            {
                Fail($"Marker fallback limit reached: {travelled:F1} m, {elapsed:F1} s");
                return;
            }

            if (useIndoorSensorSimulation)
            {
                if (!routeGraph.TryGetEntry(CurrentNode, activeRoute[targetRouteIndex], out NavigationMarker entryZone))
                { Fail("SIM NFC entry zone missing"); return; }
                // This state advances straight; coordinate guidance belongs to centre approach.
                laneFollower.SetManualCommand(fallbackStraightCommand, 0f);
                statusDetail = $"SIM NFC: straight to entry {entryZone.NodeId}; " +
                    $"travel={travelled:F2}/{maximumFallbackDistance:F2} m, elapsed={elapsed:F1}/{maximumFallbackSeconds:F1}s";
                return;
            }
            PlantNodeId target = activeRoute[targetRouteIndex];
            bool visible = markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                           observation.nodeId == target &&
                           observation.confidence >= minimumMarkerConfidence;
            if (!visible)
            {
                markerFrames = 0;
                statusDetail = $"Straight to marker ID {(int)target}: {travelled:F1}/{maximumFallbackDistance:F1} m";
                laneFollower.SetManualCommand(fallbackStraightCommand, 0f);
                return;
            }

            float distance = observation.cameraRelativePosition.magnitude;
            statusDetail = $"Fallback marker ID {(int)target} visible at {distance:F2} m";
            if (distance > junctionActionDistance || (useAbsoluteTurns && targetRouteIndex < activeRoute.Count - 1))
            {
                markerFrames = 0;
                laneFollower.SetManualCommand(fallbackStraightCommand, 0f);
                return;
            }

            markerFrames++;
            laneFollower.SetManualCommand(fallbackStraightCommand * 0.5f, 0f);
            if (markerFrames >= requiredMarkerFrames)
                ArriveAtTargetNode();
        }

        private bool TryArriveAtTargetAfterAvoidance()
        {
            if (!avoidanceInterruptedCurrentLeg || targetRouteIndex >= activeRoute.Count ||
                !routeGraph.TryGetEntry(CurrentNode, activeRoute[targetRouteIndex], out NavigationMarker marker))
                return false;

            Collider footprint = laneFollower.GetComponent<Collider>();
            Vector3 robotCentre = footprint is BoxCollider box
                ? laneFollower.transform.TransformPoint(box.center)
                : laneFollower.transform.position;
            if (PlanarDistance(robotCentre, marker.transform.position) > junctionActionDistance)
                return false;

            markerFrames++;
            statusDetail = $"Reacquired route at ID {(int)activeRoute[targetRouteIndex]} after avoidance, " +
                           $"confirm={markerFrames}/{requiredMarkerFrames}";
            if (markerFrames >= requiredMarkerFrames)
            {
                ArriveAtTargetNode();
            }
            return true;
        }

        private void ResolveManeuver(PlantNodeId entry, PlantNodeId junction, PlantNodeId exit)
        {
            activeApproachDistance = maximumApproachDistance;
            activeApproachCommand = approachCommand;
            activeSearchTurnCommand = searchTurnCommand;
            if (maneuverOverrides == null) return;
            foreach (ManeuverOverride item in maneuverOverrides)
            {
                if (item.entryNode != entry || item.junctionNode != junction || item.exitNode != exit) continue;
                activeApproachDistance = item.approachDistance;
                activeApproachCommand = item.approachCommand;
                activeSearchTurnCommand = item.searchTurnCommand;
                return;
            }
        }

        private bool TryCalculateEdgeYaw(PlantNodeId from, PlantNodeId to, out float yaw)
        {
            yaw = 0f;
            if (routeGraph == null || !routeGraph.TryGetMarker(from, out NavigationMarker fromMarker) ||
                !routeGraph.TryGetMarker(to, out NavigationMarker toMarker)) return false;
            Vector3 direction = toMarker.transform.position - fromMarker.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return false;
            yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            return true;
        }

        private bool TryCalculatePathDeflection(
            PlantNodeId entry, PlantNodeId junction, PlantNodeId exit, out float angle)
        {
            angle = 0f;
            if (routeGraph == null ||
                !routeGraph.TryGetMarker(entry, out NavigationMarker entryMarker) ||
                !routeGraph.TryGetMarker(junction, out NavigationMarker junctionMarker) ||
                !routeGraph.TryGetMarker(exit, out NavigationMarker exitMarker))
                return false;

            Vector3 incoming = junctionMarker.transform.position - entryMarker.transform.position;
            Vector3 outgoing = exitMarker.transform.position - junctionMarker.transform.position;
            incoming.y = 0f;
            outgoing.y = 0f;
            if (incoming.sqrMagnitude < 0.001f || outgoing.sqrMagnitude < 0.001f)
                return false;

            angle = Vector3.Angle(incoming, outgoing);
            return true;
        }

        [ContextMenu("Reset Mission")]
        public void ResetMission()
        {
            nfcDecision = "not_checked";
            entryTransitionAt = centreTransitionAt = -1f;
            activeRoute.Clear();
            activeMission = ActiveMission.None;
            State = MissionState.Idle;
            avoidanceInterruptedCurrentLeg = false;
            normalLaneLostFrames = 0;
            activeInspectionIndex = 0;
            activeInspectionPoint = null;
            statusDetail = $"Ready at {CurrentNode}";
            laneFollower?.SetDriveEnabled(false);
        }

        private void CompleteMission()
        {
            int requiredInspections = activeMission == ActiveMission.EquipmentAAndB ? 4 :
                (activeMission == ActiveMission.EquipmentA || activeMission == ActiveMission.EquipmentB) ? 2 : 0;
            if (activeInspectionIndex < requiredInspections)
            {
                Fail($"Equipment mission reached the route end with only {activeInspectionIndex}/{requiredInspections} inspections completed");
                return;
            }

            State = MissionState.Completed;
            statusDetail = $"Completed at ID {(int)CurrentNode} ({CurrentNode})";
            laneFollower.SetDriveEnabled(false);
        }

        private bool ConnectionsReady() =>
            routeGraph != null && missionPlanner != null && (useIndoorSensorSimulation ? indoorSensors != null : markerSource != null) && laneFollower != null;

        private void Fail(string reason)
        {
            State = MissionState.Fault;
            statusDetail = reason;
            laneFollower?.SetDriveEnabled(false);
            Debug.LogError($"Navigation mission fault: {reason}", this);
            RecordDrivingLog("fault");
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private string RouteText()
        {
            if (activeRoute.Count == 0) return "No active route";
            var text = new StringBuilder();
            for (int i = 0; i < activeRoute.Count; i++)
            {
                if (i > 0) text.Append(" > ");
                text.Append((int)activeRoute[i]);
            }
            return text.ToString();
        }

        private void OnGUI()
        {
            if (!showMissionPanel) return;
            EnsureStyles();
            Rect panel = new Rect(10f, Screen.height - 90f, 500f, 80f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 7f, panel.width - 20f, 22f),
                $"MISSION: {activeMission}   State: {State}" +
                (avoidancePaused ? " [PPO / ADAS]" : ""), titleStyle);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 31f, panel.width - 20f, 44f),
                $"{statusDetail}\nRoute: {RouteText()}", statusStyle);
        }

        private void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.cyan }
            };
            statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, normal = { textColor = Color.white }, wordWrap = true
            };
        }
    }
}
