using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using KSP.UI.Screens;

using BDArmory.Settings;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Competition.VesselSpawning
{
    /// <summary>
    /// Spawn teams of craft in a custom template.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CustomTemplateSpawning : VesselSpawnerBase
    {
        public static CustomTemplateSpawning Instance;
        protected override void Awake()
        {
            base.Awake();
            if (Instance != null) Destroy(Instance);
            Instance = this;
        }

        void Start()
        {
            if (customSpawnConfig == null) LoadTemplate();
            StartCoroutine(WaitForBDASettings());
        }

        void LogMessage(string message, bool toScreen = true, bool toLog = true) => LogMessageFrom("CustomTemplateSpawning", message, toScreen, toLog);

        IEnumerator WaitForBDASettings()
        {
            yield return new WaitUntil(() => BDArmorySetup.Instance is not null);
            if (_crewGUICheckIndex < 0) _crewGUICheckIndex = GUIUtils.RegisterGUIRect(new Rect());
            if (_vesselGUICheckIndex < 0) _vesselGUICheckIndex = GUIUtils.RegisterGUIRect(new Rect());
        }

        public override IEnumerator Spawn(SpawnConfig spawnConfig)
        {
            var customSpawnConfig = spawnConfig as CustomSpawnConfig;
            if (customSpawnConfig == null) yield break;
            SpawnCustomTemplateAsCoroutine(customSpawnConfig);
        }

        public void CancelSpawning()
        {
            if (vesselsSpawning)
            {
                vesselsSpawning = false;
                LogMessage("Vessel spawning cancelled.");
            }
            if (spawnCustomTemplateCoroutine != null)
            {
                StopCoroutine(spawnCustomTemplateCoroutine);
                spawnCustomTemplateCoroutine = null;
            }
        }

        #region Custom template spawning
        /// <summary>
        /// Prespawn initialisation to handle camera and body changes and to ensure that only a single spawning coroutine is running.
        /// </summary>
        /// <param name="spawnConfig">The spawn config for the new spawning.</param>
        public override void PreSpawnInitialisation(SpawnConfig spawnConfig)
        {
            if (craftBrowser != null) craftBrowser = null; // Clean up the craft browser.

            base.PreSpawnInitialisation(spawnConfig);

            vesselsSpawning = true; // Signal that we've started the spawning vessels routine.
            vesselSpawnSuccess = false; // Set our success flag to false for now.
            spawnFailureReason = SpawnFailureReason.None; // Reset the spawn failure reason.
            if (spawnCustomTemplateCoroutine != null)
                StopCoroutine(spawnCustomTemplateCoroutine);
        }

        public void SpawnCustomTemplate(CustomSpawnConfig spawnConfig)
        {
            if (spawnConfig == null) return;
            PreSpawnInitialisation(spawnConfig);
            spawnCustomTemplateCoroutine = StartCoroutine(SpawnCustomTemplateCoroutine(spawnConfig));
            LogMessage("Triggering vessel spawning at " + spawnConfig.latitude.ToString("G6") + ", " + spawnConfig.longitude.ToString("G6") + ", with altitude " + spawnConfig.altitude + "m.", false);
        }

        /// <summary>
        /// A coroutine version of the SpawnCustomTemplate function that performs the required prespawn initialisation.
        /// </summary>
        /// <param name="spawnConfig">The spawn config to use.</param>
        public IEnumerator SpawnCustomTemplateAsCoroutine(CustomSpawnConfig spawnConfig)
        {
            PreSpawnInitialisation(spawnConfig);
            LogMessage("Triggering vessel spawning at " + spawnConfig.latitude.ToString("G6") + ", " + spawnConfig.longitude.ToString("G6") + ", with altitude " + spawnConfig.altitude + "m.", false);
            yield return SpawnCustomTemplateCoroutine(spawnConfig);
        }

        private Coroutine spawnCustomTemplateCoroutine;
        // Spawns all vessels in an outward facing ring and lowers them to the ground. An altitude of 5m should be suitable for most cases.
        private IEnumerator SpawnCustomTemplateCoroutine(CustomSpawnConfig spawnConfig)
        {
            #region Initialisation and sanity checks
            // Tally up the craft to spawn and figure out teams.
            spawnConfig.craftFiles = spawnConfig.customVesselSpawnConfigs.SelectMany(team => team).Select(config => config.craftURL).Where(craftURL => !string.IsNullOrEmpty(craftURL)).ToList();
            LogMessage("Spawning " + spawnConfig.craftFiles.Count + " vessels at an altitude of " + spawnConfig.altitude.ToString("G0") + "m" + (spawnConfig.craftFiles.Count > 8 ? ", this may take some time..." : "."));
            #endregion

            yield return AcquireSpawnPoint(spawnConfig, 100f, false);
            if (spawnFailureReason != SpawnFailureReason.None)
            {
                vesselsSpawning = false;
                SpawnUtils.RevertSpawnLocationCamera(true, true);
                yield break;
            }

            // Configure the vessels' individual spawn configs.
            var vesselSpawnConfigs = new List<VesselSpawnConfig>();
            foreach (var customVesselSpawnConfig in spawnConfig.customVesselSpawnConfigs.SelectMany(config => config))
            {
                if (string.IsNullOrEmpty(customVesselSpawnConfig.craftURL)) continue;

                var vesselSpawnPoint = FlightGlobals.currentMainBody.GetWorldSurfacePosition(customVesselSpawnConfig.latitude, customVesselSpawnConfig.longitude, spawnConfig.altitude);
                var radialUnitVector = (vesselSpawnPoint - FlightGlobals.currentMainBody.transform.position).normalized;
                var refDirection = Math.Abs(Vector3.Dot(Vector3.up, radialUnitVector)) < 0.71f ? Vector3.up : Vector3.forward; // Avoid that the reference direction is colinear with the local surface normal.
                var crew = new List<ProtoCrewMember>();
                if (!string.IsNullOrEmpty(customVesselSpawnConfig.kerbalName)) crew.Add(HighLogic.CurrentGame.CrewRoster[customVesselSpawnConfig.kerbalName]);
                vesselSpawnConfigs.Add(new VesselSpawnConfig(
                    customVesselSpawnConfig.craftURL,
                    vesselSpawnPoint,
                    Vector3.ProjectOnPlane(Quaternion.AngleAxis(customVesselSpawnConfig.heading, radialUnitVector) * refDirection, radialUnitVector).normalized,
                    (float)spawnConfig.altitude,
                    0,
                    false,
                    customVesselSpawnConfig.teamIndex,
                    false,
                    crew
                ));
            }
            VesselSpawner.ReservedCrew = vesselSpawnConfigs.Where(config => config.crew.Count > 0).SelectMany(config => config.crew).Select(crew => crew.name).ToHashSet();
            foreach (var crew in vesselSpawnConfigs.Where(config => config.crew.Count > 0).SelectMany(config => config.crew)) crew.rosterStatus = ProtoCrewMember.RosterStatus.Available; // Set all the requested crew as available since we've just killed off everything.

            yield return SpawnVessels(vesselSpawnConfigs);
            VesselSpawner.ReservedCrew.Clear();
            if (spawnFailureReason != SpawnFailureReason.None)
            {
                vesselsSpawning = false;
                SpawnUtils.RevertSpawnLocationCamera(true, true);
                yield break;
            }

            #region Post-spawning
            // Spawning has succeeded, vessels have been renamed where necessary and vessels are ready. Time to assign teams and any other stuff.
            yield return PostSpawnMainSequence(spawnConfig, false);
            if (spawnFailureReason != SpawnFailureReason.None)
            {
                LogMessage("Vessel spawning FAILED! " + spawnFailureReason);
                vesselsSpawning = false;
                SpawnUtils.RevertSpawnLocationCamera(true, true);
                yield break;
            }

            // Revert the camera and focus on one of the vessels.
            SpawnUtils.RevertSpawnLocationCamera(true);
            if ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.state == Vessel.State.DEAD) && spawnedVessels.Count > 0)
            {
                LoadedVesselSwitcher.Instance.ForceSwitchVessel(spawnedVessels.Take(UnityEngine.Random.Range(1, spawnedVessels.Count)).Last().Value); // Update the camera.
            }
            FlightCamera.fetch.SetDistance(50);

            // Assign the vessels to teams.
            LogMessage("Assigning vessels to teams.", false);
            var teamVesselNames = new List<List<string>>();
            for (int i = 0; i < spawnedVesselsTeamIndex.Max(kvp => kvp.Value); ++i)
                teamVesselNames.Add(spawnedVesselsTeamIndex.Where(kvp => kvp.Value == i).Select(kvp => kvp.Key).ToList());
            LoadedVesselSwitcher.Instance.MassTeamSwitch(true, false, null, teamVesselNames); // Assign A, B, ...
            #endregion

            LogMessage("Vessel spawning SUCCEEDED!", true, BDArmorySettings.DEBUG_SPAWNING);
            vesselSpawnSuccess = true;
            vesselsSpawning = false;

            // Run the competition.
            BDACompetitionMode.Instance.StartCompetitionMode(BDArmorySettings.COMPETITION_DISTANCE, BDArmorySettings.COMPETITION_START_DESPITE_FAILURES);
        }
        #endregion

        #region Templates
        public CustomSpawnConfig customSpawnConfig = null;
        public void LoadTemplate(string templateName = null)
        {
            if (string.IsNullOrEmpty(templateName)) // Empty template
            {
                customSpawnConfig = new CustomSpawnConfig(
                    "",
                    new SpawnConfig(
                        BDArmorySettings.VESSEL_SPAWN_WORLDINDEX,
                        BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.x,
                        BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.y,
                        BDArmorySettings.VESSEL_SPAWN_ALTITUDE,
                        BDArmorySettings.VESSEL_SPAWN_EASE_IN_SPEED),
                    new List<List<CustomVesselSpawnConfig>>());
            }
            else
            {
                // Load the template from disk.
            }
        }

        /// <summary>
        /// Save the current setup as a template.
        /// Vessel positions rotations and teams are saved.
        /// </summary>
        /// <param name="templateName"></param>
        public void SaveTemplate()
        {
            // For the vessels in the vessel switcher, save the position, rotations and teams.
            // Also, save the planet and centroid of the positions to the SpawnConfig
            customSpawnConfig.worldIndex = BDArmorySettings.VESSEL_SPAWN_WORLDINDEX;
            var geoCoords = FlightGlobals.currentMainBody.GetLatitudeAndLongitude(LoadedVesselSwitcher.Instance.WeaponManagers.SelectMany(tm => tm.Value).Select(wm => wm.vessel.transform.position).Aggregate(Vector3.zero, (l, r) => l + r) / LoadedVesselSwitcher.Instance.WeaponManagers.SelectMany(tm => tm.Value).Count()); // Set the central spawn location at the centroid of the craft.
            customSpawnConfig.latitude = geoCoords.x;
            customSpawnConfig.longitude = geoCoords.y;
            customSpawnConfig.altitude = BDArmorySettings.VESSEL_SPAWN_ALTITUDE;
            customSpawnConfig.easeInSpeed = BDArmorySettings.VESSEL_SPAWN_EASE_IN_SPEED;
            customSpawnConfig.customVesselSpawnConfigs.Clear();
            int teamCount = 0;
            foreach (var team in LoadedVesselSwitcher.Instance.WeaponManagers)
            {
                var teamConfigs = new List<CustomVesselSpawnConfig>();
                foreach (var member in team.Value)
                {
                    CustomVesselSpawnConfig vesselSpawnConfig = new CustomVesselSpawnConfig();
                    geoCoords = FlightGlobals.currentMainBody.GetLatitudeAndLongitude(member.vessel.transform.position);
                    vesselSpawnConfig.latitude = geoCoords.x;
                    vesselSpawnConfig.longitude = geoCoords.y;
                    vesselSpawnConfig.heading = (Vector3.SignedAngle(member.vessel.north, member.vessel.ReferenceTransform.up, member.vessel.up) + 360f) % 360f;
                    vesselSpawnConfig.teamIndex = teamCount;
                    teamConfigs.Add(vesselSpawnConfig);
                }
                customSpawnConfig.customVesselSpawnConfigs.Add(teamConfigs);
                ++teamCount;
            }
            // FIXME Save this config to disk.
            Debug.Log($"DEBUG {customSpawnConfig.ToString()}");
        }

        /// <summary>
        /// Configure the spawn template with locally settable config values and perform a sanity check for being able to run a competition.
        /// </summary>
        /// <returns>true if there are sufficient non-empty teams for a competition, false otherwise</returns>
        public bool ConfigureTemplate()
        {
            // Sanity check
            if (customSpawnConfig.customVesselSpawnConfigs.Count(team => team.Count(cfg => !string.IsNullOrEmpty(cfg.craftURL)) > 0) < 2) // At least two non-empty teams.
            {
                BDACompetitionMode.Instance.competitionStatus.Add("Not enough vessels selected for a competition.");
                return false;
            }

            // Set the locally settable config values.
            customSpawnConfig.altitude = Mathf.Clamp(BDArmorySettings.VESSEL_SPAWN_ALTITUDE, 2f, 10f);
            customSpawnConfig.easeInSpeed = BDArmorySettings.VESSEL_SPAWN_EASE_IN_SPEED;
            customSpawnConfig.killEverythingFirst = true;
            return true;
        }

        #endregion

        #region UI
        void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (!BDArmorySetup.GAME_UI_ENABLED) return;
            if (showCrewSelection)
            {
                if (Event.current.type == EventType.MouseDown && !crewSelectionWindowRect.Contains(Event.current.mousePosition))
                {
                    HideCrewSelection();
                }
                else
                {
                    crewSelectionWindowRect = GUILayout.Window(GetInstanceID(), crewSelectionWindowRect, CrewSelectionWindow, "Crew Selection", BDArmorySetup.BDGuiSkin.window);
                }
            }
            if (showVesselSelection)
            {
                if (Event.current.type == EventType.MouseDown && !vesselSelectionWindowRect.Contains(Event.current.mousePosition))
                {
                    HideVesselSelection();
                }
                else
                {
                    vesselSelectionWindowRect = GUILayout.Window(GetInstanceID(), vesselSelectionWindowRect, VesselSelectionWindow, "Vessel Selection", BDArmorySetup.BDGuiSkin.window);
                }
            }
        }

        #region Template Selection
        bool showTemplateSelection = false;
        BDGUIComboBox templateSelector; // FIXME Use this drop-down box instead of a window for this.
        public void ShowTemplateSelection(Vector2 position)
        {
            // Read templates from disk, then show window
            showTemplateSelection = true;
        }
        void HideTemplateSelection()
        {
            showTemplateSelection = false;
        }
        public void TemplateSelectionWindow(int windowID)
        {
            // pick a template
        }
        #endregion

        CustomVesselSpawnConfig currentVesselSpawnConfig;
        List<CustomVesselSpawnConfig> currentTeamSpawnConfigs;
        #region Vessel Selection
        internal static int _vesselGUICheckIndex = -1;
        bool showVesselSelection = false;
        Rect vesselSelectionWindowRect = new Rect(0, 0, 500, 800);
        Vector2 vesselSelectionScrollPos = default;
        CustomCraftBrowserDialog craftBrowser;

        /// <summary>
        /// Show the vessel selection window.
        /// </summary>
        /// <param name="position">Position of the mouse click.</param>
        /// <param name="craftURL">The URL of the craft.</param>
        public void ShowVesselSelection(Vector2 position, CustomVesselSpawnConfig vesselSpawnConfig, List<CustomVesselSpawnConfig> teamSpawnConfigs)
        {
            if (showCrewSelection) HideCrewSelection();
            if (showVesselSelection && vesselSpawnConfig == currentVesselSpawnConfig)
            {
                HideVesselSelection();
                return;
            }
            currentVesselSpawnConfig = vesselSpawnConfig;
            currentTeamSpawnConfigs = teamSpawnConfigs;
            if (craftBrowser == null)
            {
                craftBrowser = new CustomCraftBrowserDialog();
                craftBrowser.UpdateList(craftBrowser.facility);
            }
            vesselSelectionWindowRect.position = position + new Vector2(-vesselSelectionWindowRect.width - 120, -vesselSelectionWindowRect.height / 2); // Centred and slightly offset to allow clicking the same spot.
            showVesselSelection = true;
            GUIUtils.SetGUIRectVisible(_vesselGUICheckIndex, true);
        }

        /// <summary>
        /// Hide the vessel selection window.
        /// </summary>
        public void HideVesselSelection(CustomVesselSpawnConfig vesselSpawnConfig = null)
        {
            if (vesselSpawnConfig != null)
            {
                vesselSpawnConfig.craftURL = null;
            }
            showVesselSelection = false;
            GUIUtils.SetGUIRectVisible(_vesselGUICheckIndex, false);
        }

        public void VesselSelectionWindow(int windowID)
        {
            GUI.DragWindow(new Rect(0, 0, vesselSelectionWindowRect.width - 20, 20));
            if (GUI.Button(new Rect(vesselSelectionWindowRect.width - 22, 5, 18, 18), "X", BDArmorySetup.BDGuiSkin.button))
            {
                HideVesselSelection();
                return;
            }
            GUILayout.BeginVertical();
            vesselSelectionScrollPos = GUILayout.BeginScrollView(vesselSelectionScrollPos, GUI.skin.box, GUILayout.Width(vesselSelectionWindowRect.width - 20), GUILayout.MaxHeight(vesselSelectionWindowRect.height - 60));
            using (var vessels = craftBrowser.craftList.GetEnumerator())
                while (vessels.MoveNext())
                {
                    var vesselURL = vessels.Current.Key;
                    var vesselInfo = vessels.Current.Value;
                    if (vesselURL == null || vesselInfo == null) continue;
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button($"{vesselInfo.shipName}", craftBrowser.vesselButtonStyle, GUILayout.MaxWidth(vesselSelectionWindowRect.width - 130)))
                    {
                        currentVesselSpawnConfig.craftURL = vesselURL;
                        foreach (var vesselSpawnConfig in currentTeamSpawnConfigs) // Set the other empty slots for the team to the same vessel.
                        {
                            if (string.IsNullOrEmpty(vesselSpawnConfig.craftURL))
                            {
                                vesselSpawnConfig.craftURL = vesselURL;
                            }
                        }
                        HideVesselSelection();
                    }
                    GUILayout.Label($"Parts: {vesselInfo.partCount}\nVersion: {vesselInfo.version}{(vesselInfo.compatibility == VersionCompareResult.COMPATIBLE ? "" : $"\n{vesselInfo.compatibility}")}", craftBrowser.vesselInfoStyle, GUILayout.Width(100));
                    GUILayout.EndHorizontal();
                }
            GUILayout.EndScrollView();
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear", BDArmorySetup.BDGuiSkin.button))
            {
                currentVesselSpawnConfig.craftURL = null;
                HideVesselSelection();
            }
            if (GUILayout.Button("Clear All", BDArmorySetup.BDGuiSkin.button))
            {
                foreach (var team in customSpawnConfig.customVesselSpawnConfigs)
                    foreach (var member in team)
                        member.craftURL = null;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUIUtils.RepositionWindow(ref vesselSelectionWindowRect);
            GUIUtils.UpdateGUIRect(vesselSelectionWindowRect, _vesselGUICheckIndex);
            GUIUtils.UseMouseEventInRect(vesselSelectionWindowRect);
            GUI.BringWindowToFront(windowID);
        }

        void OnSelected(string fullPath, CraftBrowserDialog.LoadType loadType)
        {
            currentVesselSpawnConfig.craftURL = fullPath;
            HideVesselSelection();
        }
        void OnCancelled()
        { HideVesselSelection(); }
        internal class CustomCraftBrowserDialog
        {
            public EditorFacility facility = EditorFacility.SPH;
            string profile = HighLogic.SaveFolder;
            string craftFolder;
            public Dictionary<string, CraftProfileInfo> craftList = new Dictionary<string, CraftProfileInfo>();
            public GUIStyle vesselButtonStyle = new GUIStyle(BDArmorySetup.BDGuiSkin.button);
            public GUIStyle vesselInfoStyle = new GUIStyle(BDArmorySetup.BDGuiSkin.label);
            public void UpdateList(EditorFacility facility)
            {
                this.facility = facility;
                craftFolder = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "saves", profile, "Ships", facility.ToString()));
                craftList = Directory.GetFiles(craftFolder, "*.craft").ToDictionary(craft => craft, craft => new CraftProfileInfo());
                if (craftList.ContainsKey(Path.Combine(craftFolder, "Auto-Saved Ship.craft"))) craftList.Remove(Path.Combine(craftFolder, "Auto-Saved Ship.craft")); // Ignore the Auto-Saved Ship.
                CraftProfileInfo.PrepareCraftMetaFileLoad();
                foreach (var craft in craftList.Keys.ToList())
                {
                    var craftMeta = Path.Combine(craftFolder, $"{Path.GetFileNameWithoutExtension(craft)}.loadmeta");
                    if (File.Exists(craftMeta)) // If the loadMeta file exists, use it, otherwise generate one.
                    {
                        craftList[craft].LoadFromMetaFile(craftMeta);
                    }
                    else
                    {
                        var craftNode = ConfigNode.Load(craft);
                        craftList[craft].LoadDetailsFromCraftFile(craftNode, craft);
                        craftList[craft].SaveToMetaFile(craftMeta);
                    }
                }
                vesselButtonStyle.stretchHeight = true;
                vesselButtonStyle.fontSize = 20;
                vesselInfoStyle.fontSize = 12;
            }
        }

        #endregion

        #region Crew Selection
        internal static int _crewGUICheckIndex = -1;
        bool showCrewSelection = false;
        Rect crewSelectionWindowRect = new Rect(0, 0, 300, 400);
        Vector2 crewSelectionScrollPos = default;
        HashSet<string> SelectedCrewMembers = new HashSet<string>();

        /// <summary>
        /// Show the crew selection window.
        /// </summary>
        /// <param name="position">Position of the mouse click.</param>
        /// <param name="vesselSpawnConfig">The VesselSpawnConfig clicked on.</param>
        public void ShowCrewSelection(Vector2 position, CustomVesselSpawnConfig vesselSpawnConfig)
        {
            if (showVesselSelection) HideVesselSelection();
            if (showCrewSelection && vesselSpawnConfig == currentVesselSpawnConfig)
            {
                HideCrewSelection();
                return;
            }
            currentVesselSpawnConfig = vesselSpawnConfig;
            crewSelectionWindowRect.position = position + new Vector2(50, -crewSelectionWindowRect.height / 2); // Centred and slightly offset to allow clicking the same spot.
            showCrewSelection = true;
            GUIUtils.SetGUIRectVisible(_crewGUICheckIndex, true);
            foreach (var crew in HighLogic.CurrentGame.CrewRoster.Kerbals(ProtoCrewMember.KerbalType.Crew)) // Set any non-assigned crew as available.
            {
                if (crew.rosterStatus != ProtoCrewMember.RosterStatus.Assigned)
                    crew.rosterStatus = ProtoCrewMember.RosterStatus.Available;
            }
        }

        /// <summary>
        /// Hide the crew selection window.
        /// </summary>
        public void HideCrewSelection(CustomVesselSpawnConfig vesselSpawnConfig = null)
        {
            if (vesselSpawnConfig != null)
            {
                SelectedCrewMembers.Remove(vesselSpawnConfig.kerbalName);
                vesselSpawnConfig.kerbalName = null;
            }
            showCrewSelection = false;
            currentVesselSpawnConfig = null;
            GUIUtils.SetGUIRectVisible(_crewGUICheckIndex, false);
        }

        /// <summary>
        /// Crew selection window borrowed from VesselMover and modified.
        /// </summary>
        /// <param name="windowID"></param>
        public void CrewSelectionWindow(int windowID)
        {
            KerbalRoster kerbalRoster = HighLogic.CurrentGame.CrewRoster;
            GUI.DragWindow(new Rect(0, 0, crewSelectionWindowRect.width - 20, 20));
            if (GUI.Button(new Rect(crewSelectionWindowRect.width - 22, 5, 18, 18), "X", BDArmorySetup.BDGuiSkin.button))
            {
                HideCrewSelection();
                return;
            }
            GUILayout.BeginVertical();
            crewSelectionScrollPos = GUILayout.BeginScrollView(crewSelectionScrollPos, GUI.skin.box, GUILayout.Width(crewSelectionWindowRect.width - 20), GUILayout.MaxHeight(crewSelectionWindowRect.height - 60));
            using (var kerbals = kerbalRoster.Kerbals(ProtoCrewMember.KerbalType.Crew).GetEnumerator())
                while (kerbals.MoveNext())
                {
                    ProtoCrewMember crewMember = kerbals.Current;
                    if (crewMember == null || SelectedCrewMembers.Contains(crewMember.name)) continue;
                    if (GUILayout.Button($"{crewMember.name}, {crewMember.gender}, {crewMember.trait}", BDArmorySetup.BDGuiSkin.button))
                    {
                        SelectedCrewMembers.Remove(currentVesselSpawnConfig.kerbalName);
                        SelectedCrewMembers.Add(crewMember.name);
                        currentVesselSpawnConfig.kerbalName = crewMember.name;
                        HideCrewSelection();
                    }
                }
            GUILayout.EndScrollView();
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear", BDArmorySetup.BDGuiSkin.button))
            {
                SelectedCrewMembers.Remove(currentVesselSpawnConfig.kerbalName);
                currentVesselSpawnConfig.kerbalName = null;
                HideCrewSelection();
            }
            if (GUILayout.Button("Clear All", BDArmorySetup.BDGuiSkin.button))
            {
                SelectedCrewMembers.Clear();
                foreach (var team in customSpawnConfig.customVesselSpawnConfigs)
                    foreach (var member in team)
                        member.kerbalName = null;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUIUtils.RepositionWindow(ref crewSelectionWindowRect);
            GUIUtils.UpdateGUIRect(crewSelectionWindowRect, _crewGUICheckIndex);
            GUIUtils.UseMouseEventInRect(crewSelectionWindowRect);
            GUI.BringWindowToFront(windowID);
        }
        #endregion

        #endregion
    }

}
