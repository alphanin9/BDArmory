﻿using System.Linq;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Settings;
using BDArmory.Utils;

namespace BDArmory.GameModes
{
    public class ModuleSpaceFriction : PartModule
    {
        /// <summary>
        /// Adds friction/drag to craft in null-atmo porportional to AI MaxSpeed setting to ensure craft does not exceed said speed
        /// Adds counter-gravity to prevent null-atmo ships from falling to the ground from gravity in the absence of wings and lift
        /// Provides additional friction/drag during corners to help spacecraft drift through turns instead of being stuck with straight-up joust charges
        /// TL;DR, provides the means for SciFi style space dogfights
        /// </summary>

        private double frictionCoeff = 1.0f; //how much force is applied to decellerate craft

        //[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Space Friction"), UI_Toggle(disabledText = "Disabled", enabledText = "Enabled", scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        //public bool FrictionEnabled = false; //global value

        //[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "CounterGrav"), UI_Toggle(disabledText = "Disabled", enabledText = "Enabled", scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        //public bool AntiGravEnabled = false; //global value

        [KSPField(isPersistant = true)]
        public bool AntiGravOverride = false; //per craft override to be set in the .craft file, for things like zeppelin battles where attacking planes shouldn't be under countergrav

        public float maxVelocity = 300; //MaxSpeed setting in PilotAI

        public float frictMult; //engine thrust of craft

        float vesselAlt = 25;
        //public float driftMult = 2; //additional drag multipler for cornering/decellerating so things don't take the same amount of time to decelerate as they do to accelerate
        float landedTime = 0;

        int repulsors = 1;
        public static bool GameIsPaused
        {
            get { return PauseMenu.isOpen || Time.timeScale == 0; }
        }
        BDModulePilotAI AI;
        public BDModulePilotAI pilot
        {
            get
            {
                if (AI) return AI;
                AI = VesselModuleRegistry.GetBDModulePilotAI(vessel, true); // FIXME should this be IBDAIControl?
                return AI;
            }
        }
        BDModuleSurfaceAI SAI;
        public BDModuleSurfaceAI driver
        {
            get
            {
                if (SAI) return SAI;
                SAI = VesselModuleRegistry.GetBDModuleSurfaceAI(vessel, true);
                return SAI;
            }
        }

        BDModuleVTOLAI VAI;
        public BDModuleVTOLAI flier
        {
            get
            {
                if (VAI) return VAI;
                VAI = VesselModuleRegistry.GetModule<BDModuleVTOLAI>(vessel);

                return VAI;
            }
        }
        ModuleEngines Engine;
        public ModuleEngines foundEngine
        {
            get
            {
                if (Engine) return Engine;
                Engine = VesselModuleRegistry.GetModuleEngines(vessel).FirstOrDefault();
                return Engine;
            }
        }
        MissileFire MF;
        public MissileFire weaponManager
        {
            get
            {
                if (MF) return MF;
                MF = VesselModuleRegistry.GetMissileFire(vessel, true);
                return MF;
            }
        }
        void Start()
        {
            if (HighLogic.LoadedSceneIsFlight)
            {
                using (var engine = VesselModuleRegistry.GetModules<ModuleEngines>(vessel).GetEnumerator())
                    while (engine.MoveNext())
                    {
                        if (engine.Current == null) continue;
                        if (engine.Current.independentThrottle) continue; //only grab primary thrust engines
                        frictMult += (engine.Current.maxThrust * (engine.Current.thrustPercentage / 100));
                        //have this called onvesselModified?
                    }
                frictMult /= 4; //doesn't need to be 100% of thrust at max speed, Ai will already self-limit; this also has the AI throttle down, which allows for slamming the throttle full for braking/coming about, instead of being stuck with lower TwR
                repulsors = VesselModuleRegistry.GetRepulsorModules(vessel).Count;
            }
        }

