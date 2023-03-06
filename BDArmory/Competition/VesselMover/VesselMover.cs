using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KSP.UI.Screens;

using BDArmory.Competition.VesselSpawning;
using BDArmory.Extensions;
using BDArmory.Settings;
using BDArmory.UI;
using BDArmory.Utils;

using static BDArmory.Competition.VesselSpawning.CustomTemplateSpawning;

namespace BDArmory.Competition.VesselMover
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class VesselMover : VesselSpawnerBase
    {
        public static VesselMover Instance;

        enum State { None, Moving, Lowering, Spawning };
        State state
        {
            get { return _state; }
            set { _state = value; ResetWindowHeight(); }
        }
        State _state;
        internal WaitForFixedUpdate wait = new WaitForFixedUpdate();
        HashSet<Vessel> movingVessels = new HashSet<Vessel>();
        HashSet<Vessel> loweringVessels = new HashSet<Vessel>();
        List<float> jumpToAltitudes = new List<float> { 10, 100, 1000, 10000, 50000 };
        RaycastHit[] hits = new RaycastHit[10];

        #region Monobehaviour routines
        protected override void Awake()
        {
            base.Awake();
            if (Instance != null) Destroy(Instance);
            Instance = this;
        }

        void Start()
        {
            ready = false;
            StartCoroutine(WaitForBdaSettings());
            ConfigureStyles();
            moveIndicator = new GameObject().AddComponent<LineRenderer>();
            moveIndicator.material = new Material(Shader.Find("KSP/Emissive/Diffuse"));
            moveIndicator.material.SetColor("_EmissiveColor", Color.green);
            moveIndicator.startWidth = 0.15f;
            moveIndicator.endWidth = 0.15f;
            moveIndicator.enabled = false;
            moveIndicator.positionCount = circleRes + 3;
            GameEvents.onVesselChange.Add(OnVesselChanged);
        }

        private IEnumerator WaitForBdaSettings()
        {
            yield return new WaitUntil(() => BDArmorySettings.ready);

            BDArmorySetup.WindowRectVesselMover.height = 0;
            if (guiCheckIndex < 0) guiCheckIndex = GUIUtils.RegisterGUIRect(new Rect());
            if (_vesselGUICheckIndex < 0) _vesselGUICheckIndex = GUIUtils.RegisterGUIRect(new Rect());
            if (_crewGUICheckIndex < 0) _crewGUICheckIndex = GUIUtils.RegisterGUIRect(new Rect());
            ready = true;
            BDArmorySetup.Instance.hasVesselMover = true;
        }

        void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(OnVesselChanged);
        }

        void Update()
        {
            if (state != State.None && FlightGlobals.ActiveVessel == null) state = State.None;
            if (state == State.Moving && IsMoving(FlightGlobals.ActiveVessel))
                HandleInput(); // Input needs to be handled in Update.
        }

        void LateUpdate()
        {
            if (state == State.Moving && !MapView.MapIsEnabled && BDArmorySetup.GAME_UI_ENABLED)
            {
                moveIndicator.enabled = true;
                DrawMovingIndicator();
            }
            else moveIndicator.enabled = false;
        }
        #endregion

        #region Input
        Vector3 positionAdjustment = Vector3.zero; // X, Y, Z
        Vector3 rotationAdjustment = Vector3.zero; // Roll, Yaw, Pitch
        bool translating = false;
        bool rotating = false;
        bool jump = false;
        bool jumpDirection = false; // false = down, true = up
        bool reset = false;
        bool autoLevelPlane = false;
        bool autoLevelRocket = false;
        void HandleInput()
        {
            positionAdjustment = Vector3.zero;
            rotationAdjustment = Vector3.zero;
            translating = false;
            rotating = false;
            autoLevelPlane = false;
            autoLevelRocket = false;

            if (GameSettings.THROTTLE_CUTOFF.GetKeyDown()) // Reset altitude to base.
            { reset = true; }
            else if (Input.GetKeyDown(KeyCode.Tab)) // Jump to next reference altitude.
            {
                jump = true;
                jumpDirection = GameSettings.THROTTLE_UP.GetKey();
            }
            else if (GameSettings.THROTTLE_UP.GetKey()) // Increase altitude.
            {
                positionAdjustment.z = 1f;
                translating = true;
            }
            else if (GameSettings.THROTTLE_DOWN.GetKey()) // Decrease altitude.
            {
                positionAdjustment.z = -1f;
                translating = true;
            }

            if (GameSettings.PITCH_DOWN.GetKey()) // Translate forward.
            {
                positionAdjustment.y = 1f;
                translating = true;
            }
            else if (GameSettings.PITCH_UP.GetKey()) // Translate backward.
            {
                positionAdjustment.y = -1f;
                translating = true;
            }

            if (GameSettings.YAW_RIGHT.GetKey()) // Translate right.
            {
                positionAdjustment.x = 1f;
                translating = true;
            }
            else if (GameSettings.YAW_LEFT.GetKey()) // Translate left.
            {
                positionAdjustment.x = -1f;
                translating = true;
            }

            if (GameSettings.TRANSLATE_FWD.GetKey()) // Auto-level plane
            { autoLevelPlane = true; rotating = true; }
            else if (GameSettings.TRANSLATE_BACK.GetKey()) // Auto-level rocket
            { autoLevelRocket = true; rotating = true; }
            else
            {
                if (GameSettings.ROLL_LEFT.GetKey()) // Roll left.
                {
                    rotationAdjustment.x = -1f;
                    rotating = true;
                }
                else if (GameSettings.ROLL_RIGHT.GetKey()) // Roll right.
                {
                    rotationAdjustment.x = 1f;
                    rotating = true;
                }

                if (GameSettings.TRANSLATE_DOWN.GetKey()) // Pitch down.
                {
                    rotationAdjustment.z = 1f;
                    rotating = true;
                }
                else if (GameSettings.TRANSLATE_UP.GetKey()) // Pitch up.
                {
                    rotationAdjustment.z = -1f;
                    rotating = true;
                }

                if (GameSettings.TRANSLATE_RIGHT.GetKey()) // Yaw right.
                {
                    rotationAdjustment.y = 1f;
                    rotating = true;
                }
                else if (GameSettings.TRANSLATE_LEFT.GetKey()) // Yaw left.
                {
                    rotationAdjustment.y = -1f;
                    rotating = true;
                }
            }
        }
        #endregion

        #region Moving
        Vector3d geoCoords;
        bool IsValid(Vessel vessel) => vessel != null && vessel.loaded && !vessel.packed;
        bool IsMoving(Vessel vessel) => IsValid(vessel) && movingVessels.Contains(vessel);
        bool IsLowering(Vessel vessel) => IsValid(vessel) && loweringVessels.Contains(vessel);
        IEnumerator MoveVessel(Vessel vessel)
        {
            if (!IsValid(vessel)) { state = State.None; yield break; }
            if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Moving {vessel.vesselName}");
            movingVessels.Add(vessel);
            state = State.Moving;

            var hadPatchedConicsSolver = vessel.PatchedConicsAttached;
            if (hadPatchedConicsSolver)
            {
                if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Detaching patched conic solver");
                try
                {
                    vessel.DetachPatchedConicsSolver();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BDArmory.VesselMover]: Failed to remove the Patched Conic Solver: {e.Message}");
                }
            }

            // Disable various action groups. We'll enable some of them again later if specified.
            vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, false); // Disable RCS
            if (BDArmorySettings.VESSEL_MOVER_ENABLE_SAS) vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false); // Disable SAS
            if (BDArmorySettings.VESSEL_MOVER_ENABLE_BRAKES) vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, false); // Disable Brakes
            foreach (LaunchClamp clamp in vessel.FindPartModulesImplementing<LaunchClamp>()) clamp.Release(); // Release clamps
            KillRotation(vessel);
            if (BDArmorySettings.VESSEL_MOVER_ENABLE_BRAKES) vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);

            var up = (vessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
            Vector3 forward = default, right = default;
            float startingAltitude = 2f * vessel.GetRadius();
            var lowerBound = GetLowerBound(vessel);
            var safeAlt = SafeAltitude(vessel, lowerBound);
            var position = vessel.transform.position;
            var rotation = vessel.transform.rotation;
            var referenceTransform = vessel.ReferenceTransform;
            if (LandedOrSplashed(vessel) || startingAltitude > safeAlt) // Careful with initial separation from ground.
            {
                var count = 0;
                while (IsMoving(vessel) && ++count <= 10)
                {
                    vessel.Landed = false;
                    vessel.Splashed = false;
                    vessel.IgnoreGForces(240);
                    position += count * (startingAltitude - safeAlt) / 55f * up;
                    vessel.SetPosition(position);
                    vessel.SetWorldVelocity(Vector3d.zero);
                    vessel.SetRotation(rotation);
                    yield return wait;
                    KrakensbaneCorrection(ref position);
                }
            }

            KillRotation(vessel);
            float moveSpeed = 0;
            float rotateSpeed = 0;
            while (IsMoving(vessel))
            {
                if (vessel.isActiveVessel)
                {
                    if (translating || autoLevelPlane || autoLevelRocket)
                    {
                        up = (vessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
                        if (MapView.MapIsEnabled)
                        {
                            forward = Vector3.ProjectOnPlane(-Math.Sign(vessel.latitude) * (vessel.mainBody.GetWorldSurfacePosition(vessel.latitude - Math.Sign(vessel.latitude), vessel.longitude, vessel.altitude) - vessel.GetWorldPos3D()), up).normalized;
                        }
                        else
                        {
                            if (Vector3.Dot(-up, FlightCamera.fetch.mainCamera.transform.up) > 0)
                                forward = Vector3.ProjectOnPlane(FlightCamera.fetch.mainCamera.transform.up, up).normalized;
                            else
                                forward = Vector3.ProjectOnPlane(vessel.transform.position - FlightCamera.fetch.mainCamera.transform.position, up).normalized;
                        }
                        right = Vector3.Cross(up, forward);
                    }

                    // Perform rotations first to update lower bound.
                    if (rotating)
                    {
                        var radarAltitude = RadarAltitude(vessel) - lowerBound;
                        rotateSpeed = Mathf.Clamp(Mathf.MoveTowards(rotateSpeed, 180f, Mathf.Min(10f + 10f * rotateSpeed, 180f) * Time.fixedDeltaTime), 0f, 180f);
                    }
                    else { rotateSpeed = 0f; }
                    if (autoLevelPlane)
                    {
                        Quaternion targetRot = Quaternion.LookRotation(-up, forward);
                        rotation = Quaternion.RotateTowards(rotation, targetRot, rotateSpeed * 2f * Time.fixedDeltaTime);
                    }
                    else if (autoLevelRocket)
                    {
                        Quaternion targetRot = Quaternion.LookRotation(forward, up);
                        rotation = Quaternion.RotateTowards(rotation, targetRot, rotateSpeed * 2f * Time.fixedDeltaTime);
                    }
                    else if (rotating)
                    {
                        if (rotationAdjustment.x != 0) rotation = Quaternion.AngleAxis(-rotateSpeed * Time.fixedDeltaTime * rotationAdjustment.x, referenceTransform.up) * rotation; // Roll
                        if (rotationAdjustment.z != 0) rotation = Quaternion.AngleAxis(rotateSpeed * Time.fixedDeltaTime * rotationAdjustment.z, referenceTransform.right) * rotation; // Pitch
                        if (rotationAdjustment.y != 0) rotation = Quaternion.AngleAxis(-rotateSpeed * Time.fixedDeltaTime * rotationAdjustment.y, referenceTransform.forward) * rotation; // Yaw
                    }
                    if (rotating)
                    {
                        vessel.IgnoreGForces(240);
                        var previousLowerBound = lowerBound;
                        vessel.SetRotation(rotation);
                        lowerBound = GetLowerBound(vessel);
                        vessel.SetPosition(position);
                        vessel.SetWorldVelocity(Vector3d.zero);
                        position += (lowerBound - previousLowerBound) * up;
                    }

                    // Translations/Altitude changes
                    if (reset)
                    {
                        var baseAltitude = 2f * vessel.GetRadius();
                        var safeAltitude = SafeAltitude(vessel, lowerBound);
                        if (!BDArmorySettings.VESSEL_MOVER_BELOW_WATER) safeAltitude = Mathf.Min((float)vessel.altitude, safeAltitude);
                        if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Resetting to base altitude {baseAltitude + lowerBound}m (safeAlt: {safeAltitude}, lower bound: {lowerBound}m)");
                        position += (baseAltitude - safeAltitude) * up;
                        reset = false;
                    }
                    else if (jump)
                    {
                        var baseAltitude = 2f * vessel.GetRadius();
                        var safeAltitude = SafeAltitude(vessel, lowerBound);
                        var jumpToAltitude = baseAltitude;
                        if (jumpDirection) // Jump up
                        {
                            jumpToAltitude = jumpToAltitudes.Where(a => a > 1.05f * safeAltitude && a > 2f * baseAltitude).Select(a => Mathf.Max(a, baseAltitude)).FirstOrDefault();
                            if (jumpToAltitude < baseAltitude) jumpToAltitude = baseAltitude;
                        }
                        else // Jump down
                        {
                            jumpToAltitude = safeAltitude < 1.1f * baseAltitude ? jumpToAltitudes.Last() : jumpToAltitudes.Where(a => a < 0.95f * safeAltitude).LastOrDefault();
                            if (jumpToAltitude < 2f * baseAltitude) jumpToAltitude = baseAltitude;
                        }
                        if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Jumping to altitude {jumpToAltitude}m (safeAlt: {safeAltitude}, baseAlt: {baseAltitude})");
                        up = (vessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
                        position += (jumpToAltitude - safeAltitude) * up;
                        jump = false;
                    }
                    else if (translating)
                    {
                        var radarAltitude = RadarAltitude(vessel);
                        var distance = Mathf.Abs(radarAltitude - lowerBound);
                        float maxMoveSpeed = distance > 25f ? 20f * BDAMath.Sqrt(distance) : distance > 1f ? 4f * distance : 1f;
                        moveSpeed = Mathf.Clamp(Mathf.MoveTowards(moveSpeed, maxMoveSpeed, maxMoveSpeed * Time.fixedDeltaTime), 0, maxMoveSpeed);

                        var moveDistance = moveSpeed * Time.fixedDeltaTime;
                        var offset = 3f * positionAdjustment.x * moveDistance * right + 3f * positionAdjustment.y * moveDistance * forward;
                        offset += (radarAltitude - RadarAltitude(position + offset)) * up;
                        var safeAltitude = radarAltitude < 1000 ? SafeAltitude(vessel, lowerBound, offset) : radarAltitude; // Don't bother when over 1000m.
                        offset += Mathf.Max(positionAdjustment.z * moveSpeed * Time.fixedDeltaTime, -safeAltitude) * up;
                        position += offset;
                        // Debug.Log($"DEBUG position: {position:G6}, altitude: {radarAltitude}, safeAltCorrection: {safeAltitude}, distance: {distance}, moveSpeed: {moveSpeed}, maxSpeed: {maxMoveSpeed}");
                    }
                    else { moveSpeed = 0; }

                    if (translating || rotating || autoLevelPlane || autoLevelRocket)  // Correct for terrain/object collisions.
                    {
                        var radarAltitude = RadarAltitude(vessel);
                    }
                }

                vessel.IgnoreGForces(240);
                vessel.SetPosition(position);
                vessel.SetWorldVelocity(Vector3d.zero);
                vessel.SetRotation(rotation); // Reset the rotation to prevent any angular momentum from messing with the orientation.
                yield return wait;
                KrakensbaneCorrection(ref position);
            }

            if (hadPatchedConicsSolver && !vessel.PatchedConicsAttached)
            {
                if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Re-attaching patched conic solver");
                try
                {
                    vessel.AttachPatchedConicsSolver();
                    if (vessel.altitude > 1e5) vessel.SetWorldVelocity(UnityEngine.Random.rotation * Vector3.one * 0.01f); // Add noise to the velocity if above 100km to avoid NaNs in the Patched Cubic Solver due to a degenerate orbit.
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BDArmory.VesselMover]: Failed to re-attach the Patched Conic Solver: {e.Message}");
                }
            }
        }

        void KillRotation(Vessel vessel)
        {
            if (vessel.angularVelocity == default) return;
            foreach (var part in vessel.Parts)
            {
                var rb = part.Rigidbody;
                if (rb == null) continue;
                rb.angularVelocity = default;
            }
        }

        void KrakensbaneCorrection(ref Vector3 position)
        {
            if (!BDKrakensbane.IsActive) return;
            position -= BDKrakensbane.FloatingOriginOffsetNonKrakensbane;
        }

        IEnumerator PlaceVessel(Vessel vessel)
        {
            if (IsLowering(vessel)) yield break; // We're already doing this.
            if (!IsMoving(vessel)) { state = State.None; yield break; } // The vessel isn't moving, abort.
            if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Placing {vessel.vesselName}");
            movingVessels.Remove(vessel);
            loweringVessels.Add(vessel);

            KillRotation(vessel);
            if (BDArmorySettings.VESSEL_MOVER_ENABLE_BRAKES) vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
            if (!FlightGlobals.currentMainBody.hasSolidSurface) { DropVessel(vessel); yield break; } // No surface to lower to!
            if (BDArmorySettings.VESSEL_MOVER_LOWER_FAST)
            {
                var up = (vessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
                var baseAltitude = 2f * vessel.GetRadius();
                var safeAltitude = SafeAltitude(vessel) * 0.99f; // Only go 99% of the way in a single jump in case terrain is still loading in.
                if (baseAltitude < safeAltitude)
                    vessel.Translate((baseAltitude - safeAltitude) * up);
                vessel.SetWorldVelocity(Vector3d.zero);
            }
            yield return LowerVessel(vessel);
        }

        IEnumerator LowerVessel(Vessel vessel)
        {
            if (!FlightGlobals.currentMainBody.hasSolidSurface) { DropVessel(vessel); yield break; } // No surface to lower to!
            if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Lowering {vessel.vesselName}");
            state = State.Lowering;
            var lowerBound = GetLowerBound(vessel);
            var up = (vessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
            bool finalLowering = false;
            var previousAltitude = vessel.altitude;
            while (IsLowering(vessel) && !LandedOrSplashed(vessel) && (!finalLowering || vessel.altitude - previousAltitude < -0.1 * BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED * Time.fixedDeltaTime))
            {
                var distance = SafeAltitude(vessel, lowerBound);
                var speed = distance > 25f ? 20f * BDAMath.Sqrt(distance) : distance > BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED ? 4f * distance : BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED;
                if (speed > BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED)
                {
                    vessel.SetWorldVelocity(Vector3d.zero);
                    vessel.Translate(-speed * up * Time.fixedDeltaTime);
                }
                else
                {
                    if (!finalLowering && vessel.verticalSpeed < -1e-2) finalLowering = true;
                    vessel.SetWorldVelocity(-speed * up);
                }
                // Debug.Log($"DEBUG landed/splashed: {LandedOrSplashed(vessel)}, altitude: {vessel.altitude}m ({distance}m), v-speed: {vessel.verticalSpeed}m/s, speed: {speed}");
                previousAltitude = vessel.altitude;
                yield return wait;
            }
            if (!IsLowering(vessel)) yield break; // Vessel destroyed or state switched, e.g., moving again.

            // Turn on brakes and SAS (apparently helps to avoid the turning bug).
            if (BDArmorySettings.VESSEL_MOVER_ENABLE_SAS)
            { vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true); }
            if (BDArmorySettings.VESSEL_MOVER_ENABLE_BRAKES)
            {
                vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                // Ease the craft to a resting position.
                messageState = Messages.EasingCraft;
                var startTime = Time.time;
                var stationaryStartTime = startTime;
                vessel.IgnoreGForces(0);
                while (IsLowering(vessel) && Time.time - startTime < 10f && Time.time - stationaryStartTime <= 0.1f) // Damp movement for up to 10s.
                {
                    // if ((float)vessel.verticalSpeed < -0.1f * BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED)
                    if ((vessel.altitude >= 0 && Math.Abs(vessel.altitude - previousAltitude) > 0.1 * BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED * Time.fixedDeltaTime)
                        || (vessel.altitude < 0 && vessel.altitude - previousAltitude < -0.1 * BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED * Time.fixedDeltaTime))
                    {
                        vessel.SetWorldVelocity(vessel.GetSrfVelocity() * (0.45f + 0.5f * BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED));
                        stationaryStartTime = Time.time;
                        yield return wait; // Setting the velocity prevents a proper velocity calculation on the next frame, so wait an extra frame for it to take effect.
                    }
                    // Debug.Log($"DEBUG landed/splashed: {LandedOrSplashed(vessel)}, altitude: {vessel.altitude}m, v-speed: {vessel.verticalSpeed}m/s");
                    previousAltitude = vessel.altitude;
                    yield return wait;
                }
            }
            if (IsLowering(vessel))
            {
                loweringVessels.Remove(vessel);
                state = State.None;
                messageState = Messages.None;
            }
        }

        void DropVessel(Vessel vessel)
        {
            if (!IsMoving(vessel) && !IsLowering(vessel)) return; // Not in a valid state for dropping.
            if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Dropping {vessel.vesselName}");
            movingVessels.Remove(vessel);
            loweringVessels.Remove(vessel);
            state = State.None;
            messageState = Messages.None;
        }

        /// <summary>
        /// Get the vertical distance (non-negative) from the vessel transform position to the lowest point.
        /// </summary>
        /// <param name="vessel"></param>
        /// <returns></returns>
        float GetLowerBound(Vessel vessel)
        {
            var up = (vessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
            var maxDim = 2f * vessel.GetRadius();
            var radius = vessel.GetRadius(up, vessel.GetBounds());
            var hitCount = Physics.BoxCastNonAlloc(vessel.transform.position - (maxDim + 0.1f) * up, new Vector3(radius, 0.1f, radius), up, hits, Quaternion.FromToRotation(Vector3.up, up), maxDim, (int)(LayerMasks.Parts | LayerMasks.EVA | LayerMasks.Wheels));
            if (hitCount == hits.Length)
            {
                hits = Physics.BoxCastAll(vessel.transform.position - (maxDim + 0.1f) * up, new Vector3(radius, 0.1f, radius), up, Quaternion.FromToRotation(Vector3.up, up), maxDim, (int)(LayerMasks.Parts | LayerMasks.EVA | LayerMasks.Wheels));
                hitCount = hits.Length;
            }
            var distances = hits.Take(hitCount).Where(hit => hit.collider != null && hit.collider.gameObject != null).Where(hit => { var part = hit.collider.gameObject.GetComponentInParent<Part>(); return part != null && part.vessel == vessel; }).Select(hit => hit.distance).ToArray();
            if (distances.Length == 0)
            {
                Debug.LogWarning($"[BDArmory.VesselMover]: Failed to detect craft for lower bound!");
                return 0;
            }
            return maxDim - distances.Min();
        }

        bool LandedOrSplashed(Vessel vessel) => BDArmorySettings.VESSEL_MOVER_BELOW_WATER ? vessel.Landed : vessel.LandedOrSplashed;
        float RadarAltitude(Vessel vessel) => (float)(vessel.altitude - vessel.mainBody.TerrainAltitude(vessel.latitude, vessel.longitude, BDArmorySettings.VESSEL_MOVER_BELOW_WATER));
        float RadarAltitude(Vector3 position) => (float)(FlightGlobals.currentMainBody.GetAltitude(position) - BodyUtils.GetTerrainAltitudeAtPos(position, BDArmorySettings.VESSEL_MOVER_BELOW_WATER));
        float SafeAltitude(Vessel vessel, float lowerBound = -1f, Vector3 offset = default)
        {
            var altitude = RadarAltitude(vessel);
            if (BDArmorySettings.VESSEL_MOVER_DONT_WORRY_ABOUT_COLLISIONS) return altitude;
            var position = vessel.transform.position + offset;
            var up = (position - FlightGlobals.currentMainBody.transform.position).normalized;
            var radius = vessel.GetRadius(up, vessel.GetBounds());
            if (lowerBound < 0) lowerBound = GetLowerBound(vessel);

            // Detect collisions from moving in the direction of the offset. 100m is generally sufficient.
            var hitCount = Physics.BoxCastNonAlloc(position + 100.1f * up, new Vector3(radius, 0.1f, radius), -up, hits, Quaternion.FromToRotation(Vector3.up, up), altitude + 100f, (int)(LayerMasks.Scenery | LayerMasks.Parts | LayerMasks.EVA | LayerMasks.Wheels));
            if (hitCount == hits.Length)
            {
                hits = Physics.BoxCastAll(position + 100.1f * up, new Vector3(radius, 0.1f, radius), -up, Quaternion.FromToRotation(Vector3.up, up), altitude + 100f, (int)(LayerMasks.Scenery | LayerMasks.Parts | LayerMasks.EVA | LayerMasks.Wheels));
                hitCount = hits.Length;
            }
            if (hitCount > 0)
            {
                var distances = hits.Take(hitCount).Where(hit => hit.collider != null && hit.collider.gameObject != null).Where(hit => { var part = hit.collider.gameObject.GetComponentInParent<Part>(); return part == null || part.vessel != vessel; }).Select(hit => hit.distance).ToArray();
                if (distances.Length > 0) altitude = Mathf.Min(altitude, distances.Min() - 100f);
            }
            return altitude - lowerBound - 0.1f;
        }
        #endregion

        #region Spawning
        Vessel spawnedVessel;
        HashSet<string> KerbalNames = new HashSet<string>();
        int crewCapacity = -1;
        string vesselNameToSpawn = "";
        CustomCraftBrowserDialog craftBrowser;
        IEnumerator SpawnVessel()
        {
            state = State.Spawning;

            // Open craft selection
            string craftFile = "";
            bool abort = false;
            messageState = Messages.OpeningCraftBrowser;
            if (BDArmorySettings.VESSEL_MOVER_CLASSIC_CRAFT_CHOOSER)
            {
                var craftBrowser = CraftBrowserDialog.Spawn(EditorFacility.SPH, HighLogic.SaveFolder, (path, loadType) => { craftFile = path; }, () => { abort = true; }, false);
                while (!abort && string.IsNullOrEmpty(craftFile)) yield return wait;
                craftBrowser = null;
            }
            else
            {
                ShowVesselSelection((path) => { craftFile = path; }, () => { abort = true; });
                while (!abort && string.IsNullOrEmpty(craftFile)) yield return wait;
            }
            messageState = Messages.None;
            if (abort || string.IsNullOrEmpty(craftFile)) { state = State.None; yield break; }
            if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: {craftFile} selected for spawning.");

            // Choose crew
            KerbalNames.Clear();
            crewCapacity = GetCrewCapacity(craftFile, out vesselNameToSpawn);
            if (BDArmorySettings.VESSEL_MOVER_CHOOSE_CREW)
            { yield return ChooseCrew(); }
            messageState = Messages.None;

            // Select location
            yield return GetSpawnPoint();
            messageState = Messages.None;
            if (geoCoords == Vector3d.zero) { state = State.None; yield break; }

            // Store the camera view
            var camera = FlightCamera.fetch;
            var cameraOffset = FlightGlobals.ActiveVessel != null ? camera.transform.position - FlightGlobals.ActiveVessel.transform.position : Vector3.zero;

            // Spawn the craft
            yield return SpawnVessel(craftFile, geoCoords.x, geoCoords.y, geoCoords.z + 1000f, kerbalNames: KerbalNames); // Spawn 1km higher than requested and then move it down.
            messageState = Messages.None;
            if (spawnFailureReason != SpawnFailureReason.None) { state = State.None; yield break; }
            if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Spawned {spawnedVessel.vesselName} at {geoCoords:G6}");
            while (spawnedVessel != null && (!spawnedVessel.loaded || spawnedVessel.packed)) yield return wait;
            if (spawnedVessel != null)
            {
                var up = (spawnedVessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
                if (FlightGlobals.currentMainBody.hasSolidSurface)
                {
                    var safeAltitude = SafeAltitude(spawnedVessel);
                    if (!BDArmorySettings.VESSEL_MOVER_BELOW_WATER) safeAltitude = Mathf.Min((float)spawnedVessel.altitude, safeAltitude);
                    spawnedVessel.Translate((2f * spawnedVessel.GetRadius() - safeAltitude) * up);
                }
                else // No surface to lower to!
                {
                    spawnedVessel.Translate(-1000f * up);
                }
                spawnedVessel.SetWorldVelocity(Vector3d.zero);
            }

            // Switch to it when possible
            yield return LoadedVesselSwitcher.Instance.SwitchToVesselWhenPossible(spawnedVessel);
            if (!IsValid(spawnedVessel))
            {
                Debug.LogWarning($"[BDArmory.VesselMover]: The spawned vessel disappeared before we could switch to it!");
                state = State.None;
                yield break;
            }

            // Restore the camera view
            camera.SetCamCoordsFromPosition(spawnedVessel.transform.position + cameraOffset);

            // Switch to moving mode
            yield return MoveVessel(spawnedVessel);
            spawnedVessel = null; // Clear the reference to the spawned vessel.
        }

        void RecoverVessel()
        {
            var vessel = FlightGlobals.ActiveVessel;
            var nearestOtherVessel = FlightGlobals.VesselsLoaded.Where(v => v != vessel).OrderBy(v => (v.transform.position - vessel.transform.position).sqrMagnitude).FirstOrDefault();
            if (nearestOtherVessel != null)
            {
                if (BDArmorySettings.DEBUG_SPAWNING) Debug.Log($"[BDArmory.VesselMover]: Switching to nearest vessel {nearestOtherVessel.vesselName}");
                LoadedVesselSwitcher.Instance.ForceSwitchVessel(nearestOtherVessel);
            }
            SpawnUtils.RemoveVessel(vessel);
        }

        int GetCrewCapacity(string craftFile, out string vesselName)
        {
            CraftProfileInfo.PrepareCraftMetaFileLoad();
            var craftMeta = $"{Path.GetFileNameWithoutExtension(craftFile)}.loadmeta";
            var meta = new CraftProfileInfo();
            if (File.Exists(craftMeta)) // If the loadMeta file exists, use it, otherwise generate one.
            {
                meta.LoadFromMetaFile(craftMeta);
            }
            else
            {
                var craftNode = ConfigNode.Load(craftFile);
                meta.LoadDetailsFromCraftFile(craftNode, craftFile);
                meta.SaveToMetaFile(craftMeta);
            }
            int crewCapacity = 0;
            vesselName = meta.shipName;
            foreach (var partName in meta.partNames)
            {
                if (SpawnUtils.PartCrewCounts.ContainsKey(partName))
                    crewCapacity += SpawnUtils.PartCrewCounts[partName];
            }
            return crewCapacity;
        }

        IEnumerator ChooseCrew()
        {
            messageState = Messages.ChoosingCrew;
            ShowCrewSelection(new Vector2(Screen.width / 2, Screen.height / 2));
            while (showCrewSelection)
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    HideCrewSelection();
                    break;
                }
                yield return wait;
            }
        }

        IEnumerator GetSpawnPoint()
        {
            messageState = Messages.ChoosingSpawnPoint;
            // Use the same indicator as the original VesselMover for familiarity.
            GameObject indicatorObject = new GameObject();
            LineRenderer lr = indicatorObject.AddComponent<LineRenderer>();
            lr.material = new Material(Shader.Find("KSP/Particles/Alpha Blended"));
            lr.material.SetColor("_TintColor", Color.green);
            lr.material.mainTexture = Texture2D.whiteTexture;
            lr.useWorldSpace = false;

            Vector3[] positions = new Vector3[] { Vector3.zero, 10 * Vector3.forward };
            lr.SetPositions(positions);
            lr.positionCount = positions.Length;
            lr.startWidth = 0.1f;
            lr.endWidth = 1f;

            Vector3 mouseAim, point;
            Ray ray;
            bool altitudeCorrection = false;
            var currentMainBody = FlightGlobals.currentMainBody;
            while (true)
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    geoCoords = Vector3d.zero;
                    break;
                }

                mouseAim = new Vector3(Input.mousePosition.x / Screen.width, Input.mousePosition.y / Screen.height, 0);
                ray = FlightCamera.fetch.mainCamera.ViewportPointToRay(mouseAim);
                if (Physics.Raycast(ray, out RaycastHit hit, (ray.origin - currentMainBody.transform.position).magnitude, (int)(LayerMasks.Scenery | LayerMasks.Parts | LayerMasks.Wheels | LayerMasks.EVA)))
                {
                    point = hit.point;
                    altitudeCorrection = false;
                }
                else if (SphereRayIntersect(ray, currentMainBody.transform.position, (float)currentMainBody.Radius, out float distance))
                {
                    point = ray.GetPoint(distance);
                    altitudeCorrection = true;
                }
                else
                {
                    yield return wait;
                    continue;
                }

                indicatorObject.transform.position = point;
                indicatorObject.transform.rotation = Quaternion.LookRotation(point - currentMainBody.transform.position);

                if (Input.GetMouseButtonDown(0))
                {
                    currentMainBody.GetLatLonAlt(point, out geoCoords.x, out geoCoords.y, out geoCoords.z);
                    if (altitudeCorrection) geoCoords.z = currentMainBody.TerrainAltitude(geoCoords.x, geoCoords.y, BDArmorySettings.VESSEL_MOVER_BELOW_WATER);
                    break;
                }
                yield return wait;
            }
            Destroy(indicatorObject);
        }

        bool SphereRayIntersect(Ray ray, Vector3 sphereCenter, float sphereRadius, out float distance)
        {
            distance = 0;
            Vector3 n = ray.direction;
            Vector3 R = ray.origin - sphereCenter;
            float r = sphereRadius;
            float d = Vector3.Dot(n, R); // d is non-positive if the ray originates outside the sphere and intersects it
            if (d > 0) return false;
            var G = d * d - R.sqrMagnitude + r * r;
            if (G < 0) return false;
            distance = -d - BDAMath.Sqrt(G);
            return true;
        }

        IEnumerator SpawnVessel(string craftUrl, double latitude, double longitude, double altitude, float initialHeading = 90f, float initialPitch = 0f, HashSet<string> kerbalNames = null)
        {
            messageState = Messages.LoadingCraft;
            spawnFailureReason = SpawnFailureReason.None; // Reset the spawn failure reason.
            var spawnPoint = FlightGlobals.currentMainBody.GetWorldSurfacePosition(latitude, longitude, altitude);
            var radialUnitVector = (spawnPoint - FlightGlobals.currentMainBody.transform.position).normalized;
            var north = VectorUtils.GetNorthVector(spawnPoint, FlightGlobals.currentMainBody);
            var direction = Vector3.ProjectOnPlane(Quaternion.AngleAxis(initialHeading, radialUnitVector) * north, radialUnitVector).normalized;
            var crew = new List<ProtoCrewMember>();
            if (kerbalNames != null)
            {
                foreach (var kerbalName in kerbalNames) crew.Add(HighLogic.CurrentGame.CrewRoster[kerbalName]);
                VesselSpawner.ReservedCrew = crew.Select(crew => crew.name).ToHashSet(); // Reserve the crew so they don't get swapped out.
                foreach (var c in crew) c.rosterStatus = ProtoCrewMember.RosterStatus.Available; // Set all the requested crew as available.
            }
            VesselSpawnConfig vesselSpawnConfig = new VesselSpawnConfig(craftUrl, spawnPoint, direction, (float)altitude, initialPitch, false, crew: crew);

            // Spawn vessel.
            yield return SpawnSingleVessel(vesselSpawnConfig);
            VesselSpawner.ReservedCrew.Clear(); // Clear the reserved crew again.
            if (spawnFailureReason != SpawnFailureReason.None) { state = State.None; yield break; }
            var vessel = spawnedVessels[latestSpawnedVesselName];
            if (vessel == null)
            {
                spawnFailureReason = SpawnFailureReason.VesselFailedToSpawn;
                state = State.None;
                yield break;
            }
            if (vesselSpawnConfig.editorFacility == EditorFacility.VAB) vessel.SetRotation(Quaternion.AngleAxis(90, radialUnitVector) * vessel.transform.rotation); // Rotate rockets to the same orientation as the launch pad.
            spawnedVessel = vessel;
        }

        public override IEnumerator Spawn(SpawnConfig spawnConfig) { yield break; } // Compliance with SpawnStrategy kludge.
        #endregion

        #region GUI
        static int guiCheckIndex = -1;
        bool helpShowing = false;
        bool ready = false;
        float windowWidth = 300;
        enum Messages { None, Custom, OpeningCraftBrowser, ChoosingCrew, ChoosingSpawnPoint, LoadingCraft, EasingCraft }
        Messages messageState { get { return _messageState; } set { _messageState = value; if (value != Messages.None) messageDisplayTime = Time.time + 5; } }
        Messages _messageState = Messages.None;
        string customMessage = "";
        float messageDisplayTime = 0;


        private void OnGUI()
        {
            if (!(ready && BDArmorySetup.GAME_UI_ENABLED))
                return;

            if (BDArmorySetup.Instance.showVesselMoverGUI)
            {
                BDArmorySetup.SetGUIOpacity();
                BDArmorySetup.WindowRectVesselMover = GUILayout.Window(
                    GUIUtility.GetControlID(FocusType.Passive),
                    BDArmorySetup.WindowRectVesselMover,
                    WindowVesselMover,
                    StringUtils.Localize("#LOC_BDArmory_VesselMover_Title"), // "BDA Vessel Mover"
                    BDArmorySetup.BDGuiSkin.window,
                    GUILayout.Width(windowWidth)
                );
                if (showVesselSelection)
                {
                    vesselSelectionWindowRect = GUILayout.Window(
                        GUIUtility.GetControlID(FocusType.Passive),
                        vesselSelectionWindowRect,
                        VesselSelectionWindow,
                        StringUtils.Localize("#LOC_BDArmory_Settings_CustomSpawnTemplate_VesselSelection"),
                        BDArmorySetup.BDGuiSkin.window
                    );
                }
                else if (showCrewSelection)
                {
                    crewSelectionWindowRect = GUILayout.Window(
                        GUIUtility.GetControlID(FocusType.Passive),
                        crewSelectionWindowRect,
                        CrewSelectionWindow,
                        StringUtils.Localize("#LOC_BDArmory_Settings_CustomSpawnTemplate_CrewSelection"),
                        BDArmorySetup.BDGuiSkin.window
                    );
                }
                BDArmorySetup.SetGUIOpacity(false);
                GUIUtils.UpdateGUIRect(BDArmorySetup.WindowRectVesselMover, guiCheckIndex);
            }
            else
            {
                if (showCrewSelection)
                {
                    KerbalNames.Clear();
                    HideCrewSelection();
                }
                if (showVesselSelection)
                {
                    HideVesselSelection();
                }
            }

            if (Time.time > messageDisplayTime) messageState = Messages.None;
            switch (messageState)
            {
                case Messages.Custom:
                    DrawShadowedMessage(customMessage);
                    break;
                case Messages.OpeningCraftBrowser:
                    DrawShadowedMessage("Opening Craft Browser...");
                    break;
                case Messages.ChoosingCrew:
                    DrawShadowedMessage("Opening Crew Selection...");
                    break;
                case Messages.ChoosingSpawnPoint:
                    DrawShadowedMessage("Click somewhere to spawn!");
                    break;
                case Messages.LoadingCraft:
                    DrawShadowedMessage("Loading Craft...");
                    break;
                case Messages.EasingCraft:
                    DrawShadowedMessage("Easing Craft for up to 10s...");
                    break;
            }
        }

        GUIStyle messageStyle;
        GUIStyle messageShadowStyle;
        void ConfigureStyles()
        {
            messageStyle = new GUIStyle(HighLogic.Skin.label);
            messageStyle.fontSize = 22;
            messageStyle.alignment = TextAnchor.UpperCenter;

            messageShadowStyle = new GUIStyle(messageStyle);
            messageShadowStyle.normal.textColor = new Color(0, 0, 0, 0.75f);
        }

        void DrawShadowedMessage(string message)
        {
            Rect labelRect = new Rect(0, (Screen.height * 0.25f) + (Mathf.Sin(2 * Time.time) * 5), Screen.width, 200);
            Rect shadowRect = new Rect(labelRect);
            shadowRect.position += new Vector2(2, 2);
            GUI.Label(shadowRect, message, messageShadowStyle);
            GUI.Label(labelRect, message, messageStyle);
        }

        void OnVesselChanged(Vessel vessel)
        {
            if (!IsValid(vessel))
            { state = State.None; }
            if (movingVessels.Contains(vessel))
            { state = State.Moving; }
            else if (loweringVessels.Contains(vessel))
            { state = State.Lowering; }
            else
            { state = State.None; }
            // Clean the moving and lowering vessel hashsets.
            movingVessels = movingVessels.Where(v => IsValid(v)).ToHashSet();
            loweringVessels = loweringVessels.Where(v => IsValid(v)).ToHashSet();
        }

        void WindowVesselMover(int id)
        {
            GUI.DragWindow(new Rect(0, 0, BDArmorySetup.WindowRectVesselMover.width, 20));
            GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
            switch (state)
            {
                case State.None:
                    {
                        if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_MoveVessel"), BDArmorySetup.ButtonStyle, GUILayout.Height(40))) StartCoroutine(MoveVessel(FlightGlobals.ActiveVessel));
                        if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_SpawnVessel"), BDArmorySetup.ButtonStyle, GUILayout.Height(40))) StartCoroutine(SpawnVessel());
                        if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_RecoverVessel"), BDArmorySetup.ButtonStyle, GUILayout.Height(20))) RecoverVessel();
                        GUILayout.BeginHorizontal();
                        BDArmorySettings.VESSEL_MOVER_CHOOSE_CREW = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_CHOOSE_CREW, StringUtils.Localize("#LOC_BDArmory_VesselMover_ChooseCrew"));
                        BDArmorySettings.VESSEL_MOVER_CLASSIC_CRAFT_CHOOSER = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_CLASSIC_CRAFT_CHOOSER, StringUtils.Localize("#LOC_BDArmory_VesselMover_ClassicChooser"));
                        GUILayout.EndHorizontal();
                        break;
                    }
                case State.Moving:
                    {
                        if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_PlaceVessel"), BDArmorySetup.ButtonStyle, GUILayout.Height(40))) StartCoroutine(PlaceVessel(FlightGlobals.ActiveVessel));
                        if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_DropVessel"), BDArmorySetup.ButtonStyle, GUILayout.Height(40))) DropVessel(FlightGlobals.ActiveVessel);
                        GUILayout.BeginHorizontal();
                        GUILayout.BeginVertical();
                        BDArmorySettings.VESSEL_MOVER_ENABLE_BRAKES = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_ENABLE_BRAKES, StringUtils.Localize("#LOC_BDArmory_VesselMover_EnableBrakes"));
                        BDArmorySettings.VESSEL_MOVER_LOWER_FAST = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_LOWER_FAST, StringUtils.Localize("#LOC_BDArmory_VesselMover_LowerFast"));
                        BDArmorySettings.VESSEL_MOVER_DONT_WORRY_ABOUT_COLLISIONS = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_DONT_WORRY_ABOUT_COLLISIONS, StringUtils.Localize("#LOC_BDArmory_VesselMover_DontWorryAboutCollisions"));
                        GUILayout.EndVertical();
                        GUILayout.BeginVertical();
                        BDArmorySettings.VESSEL_MOVER_ENABLE_SAS = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_ENABLE_SAS, StringUtils.Localize("#LOC_BDArmory_VesselMover_EnableSAS"));
                        BDArmorySettings.VESSEL_MOVER_BELOW_WATER = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_BELOW_WATER, StringUtils.Localize("#LOC_BDArmory_VesselMover_BelowWater"));
                        GUILayout.EndVertical();
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_MinLowerSpeed")}: {BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED}", GUILayout.Width(130));
                        BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED = BDAMath.RoundToUnit(GUILayout.HorizontalSlider(BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED, 0.1f, 1f), 0.1f);
                        GUILayout.EndHorizontal();
                        if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_Generic_Help"), helpShowing ? BDArmorySetup.SelectedButtonStyle : BDArmorySetup.ButtonStyle, GUILayout.Height(20)))
                        {
                            helpShowing = !helpShowing;
                            if (!helpShowing) ResetWindowHeight();
                        }
                        if (helpShowing)
                        {
                            GUILayout.BeginVertical();
                            GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_Movement")}: {GameSettings.PITCH_DOWN.primary}, {GameSettings.PITCH_UP.primary}, {GameSettings.YAW_LEFT.primary}, {GameSettings.YAW_RIGHT.primary}");
                            GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_Roll")}: {GameSettings.ROLL_LEFT.primary}, {GameSettings.ROLL_RIGHT.primary}");
                            GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_Pitch")}: {GameSettings.TRANSLATE_DOWN.primary}, {GameSettings.TRANSLATE_UP.primary}");
                            GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_Yaw")}: {GameSettings.TRANSLATE_LEFT.primary}, {GameSettings.TRANSLATE_RIGHT.primary}");
                            GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_AutoRotateRocket")}: {GameSettings.TRANSLATE_BACK.primary}");
                            GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_AutoRotatePlane")}: {GameSettings.TRANSLATE_FWD.primary}");
                            GUILayout.Label(StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_CycleAltitudes"));
                            GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_ResetAltitude")}: {GameSettings.THROTTLE_CUTOFF.primary}");
                            GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_Help_AdjustAltitude")}: {GameSettings.THROTTLE_UP.primary}, {GameSettings.THROTTLE_DOWN.primary}");
                            GUILayout.EndVertical();
                        }
                        break;
                    }
                case State.Lowering:
                    {
                        if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_MoveVessel"), BDArmorySetup.ButtonStyle, GUILayout.Height(40))) { DropVessel(FlightGlobals.ActiveVessel); StartCoroutine(MoveVessel(FlightGlobals.ActiveVessel)); }
                        if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_DropVessel"), BDArmorySetup.ButtonStyle, GUILayout.Height(40))) DropVessel(FlightGlobals.ActiveVessel);
                        GUILayout.BeginHorizontal();
                        GUILayout.BeginVertical();
                        BDArmorySettings.VESSEL_MOVER_ENABLE_BRAKES = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_ENABLE_BRAKES, StringUtils.Localize("#LOC_BDArmory_VesselMover_EnableBrakes"));
                        BDArmorySettings.VESSEL_MOVER_LOWER_FAST = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_LOWER_FAST, StringUtils.Localize("#LOC_BDArmory_VesselMover_LowerFast"));
                        GUILayout.EndVertical();
                        GUILayout.BeginVertical();
                        BDArmorySettings.VESSEL_MOVER_ENABLE_SAS = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_ENABLE_SAS, StringUtils.Localize("#LOC_BDArmory_VesselMover_EnableSAS"));
                        BDArmorySettings.VESSEL_MOVER_BELOW_WATER = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_BELOW_WATER, StringUtils.Localize("#LOC_BDArmory_VesselMover_BelowWater"));
                        GUILayout.EndVertical();
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_VesselMover_MinLowerSpeed")}: {BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED}", GUILayout.Width(130));
                        BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED = BDAMath.RoundToUnit(GUILayout.HorizontalSlider(BDArmorySettings.VESSEL_MOVER_MIN_LOWER_SPEED, 0.1f, 1f), 0.1f);
                        GUILayout.EndHorizontal();
                        break;
                    }
                case State.Spawning:
                    {
                        GUILayout.Label($"Spawning craft...", BDArmorySetup.SelectedButtonStyle, GUILayout.Height(40));
                        BDArmorySettings.VESSEL_MOVER_CHOOSE_CREW = GUILayout.Toggle(BDArmorySettings.VESSEL_MOVER_CHOOSE_CREW, StringUtils.Localize("#LOC_BDArmory_VesselMover_ChooseCrew"));
                        break;
                    }
            }
            GUILayout.EndVertical();
            GUIUtils.RepositionWindow(ref BDArmorySetup.WindowRectVesselMover);
            GUIUtils.UpdateGUIRect(BDArmorySetup.WindowRectVesselMover, guiCheckIndex);
            GUIUtils.UseMouseEventInRect(BDArmorySetup.WindowRectVesselMover);
        }

        /// <summary>
        /// Reset the height of the window so that it shrinks.
        /// </summary>
        void ResetWindowHeight()
        {
            bool reposition = BDArmorySetup.WindowRectVesselMover.y + BDArmorySetup.WindowRectVesselMover.height == Screen.height;
            BDArmorySetup.WindowRectVesselMover.height = 0;
            if (reposition) BDArmorySetup.WindowRectVesselMover.y = Screen.height;
            GUIUtils.RepositionWindow(ref BDArmorySetup.WindowRectVesselMover);
        }

        public void SetVisible(bool visible)
        {
            if (!visible && craftBrowser != null) craftBrowser = null; // Clean up the craft browser.
            BDArmorySetup.Instance.showVesselMoverGUI = visible;
            GUIUtils.SetGUIRectVisible(guiCheckIndex, visible);
        }

        #region Vessel Selection
        internal static int _vesselGUICheckIndex = -1;
        bool showVesselSelection = false;
        Rect vesselSelectionWindowRect = new Rect(0, 0, 600, 800);
        Vector2 vesselSelectionScrollPos = default;
        float vesselSelectionTimer = 0;
        string selectedVesselURL = "";

        public void ShowVesselSelection(Action<string> selectedCallback = null, Action cancelledCallback = null)
        {
            if (craftBrowser == null)
            {
                craftBrowser = new CustomCraftBrowserDialog();
                craftBrowser.UpdateList(craftBrowser.facility);
            }
            craftBrowser.selectFileCallback = selectedCallback;
            craftBrowser.cancelledCallback = cancelledCallback;
            vesselSelectionWindowRect.position = new Vector2((Screen.width - vesselSelectionWindowRect.width) / 2, (Screen.height - vesselSelectionWindowRect.height) / 2);
            selectedVesselURL = "";
            showVesselSelection = true;
            vesselSelectionTimer = Time.realtimeSinceStartup;
            GUIUtils.SetGUIRectVisible(_vesselGUICheckIndex, true);
        }

        public void HideVesselSelection()
        {
            showVesselSelection = false;
            GUIUtils.SetGUIRectVisible(_vesselGUICheckIndex, false);
        }

        public void VesselSelectionWindow(int windowID)
        {
            GUI.DragWindow(new Rect(0, 0, vesselSelectionWindowRect.width, 20));
            GUILayout.BeginVertical();
            vesselSelectionScrollPos = GUILayout.BeginScrollView(vesselSelectionScrollPos, GUI.skin.box, GUILayout.Width(vesselSelectionWindowRect.width - 15), GUILayout.MaxHeight(vesselSelectionWindowRect.height - 60));
            using (var vessels = craftBrowser.craftList.GetEnumerator())
                while (vessels.MoveNext())
                {
                    var vesselURL = vessels.Current.Key;
                    var vesselInfo = vessels.Current.Value;
                    if (vesselURL == null || vesselInfo == null) continue;
                    GUILayout.BeginHorizontal(); // Vessel buttons
                    if (GUILayout.Button($"{vesselInfo.shipName}", selectedVesselURL == vesselURL ? BDArmorySetup.SelectedButtonStyle : BDArmorySetup.ButtonStyle, GUILayout.Height(50), GUILayout.MaxWidth(vesselSelectionWindowRect.width - 190)))
                    {
                        if (Time.realtimeSinceStartup - vesselSelectionTimer < 0.5f)
                        {
                            if (craftBrowser.selectFileCallback != null) craftBrowser.selectFileCallback(vesselURL);
                            HideVesselSelection();
                        }
                        else if (selectedVesselURL == vesselURL) { selectedVesselURL = ""; }
                        else { selectedVesselURL = vesselURL; }
                        vesselSelectionTimer = Time.realtimeSinceStartup;
                    }
                    GUILayout.Label($"{StringUtils.Localize("#LOC_BDArmory_Settings_CustomSpawnTemplate_Parts")}: {vesselInfo.partCount},  {StringUtils.Localize("#LOC_BDArmory_Settings_CustomSpawnTemplate_Mass")}: {(vesselInfo.totalMass < 1000f ? $"{vesselInfo.totalMass:G3}t" : $"{vesselInfo.totalMass / 1000f:G3}kt")}\nCrew count: {(craftBrowser.crewCounts.ContainsKey(vesselURL) ? craftBrowser.crewCounts[vesselURL].ToString() : "unknown")}\n{(vesselInfo.UnavailableShipParts.Count > 0 ? $"<b><color=red>{StringUtils.Localize("#LOC_BDArmory_Settings_CustomSpawnTemplate_InvalidParts")}</color></b>" : $"{StringUtils.Localize("#LOC_BDArmory_Settings_CustomSpawnTemplate_Version")}: {(vesselInfo.compatibility == VersionCompareResult.COMPATIBLE ? $"{vesselInfo.version}" : $"<color=red>{vesselInfo.version}</color>")}{(vesselInfo.UnavailableShipPartModules.Count > 0 ? $"  <color=red>{StringUtils.Localize("#LOC_BDArmory_Settings_CustomSpawnTemplate_UnknownModules")}</color>" : "")}")}", CustomCraftBrowserDialog.vesselInfoStyle);
                    GUILayout.EndHorizontal();
                }
            GUILayout.EndScrollView();
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_Generic_Select"), selectedVesselURL != "" ? BDArmorySetup.ButtonStyle : BDArmorySetup.SelectedButtonStyle) && selectedVesselURL != "")
            {
                if (craftBrowser.selectFileCallback != null) craftBrowser.selectFileCallback(selectedVesselURL);
                HideVesselSelection();
            }
            if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_Generic_Cancel"), BDArmorySetup.ButtonStyle))
            {
                if (craftBrowser.cancelledCallback != null) craftBrowser.cancelledCallback();
                HideVesselSelection();
            }
            if (GUILayout.Button(craftBrowser.facility == EditorFacility.SPH ? "VAB" : "SPH", BDArmorySetup.ButtonStyle, GUILayout.Width(vesselSelectionWindowRect.width / 6)))
            {
                craftBrowser.facility = (craftBrowser.facility == EditorFacility.SPH ? EditorFacility.VAB : EditorFacility.SPH);
                craftBrowser.UpdateList(craftBrowser.facility);
            }
            if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_Settings_CustomSpawnTemplate_Refresh"), BDArmorySetup.ButtonStyle, GUILayout.Width(vesselSelectionWindowRect.width / 6)))
            { craftBrowser.UpdateList(craftBrowser.facility); }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUIUtils.RepositionWindow(ref vesselSelectionWindowRect);
            GUIUtils.UpdateGUIRect(vesselSelectionWindowRect, _vesselGUICheckIndex);
            GUIUtils.UseMouseEventInRect(vesselSelectionWindowRect);
        }

        #endregion

        #region Crew Selection
        internal static int _crewGUICheckIndex = -1;
        bool showCrewSelection = false;
        Rect crewSelectionWindowRect = new Rect(0, 0, 300, 400);
        Vector2 crewSelectionScrollPos = default;
        float crewSelectionTimer = 0;
        HashSet<string> ActiveCrewMembers = new HashSet<string>();
        bool newCustomKerbal = false;
        string newKerbalName = "";
        ProtoCrewMember.Gender newKerbalGender = ProtoCrewMember.Gender.Male;
        bool removeKerbals = false;

        /// <summary>
        /// Show the crew selection window.
        /// </summary>
        /// <param name="position">Position of the mouse click.</param>
        /// <param name="vesselSpawnConfig">The VesselSpawnConfig clicked on.</param>
        public void ShowCrewSelection(Vector2 position)
        {
            crewSelectionWindowRect.position = position + new Vector2(50, -crewSelectionWindowRect.height / 2); // Centred and slightly offset to allow clicking the same spot.
            showCrewSelection = true;
            // Find any crew on active vessels.
            ActiveCrewMembers.Clear();
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (vessel == null || !vessel.loaded) continue;
                foreach (var part in vessel.Parts)
                {
                    if (part == null) continue;
                    foreach (var crew in part.protoModuleCrew)
                    {
                        if (crew == null) continue;
                        ActiveCrewMembers.Add(crew.name);
                    }
                }
            }
            GUIUtils.SetGUIRectVisible(_crewGUICheckIndex, true);
            foreach (var crew in HighLogic.CurrentGame.CrewRoster.Kerbals(ProtoCrewMember.KerbalType.Crew)) // Set any non-assigned crew as available.
            {
                if (crew.rosterStatus != ProtoCrewMember.RosterStatus.Assigned)
                    crew.rosterStatus = ProtoCrewMember.RosterStatus.Available;
            }
            crewSelectionTimer = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Hide the crew selection window.
        /// </summary>
        public void HideCrewSelection()
        {
            showCrewSelection = false;
            newCustomKerbal = false;
            removeKerbals = false;
            GUIUtils.SetGUIRectVisible(_crewGUICheckIndex, false);
        }

        /// <summary>
        /// Crew selection window.
        /// </summary>
        /// <param name="windowID"></param>
        public void CrewSelectionWindow(int windowID)
        {
            KerbalRoster kerbalRoster = HighLogic.CurrentGame.CrewRoster;
            GUI.DragWindow(new Rect(0, 0, crewSelectionWindowRect.width, 20));
            GUILayout.BeginVertical();
            if (BDArmorySettings.VESSEL_SPAWN_FILL_SEATS == 0) GUILayout.Label($"Select up to {crewCapacity} kerbals to populate {vesselNameToSpawn}.");
            crewSelectionScrollPos = GUILayout.BeginScrollView(crewSelectionScrollPos, GUI.skin.box, GUILayout.Width(crewSelectionWindowRect.width - 15), GUILayout.MaxHeight(crewSelectionWindowRect.height - 60));
            using (var kerbals = kerbalRoster.Kerbals(ProtoCrewMember.KerbalType.Crew).GetEnumerator())
                while (kerbals.MoveNext())
                {
                    ProtoCrewMember crewMember = kerbals.Current;
                    if (crewMember == null || ActiveCrewMembers.Contains(crewMember.name)) continue;
                    if (GUILayout.Button($"{crewMember.name}, {crewMember.gender}, {crewMember.trait}", KerbalNames.Contains(crewMember.name) ? BDArmorySetup.SelectedButtonStyle : BDArmorySetup.ButtonStyle))
                    {
                        if (Time.realtimeSinceStartup - crewSelectionTimer < 0.5f)
                        {
                            KerbalNames.Add(crewMember.name);
                            HideCrewSelection();
                        }
                        else if (KerbalNames.Contains(crewMember.name)) KerbalNames.Remove(crewMember.name);
                        else KerbalNames.Add(crewMember.name);
                        crewSelectionTimer = Time.realtimeSinceStartup;
                    }
                }
            GUILayout.EndScrollView();
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_Generic_Select"), BDArmorySetup.ButtonStyle))
            { HideCrewSelection(); }
            if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_Any"), BDArmorySetup.ButtonStyle, GUILayout.Width(crewSelectionWindowRect.width / 6)))
            { KerbalNames.Clear(); HideCrewSelection(); }
            if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_Generic_New"), newCustomKerbal ? BDArmorySetup.SelectedButtonStyle : BDArmorySetup.ButtonStyle, GUILayout.Width(crewSelectionWindowRect.width / 6)))
            {
                // Create a new Kerbal!
                newCustomKerbal = !newCustomKerbal;
                newKerbalName = "Enter a new Kerbal name...";
            }
            if (GUILayout.Button("X", removeKerbals ? BDArmorySetup.SelectedButtonStyle : BDArmorySetup.CloseButtonStyle, GUILayout.Width(27)))
            {
                // Remove selected Kerbals!
                removeKerbals = !removeKerbals;
            }
            GUILayout.EndHorizontal();
            if (newCustomKerbal)
            {
                newKerbalName = GUILayout.TextField(newKerbalName);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_Generic_OK"), BDArmorySetup.ButtonStyle))
                {
                    if (!string.IsNullOrEmpty(newKerbalName) && newKerbalName != "Enter a new Kerbal name...")
                    {
                        if (HighLogic.CurrentGame.CrewRoster.Exists(newKerbalName))
                        {
                            customMessage = $"Failed to add {newKerbalName}. They already exist!";
                            messageState = Messages.Custom;
                            Debug.LogWarning($"[BDArmory.VesselMover]: {customMessage}");
                        }
                        else
                        {
                            var crewMember = HighLogic.CurrentGame.CrewRoster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                            if (crewMember.ChangeName(newKerbalName))
                            {
                                crewMember.gender = newKerbalGender;
                                KerbalRoster.SetExperienceTrait(crewMember, KerbalRoster.pilotTrait); // Make the kerbal a pilot (so they can use SAS properly).
                                KerbalRoster.SetExperienceLevel(crewMember, KerbalRoster.GetExperienceMaxLevel()); // Make them experienced.
                                crewMember.isBadass = true; // Make them bad-ass (likes nearby explosions).
                            }
                            else
                            {
                                customMessage = $"Failed to set name of {crewMember.name} to {newKerbalName}";
                                messageState = Messages.Custom;
                                Debug.LogWarning($"[BDArmory.VesselMover]: {customMessage}");
                                HighLogic.CurrentGame.CrewRoster.Remove(crewMember.name);
                            }
                        }
                    }
                    newCustomKerbal = false;
                }
                if (GUILayout.Button(newKerbalGender.ToStringCached(), BDArmorySetup.ButtonStyle, GUILayout.Width(crewSelectionWindowRect.width / 4)))
                {
                    var genders = Enum.GetValues(typeof(ProtoCrewMember.Gender)).Cast<ProtoCrewMember.Gender>();
                    bool found = false, set = false;
                    foreach (var gender in genders)
                    {
                        if (found) { newKerbalGender = gender; set = true; break; }
                        if (newKerbalGender == gender) found = true;
                    }
                    if (!set) newKerbalGender = genders.First();
                }
                GUILayout.EndHorizontal();
            }
            if (removeKerbals)
            {
                if (GUILayout.Button(StringUtils.Localize("#LOC_BDArmory_VesselMover_ReallyRemoveKerbals"), BDArmorySetup.CloseButtonStyle))
                {
                    var cantRemove = KerbalRoster.GenerateInitialCrewRoster(HighLogic.CurrentGame.Mode).Crew.Select(crew => crew.name).ToHashSet();
                    KerbalNames = KerbalNames.Where(kerbal => !cantRemove.Contains(kerbal)).ToHashSet();
                    customMessage = $"Removing {string.Join(", ", KerbalNames)}";
                    messageState = Messages.Custom;
                    foreach (var kerbalName in KerbalNames) HighLogic.CurrentGame.CrewRoster.Remove(kerbalName);
                    KerbalNames.Clear();
                    removeKerbals = false;
                }
            }
            GUILayout.EndVertical();
            GUIUtils.RepositionWindow(ref crewSelectionWindowRect);
            GUIUtils.UpdateGUIRect(crewSelectionWindowRect, _crewGUICheckIndex);
            GUIUtils.UseMouseEventInRect(crewSelectionWindowRect);
        }
        #endregion

        const int circleRes = 24;
        private LineRenderer moveIndicator;
        Vector3[] moveIndicatorPositions = new Vector3[circleRes + 3];
        private void DrawMovingIndicator()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || !vessel.loaded || vessel.packed) return;

            var angle = 360f / circleRes;
            var radius = 2f + vessel.GetRadius();
            var centre = vessel.CoM;
            VectorUtils.GetWorldCoordinateFrame(vessel.mainBody, centre, out Vector3 up, out Vector3 north, out Vector3 right);

            moveIndicatorPositions[0] = centre + radius * north;
            for (int i = 1; i < circleRes; i++)
            {
                moveIndicatorPositions[i] = centre + Quaternion.AngleAxis(i * angle, up) * north * radius;
            }
            moveIndicatorPositions[circleRes] = centre + radius * north;
            moveIndicatorPositions[circleRes + 1] = centre;
            moveIndicatorPositions[circleRes + 2] = centre + RadarAltitude(vessel) * -up;

            moveIndicator.SetPositions(moveIndicatorPositions);
        }
        #endregion
    }
}