        public void FixedUpdate()
        {
            if ((!BDArmorySettings.SPACE_HACKS && !AntiGravOverride) || !HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || this.vessel.packed || GameIsPaused) return;

            if (this.part.vessel.situation == Vessel.Situations.FLYING || this.part.vessel.situation == Vessel.Situations.SUB_ORBITAL)
            {
                if (BDArmorySettings.SF_FRICTION)
                {
                    if (this.part.vessel.speed > 10)
                    {
                        if (AI != null)
                        {
                            maxVelocity = AI.maxSpeed;
                        }
                        else if (SAI != null)
                        {
                            maxVelocity = SAI.MaxSpeed;
                        }
                        else if (VAI != null)
                            maxVelocity = VAI.MaxSpeed;

                        var speedFraction = (float)part.vessel.speed / maxVelocity;
                        frictionCoeff = speedFraction * speedFraction * speedFraction * frictMult; //at maxSpeed, have friction be 100% of vessel's engines thrust

                        frictionCoeff *= (1 + (Vector3.Angle(this.part.vessel.srf_vel_direction, this.part.vessel.GetTransform().up) / 180) * BDArmorySettings.SF_DRAGMULT * 4); //greater AoA off prograde, greater drag

                        part.vessel.rootPart.rb.AddForceAtPosition((-part.vessel.srf_vel_direction * frictionCoeff), part.vessel.CoM, ForceMode.Acceleration);
                    }
                }
                if (BDArmorySettings.SF_GRAVITY || AntiGravOverride) //have this disabled if no engines left?
                {
                    if (weaponManager != null && foundEngine != null) //have engineless craft fall
                    {
                        for (int i = 0; i < part.vessel.Parts.Count; i++)
                        {
                            if (part.vessel.parts[i].PhysicsSignificance != 1) //attempting to apply rigidbody force to non-significant parts will NRE
                            {
                                part.vessel.Parts[i].Rigidbody.AddForce(-FlightGlobals.getGeeForceAtPosition(part.vessel.Parts[i].transform.position), ForceMode.Acceleration);
                            }
                        }
                    }
                }
            }
            if (this.part.vessel.situation != Vessel.Situations.ORBITING || this.part.vessel.situation != Vessel.Situations.DOCKED || this.part.vessel.situation != Vessel.Situations.ESCAPING || this.part.vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                if (BDArmorySettings.SF_REPULSOR || AntiGravOverride)
                {
                    if ((pilot != null || driver != null || flier != null) && foundEngine != null)
                    {
                        vesselAlt = 10;
                        if (AI != null)
                        {
                            vesselAlt = AI.minAltitude;
                        }
                        else if (SAI != null)
                        {
                            vesselAlt = SAI.MaxSlopeAngle * 2;
                        }
                        else if (VAI != null)
                            vesselAlt = VAI.minAltitude;

                        float accelMult = 1f;
                        if (vessel.radarAltitude < vesselAlt) 
                        {
                            if (vessel.radarAltitude < Mathf.Max((vesselAlt / 10), 5))
                            {
                                if ((vessel.situation != Vessel.Situations.LANDED && vessel.situation != Vessel.Situations.SPLASHED && vessel.situation != Vessel.Situations.PRELAUNCH) && Time.time - 5 > landedTime)
                                {
                                    accelMult = Mathf.Clamp(Mathf.Abs((float)vessel.verticalSpeed), 1f, 100);
                                }
                                else
                                {
                                    accelMult = 10;
                                    landedTime = Time.time;
                                }
                            }

                            var repulsorModules = VesselModuleRegistry.GetRepulsorModules(vessel);
                            using (var craftPart = repulsorModules.GetEnumerator())
                                while (craftPart.MoveNext())
                                {
                                    if (craftPart.Current is null) continue;
                                    if (craftPart.Current.part.PhysicsSignificance != 1) //attempting to apply rigidbody force to non-significant parts will NRE
                                    {
                                        craftPart.Current.part.Rigidbody.AddForce(((-FlightGlobals.getGeeForceAtPosition(craftPart.Current.part.transform.position) * ((part.vessel.GetTotalMass() * 10) / repulsors)) * ((vesselAlt / Mathf.Max(BodyUtils.GetRadarAltitudeAtPos(craftPart.Current.part.transform.position), 1))) / accelMult), ForceMode.Acceleration);
                                    }
                                }
                            /*else
                            {
                                for (int i = 0; i < part.vessel.Parts.Count; i++)
                                {
                                    if (part.vessel.parts[i].PhysicsSignificance != 1) //attempting to apply rigidbody force to non-significant parts will NRE
                                    {
                                        part.vessel.Parts[i].Rigidbody.AddForce((-FlightGlobals.getGeeForceAtPosition(part.vessel.Parts[i].transform.position) * ((vesselAlt / Mathf.Max((float)part.vessel.radarAltitude, 1))) / accelMult), ForceMode.Acceleration);
                                    }
                                }
                            }*/
                        }
                    }
                }
            }
        }

        public static void AddSpaceFrictionToAllValidVessels()
        {
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (VesselModuleRegistry.GetMissileFire(vessel, true) != null && vessel.rootPart.FindModuleImplementing<ModuleSpaceFriction>() == null)
                {
                    vessel.rootPart.AddModule("ModuleSpaceFriction");
                }
            }
        }
    }
}
