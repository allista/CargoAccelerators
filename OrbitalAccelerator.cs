using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AT_Utils;
using CargoAccelerators.UI;
using KSP.Localization;
using UnityEngine;

namespace CargoAccelerators
{
    public class OrbitalAccelerator : PartModule, IPartMassModifier, IPartCostModifier, ITargetable
    {
        private static Globals GLB => Globals.Instance;

        public enum AcceleratorState
        {
            IDLE,
            LOADED,
            ACQUIRE_PAYLOAD,
            EJECT,
            LAUNCH,
            ABORT
        }

        [KSPField] public string LoadingDamperID = "LoadingDamper";
        [KSPField] public string LaunchingDamperID = "LaunchingDamper";

        [KSPField] public string BarrelAttachmentTransform = "BarrelAttachment";
        [KSPField] public string SegmentTransform = "BarrelSegment";
        [KSPField] public string SegmentSensorTransform = "BarrelSegmentSensor";
        [KSPField] public string NextSegmentTransform = "NextSegment";

        [KSPField] public int MaxSegments = 20;
        [KSPField] public float SegmentMass;
        [KSPField] public float SegmentCost;
        [KSPField] public Vector3 SegmentCoM;

        [KSPField(isPersistant = true,
            guiActive = true,
            guiActiveEditor = true,
            guiName = "Segments")]
        [UI_FloatRange(scene = UI_Scene.All, minValue = 0, maxValue = 20, stepIncrement = 1)]
        public float numSegments;

        [KSPField(isPersistant = true)] public AcceleratorState State = AcceleratorState.IDLE;
        [KSPField(isPersistant = true)] public bool AutoAlignEnabled;

        [KSPField(isPersistant = true,
            guiName = "Accelerator Controls",
            guiActive = true,
            guiActiveEditor = true,
            guiActiveUnfocused = true,
            unfocusedRange = 50)]
        [UI_Toggle(scene = UI_Scene.Flight, enabledText = "Enabled", disabledText = "Disabled")]
        public bool ShowUI;

        private AcceleratorWindow UI;

#if DEBUG
        [KSPField(guiActive = true,
            guiActiveEditor = true,
            guiName = "Vessel Mass",
            guiUnits = "t",
            guiFormat = "F1")]
        public float VesselMass;
#endif

        private AxisAttitudeController axisController;
        public ATMagneticDamper loadingDamper;
        public ExtensibleMagneticDamper launchingDamper;
        public GameObject barrelSegmentPrefab;
        private float vesselSize;
        private float launchingAttractorOrigPower;

        private struct BarrelSegment
        {
            public GameObject segmentGO;
            public Transform segmentSensor;
        }

        private readonly List<BarrelSegment> barrelSegments = new List<BarrelSegment>();

        public override void OnAwake()
        {
            base.OnAwake();
            var T = part.FindModelTransform(SegmentTransform);
            if(T != null)
                T.gameObject.SetActive(false);
            GameEvents.onVesselWasModified.Add(onVesselWasModified);
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
#if DEBUG
            this.Log(
                $"prefab model tree: {DebugUtils.formatTransformTree(part.partInfo.partPrefab.transform)}");
#endif
            var T = part.partInfo.partPrefab.FindModelTransform(SegmentTransform);
            if(T == null)
            {
                this.Log($"Unable to find {SegmentTransform} model transform");
                this.EnableModule(false);
                return;
            }
            barrelSegmentPrefab = T.gameObject;
            loadingDamper = ATMagneticDamper.GetDamper(part, LoadingDamperID);
            if(loadingDamper == null)
            {
                this.Log($"Unable to find loading damper with ID {LoadingDamperID}");
                this.EnableModule(false);
                return;
            }
            launchingDamper =
                ATMagneticDamper.GetDamper(part, LaunchingDamperID) as ExtensibleMagneticDamper;
            if(launchingDamper == null)
            {
                this.Log($"Unable to find launching damper with ID {LoadingDamperID}");
                this.EnableModule(false);
                return;
            }
            launchingAttractorOrigPower = launchingDamper.AttractorPower;
            if(!updateSegments())
            {
                this.EnableModule(false);
                return;
            }
            var numSegmentsField = Fields[nameof(numSegments)];
            numSegmentsField.OnValueModified += onNumSegmentsChange;
            if(numSegmentsField.uiControlEditor is UI_FloatRange numSegmentsControlEditor)
                numSegmentsControlEditor.maxValue = MaxSegments;
            if(numSegmentsField.uiControlFlight is UI_FloatRange numSegmentsControlFlight)
                numSegmentsControlFlight.maxValue = MaxSegments;
            Fields[nameof(ShowUI)].OnValueModified += showUI;
            axisController = new AxisAttitudeController(this);
            UI = new AcceleratorWindow(this);
            if(ShowUI)
                UI.Show(this);
        }

        private void OnDestroy()
        {
            if(vessel != null
               && FlightGlobals.fetch != null
               && ReferenceEquals(FlightGlobals.fetch.VesselTarget, this))
            {
                vessel.StartCoroutine(CallbackUtil.DelayedCallback(1,
                    FlightGlobals.fetch.SetVesselTarget,
                    vessel,
                    false));
            }
            Fields[nameof(numSegments)].OnValueModified -= onNumSegmentsChange;
            Fields[nameof(ShowUI)].OnValueModified -= showUI;
            GameEvents.onVesselWasModified.Remove(onVesselWasModified);
            axisController?.Disconnect();
            axisController = null;
            UI?.Close();
        }

        private void onVesselWasModified(Vessel vsl)
        {
            if(axisController == null || vsl != vessel || vsl == null)
                return;
            axisController.UpdateTorqueProviders();
        }

        #region UI
        private void showUI(object value)
        {
            if(ShowUI)
                UI.Show(this);
            else
                UI.Close();
        }

        public void AcquirePayload()
        {
            if(State != AcceleratorState.LOADED)
                return;
            UI.ClearMessages();
            changeState(AcceleratorState.ACQUIRE_PAYLOAD);
        }

        public void EjectPayload()
        {
            if(State != AcceleratorState.LOADED)
                return;
            UI.ClearMessages();
            changeState(AcceleratorState.EJECT);
        }

        public void AbortOperations()
        {
            switch(State)
            {
                case AcceleratorState.EJECT:
                    UI.ClearMessages();
                    changeState(AcceleratorState.IDLE);
                    break;
                case AcceleratorState.LAUNCH:
                    UI.ClearMessages();
                    changeState(AcceleratorState.ABORT);
                    break;
            }
        }

        public void ToggleAutoAlign(bool enable)
        {
            AutoAlignEnabled = enable;
            axisController.Reset();
        }

        public void LaunchPayload()
        {
            if(State != AcceleratorState.LOADED
               || launchParams == null
               || !launchParams.Valid)
                return;
            UI.ClearMessages();
            changeState(AcceleratorState.LAUNCH);
        }

#if DEBUG
        [KSPEvent(active = true, guiActive = true, guiName = "Reload CA Globals")]
        public void ReloadGlobals()
        {
            Globals.Load();
            axisController.InitPIDs();
        }
#endif
        #endregion

        #region State
        private void changeState(AcceleratorState newState)
        {
            State = newState;
            UI.UpdateState();
        }

        private void Update()
        {
            if(!HighLogic.LoadedSceneIsFlight || FlightDriver.Pause)
                return;
            loadingDamper.AutoEnable = true;
            loadingDamper.AttractorEnabled = true;
            loadingDamper.InvertAttractor = false;
            launchingDamper.AttractorEnabled = true;
            launchingDamper.InvertAttractor = true;
            switch(State)
            {
                case AcceleratorState.IDLE:
                    if(launchParams != null)
                    {
                        launchParams.Cleanup();
                        launchParams = null;
                        UI.UpdatePayloadInfo();
                    }
                    if(launchingDamper.DamperEnabled)
                        launchingDamper.EnableDamper(false);
                    if(loadingDamper.VesselsInside.Count > 0)
                        changeState(AcceleratorState.LOADED);
                    break;
                case AcceleratorState.LOADED:
                    if(loadingDamper.VesselsInside.Count == 0)
                        changeState(AcceleratorState.IDLE);
                    else if(launchParams == null)
                        changeState(AcceleratorState.ACQUIRE_PAYLOAD);
                    break;
                case AcceleratorState.ACQUIRE_PAYLOAD:
                    acquirePayload();
                    UI.UpdatePayloadInfo();
                    changeState(launchParams != null
                        ? AcceleratorState.LOADED
                        : AcceleratorState.IDLE);
                    break;
                case AcceleratorState.EJECT:
                    if(loadingDamper.VesselsInside.Count == 0)
                    {
                        changeState(AcceleratorState.IDLE);
                        break;
                    }
                    loadingDamper.InvertAttractor = true;
                    break;
                case AcceleratorState.LAUNCH:
                    loadingDamper.AutoEnable = true;
                    if(launchCoro == null)
                        launchCoro = StartCoroutine(launchPayload());
                    break;
                case AcceleratorState.ABORT:
                    if(launchCoro != null)
                    {
                        abortLaunch();
                        if(State != AcceleratorState.ABORT)
                            break;
                        if(!launchingDamper.DamperEnabled)
                        {
                            changeState(AcceleratorState.IDLE);
                            break;
                        }
                        launchingDamper.AttractorPower = (float)launchParams.acceleration;
                    }
                    if(launchingDamper.VesselsInside.Count == 0)
                        changeState(AcceleratorState.IDLE);
                    launchingDamper.InvertAttractor = false;
                    break;
            }
        }

        private void FixedUpdate()
        {
            if(!HighLogic.LoadedSceneIsFlight || FlightDriver.Pause)
                return;
            switch(State)
            {
                case AcceleratorState.ABORT:
                    var payloadRelV = (launchParams.payload.orbit.vel - vessel.orbit.vel).xzy;
                    if(Vector3d.Dot(payloadRelV, launchingDamper.attractorAxisW) < 0.1)
                    {
                        launchingDamper.EnableDamper(false);
                        changeState(AcceleratorState.IDLE);
                    }
                    break;
            }
        }

        private void LateUpdate()
        {
            if(!HighLogic.LoadedSceneIsFlight || FlightDriver.Pause || !UI.IsShown)
                return;
            if(launchParams == null || !launchParams.Valid)
                return;
            var referenceTransformRotation = vessel.ReferenceTransform.rotation;
            // update attitude error
            if(!AutoAlignEnabled || axisController.HasUserInput)
                axisController.UpdateAttitudeError();
            UI.Controller.UpdateAttitudeError(axisController.AttitudeError.x,
                axisController.AttitudeError.y,
                axisController.AttitudeError.z,
                axisController.Aligned);
            // update countdown
            UI.Controller.UpdateCountdown(launchParams.launchUT
                                          - Planetarium.GetUniversalTime());
            // update payload checks
            if(part.packed)
                return;
            var relV = (launchParams.payload.obt_velocity
                        - vessel.obt_velocity).sqrMagnitude;
            var payloadAV = launchParams.payload.angularVelocity.sqrMagnitude;
            var dist = (launchParams.payload.CurrentCoM
                        - loadingDamper.attractor.position).magnitude;
            UI.Controller.UpdatePayloadInfo((float)Math.Sqrt(relV),
                Mathf.Sqrt(payloadAV) * Mathf.Rad2Deg,
                dist,
                relV < GLB.MAX_RELATIVE_VELOCITY_SQR,
                payloadAV < GLB.MAX_ANGULAR_VELOCITY_SQR,
                dist < GLB.MAX_DISPLACEMENT);
        }
        #endregion

        #region Launch
        public class LaunchParams
        {
            private readonly OrbitalAccelerator accelerator;
            public Vessel payload;
            public string payloadTitle;
            private VesselRanges payloadRanges;

            private ManeuverNode node;
            public double nodeDeltaVm;
            public double launchUT;
            public double rawDuration;
            public double duration;
            public double energy;
            public double acceleration;
            public double maxAccelerationTime;
            public double maxDeltaV;
            public bool maneuverValid;
            public bool maneuverStarted;

            public bool Valid =>
                maneuverValid
                && payload != null
                && node?.nextPatch != null;

            public LaunchParams(OrbitalAccelerator accelerator) => this.accelerator = accelerator;

            public void SetPayloadUnpackDistance(float distance)
            {
                payloadRanges = payload.SetUnpackDistance(distance);
            }

            public void Cleanup()
            {
                if(payloadRanges == null || payload == null)
                    return;
                payload.vesselRanges = payloadRanges;
            }

            public Vector3d GetManeuverVector() =>
                (node.nextPatch.getOrbitalVelocityAtUT(node.UT)
                 - payload.orbit.getOrbitalVelocityAtUT(node.UT)).xzy;

            public bool AcquirePayload(uint vesselId)
            {
                FlightGlobals.FindVessel(vesselId, out payload);
                if(payload == null)
                {
                    accelerator.UI.AddMessage("Unable to find payload.");
                    return false;
                }
                payloadTitle = Localizer.Format(payload.vesselName);
                return UpdatePayloadNode();
            }

            public bool UpdatePayloadNode()
            {
                maneuverValid = false;
                if(payload.patchedConicSolver != null
                   && payload.patchedConicSolver.maneuverNodes.Count > 0)
                {
                    var payloadNode = payload.patchedConicSolver.maneuverNodes[0];
                    node = new ManeuverNode
                    {
                        UT = payloadNode.UT,
                        DeltaV = payloadNode.DeltaV,
                        patch = payload.orbit,
                        nextPatch = new Orbit(payloadNode.nextPatch)
                    };
                }
                else if(payload.flightPlanNode.CountNodes > 0)
                {
                    node = new ManeuverNode();
                    node.Load(payload.flightPlanNode.nodes[0]);
                    var hostNode = accelerator.vessel.patchedConicSolver.AddManeuverNode(node.UT);
                    hostNode.DeltaV = node.DeltaV;
                    accelerator.vessel.patchedConicSolver.UpdateFlightPlan();
                    node.patch = payload.orbit;
                    node.nextPatch = hostNode.nextPatch;
                    hostNode.RemoveSelf();
                }
                else
                {
                    accelerator.UI.AddMessage("Payload doesn't have a maneuver node.");
                    return false;
                }
                nodeDeltaVm = node.DeltaV.magnitude;
                return true;
            }

            public void CalculateLaunchTiming()
            {
                // duration of the maneuver without acceleration tuning
                rawDuration = nodeDeltaVm / acceleration;
                var middleDeltaV = nodeDeltaVm / 2;
                // duration of the maneuver with acceleration tuning
                var fullAccelerationTime =
                    (int)Math.Ceiling(Math.Max(nodeDeltaVm
                                               / acceleration
                                               / TimeWarp.fixedDeltaTime
                                               - GLB.FINE_TUNE_FRAMES,
                        0))
                    * TimeWarp.fixedDeltaTime;
                var remainingDeltaV = nodeDeltaVm - acceleration * fullAccelerationTime;
                var middleDuration = remainingDeltaV < middleDeltaV
                    ? rawDuration / 2
                    : fullAccelerationTime;
                duration = fullAccelerationTime;
                while(remainingDeltaV > GLB.MANEUVER_DELTA_V_TOL)
                {
                    var a = remainingDeltaV / TimeWarp.fixedDeltaTime / (GLB.FINE_TUNE_FRAMES + 1);
                    remainingDeltaV -= a * TimeWarp.fixedDeltaTime;
                    if(remainingDeltaV > middleDeltaV)
                        middleDuration += TimeWarp.fixedDeltaTime;
                    duration += TimeWarp.fixedDeltaTime;
                }
                // calculate launch start UT
                launchUT = node.UT - middleDuration;
            }

            public override string ToString()
            {
                return $@"launchParams for payload: {payload.GetID()}
nodeDeltaV: {node.DeltaV}, |{nodeDeltaVm}|
acceleration: {acceleration}
rawDuration: {rawDuration}
duration: {duration}
energy: {energy}";
            }
        }

        public LaunchParams launchParams { get; private set; }
        private Coroutine launchCoro;
        private Orbit preLaunchOrbit;

        private bool selfCheck()
        {
            if(vessel.isActiveVessel && !vessel.PatchedConicsAttached)
            {
                UI.AddMessage("Patched conics are not available. Upgrade Tracking Station.");
                return false;
            }
            if(vessel.angularVelocity.sqrMagnitude > GLB.MAX_ANGULAR_VELOCITY_SQR)
            {
                UI.AddMessage("The accelerator is rotating. Stop the rotation and try again.");
                return false;
            }
            return true;
        }

        private void clearManeuverNodes()
        {
            vessel.flightPlanNode.ClearData();
            if(vessel.patchedConicSolver == null)
                return;
            var nodes = vessel.patchedConicSolver.maneuverNodes;
            for(var i = nodes.Count - 1; i >= 0; i--)
                nodes[i].RemoveSelf();
        }

        private bool acquirePayload()
        {
            UI.ClearMessages();
            var numberOfVessels = launchingDamper.VesselsInside.Count;
            if(numberOfVessels != 1)
            {
                UI.AddMessage(numberOfVessels == 0
                    ? "No payload in acceleration area."
                    : "Multiple vessels in acceleration area.");
                return false;
            }
            clearManeuverNodes();
            launchParams = new LaunchParams(this);
            if(!launchParams.AcquirePayload(launchingDamper.VesselsInside.First()))
                return false;
            if(!checkPayloadManeuver())
                return false;
            return true;
        }

        private float calculateLaunchDistance()
        {
            var vslBounds = launchParams.payload.Bounds();
            var launchPath = Vector3.Project(vslBounds.min,
                launchingDamper.attractorAxisW);
            Transform endT;
            if(barrelSegments.Count == 0)
                endT = part.FindModelTransform(BarrelAttachmentTransform);
            else
            {
                var lastSegment = barrelSegments.Last().segmentGO.transform;
                endT = Part.FindHeirarchyTransform(lastSegment, NextSegmentTransform);
            }
            if(endT != null)
                launchPath -= endT.position;
            else
            {
                UI.AddMessage(
                    $"Unable to calculate launch path. No {BarrelAttachmentTransform} found.");
                return -1;
            }
            return launchPath.magnitude;
        }

        private bool checkPayloadManeuver()
        {
            // limit maximum acceleration by GTolerance
            launchParams.acceleration = maxLaunchAcceleration(launchParams.payload);
            if(launchParams.acceleration > launchingAttractorOrigPower)
                launchParams.acceleration = launchingAttractorOrigPower;
            // calculate the force that the accelerator will apply
            // and the energy required
            var launchDistance = calculateLaunchDistance();
            var hostMass = vessel.GetTotalMass();
            var payloadMass = launchParams.payload.GetTotalMass();
            var force = launchingDamper.GetForceForAcceleration(payloadMass,
                (float)launchParams.acceleration,
                out var energyCurrent);
            launchParams.maxAccelerationTime =
                Math.Sqrt(2 * launchDistance / force / (1 / hostMass + 1 / payloadMass));
            // calculate acceleration and max dV projections
            launchParams.acceleration = force / payloadMass;
            launchParams.maxDeltaV = launchParams.acceleration * launchParams.maxAccelerationTime;
            // compare projected max values to the required by the maneuver node
            if(launchParams.nodeDeltaVm > launchParams.maxDeltaV)
            {
                var dVShortage = launchParams.nodeDeltaVm - launchParams.maxDeltaV;
                UI.AddMessage(
                    $"This accelerator is too short for the planned maneuver of the \"{launchParams.payloadTitle}\".\nMaximum possible dV is {dVShortage:F1} m/s less then required.");
                return false;
            }
            // check if launch duration is within accelerator limits
            launchParams.CalculateLaunchTiming();
            if(launchParams.duration > launchParams.maxAccelerationTime)
            {
                var timeShortage = launchParams.duration - launchParams.maxAccelerationTime;
                UI.AddMessage(
                    $"This accelerator is too short for the planned maneuver of the \"{launchParams.payloadTitle}\".\nMaximum possible acceleration time is {timeShortage:F1} s less then required.");
                return false;
            }
            // check if there's enough energy
            launchParams.energy = launchParams.rawDuration * energyCurrent;
            vessel.GetConnectedResourceTotals(Utils.ElectricCharge.id, out var amountEC, out _);
            if(amountEC < launchParams.energy)
            {
                UI.AddMessage(
                    $"Not enough energy for the maneuver. Additional {launchParams.energy - amountEC} EC is required.");
                return false;
            }
            launchParams.maneuverValid = true;
            return true;
        }

        private static double maxLaunchAcceleration(Vessel vsl)
        {
            var minAccelerationTolerance = double.MaxValue;
            foreach(var vslPart in vsl.Parts)
            {
                if(vslPart.gTolerance < minAccelerationTolerance)
                    minAccelerationTolerance = vslPart.gTolerance;
            }
            if(HighLogic.CurrentGame.Parameters.CustomParams<GameParameters.AdvancedParams>()
                .GKerbalLimits)
            {
                foreach(var crewMember in vsl.GetVesselCrew())
                {
                    var crewGTolerance = ProtoCrewMember.GToleranceMult(crewMember)
                                         * HighLogic.CurrentGame.Parameters
                                             .CustomParams<GameParameters.AdvancedParams>()
                                             .KerbalGToleranceMult;
                    if(crewGTolerance < minAccelerationTolerance)
                        minAccelerationTolerance = crewGTolerance;
                }
            }
            return minAccelerationTolerance * Utils.G0 * 0.98;
        }

        private bool preLaunchCheck()
        {
            UI.ClearMessages();
            if(!launchParams.Valid)
            {
                UI.AddMessage("Payload lost.");
                return false;
            }
            if(!launchParams.UpdatePayloadNode() || !checkPayloadManeuver())
                return false;
            var nodeBurnVector = launchParams.GetManeuverVector();
            var attitudeError =
                Utils.Angle2((Vector3)nodeBurnVector, launchingDamper.attractorAxisW);
            if(attitudeError > GLB.MAX_ATTITUDE_ERROR)
            {
                UI.AddMessage("Accelerator is not aligned with the maneuver node.");
                return false;
            }
            if(launchParams.payload.angularVelocity.sqrMagnitude > GLB.MAX_ANGULAR_VELOCITY_SQR)
            {
                UI.AddMessage("Payload is rotating. Stop the rotation and try again.");
                return false;
            }
            var relVel = launchParams.payload.obt_velocity - vessel.obt_velocity;
            if(relVel.sqrMagnitude > GLB.MAX_RELATIVE_VELOCITY_SQR)
            {
                UI.AddMessage("Payload is moving. Wait for it to stop and try again.");
                return false;
            }
            if((launchParams.payload.CurrentCoM
                - loadingDamper.attractor.position).magnitude
               > GLB.MAX_DISPLACEMENT)
            {
                UI.AddMessage(
                    "Payload is not at the launch position yet. Wait for it to settle and try again.");
                return false;
            }
            return true;
        }

        private bool maneuverIsComplete()
        {
            var nodeBurnVector = launchParams.GetManeuverVector();
            var nodeDotAxis = Vector3d.Dot(nodeBurnVector, launchingDamper.attractorAxisW);
            if(nodeDotAxis < GLB.MANEUVER_DELTA_V_TOL)
                return true;
            if(nodeDotAxis / launchParams.acceleration
               < TimeWarp.fixedDeltaTime * GLB.FINE_TUNE_FRAMES)
            {
                launchingDamper.AttractorPower =
                    (float)nodeDotAxis / TimeWarp.fixedDeltaTime / (GLB.FINE_TUNE_FRAMES + 1);
            }
            return false;
        }

        private IEnumerator<YieldInstruction> waitAndReCheck(int secondsBeforeLaunch)
        {
            var timeLeft = launchParams.launchUT - Planetarium.GetUniversalTime();
            if(timeLeft > secondsBeforeLaunch + 10)
            {
                var warpToTime = launchParams.launchUT - secondsBeforeLaunch;
                TimeWarp.fetch.WarpTo(warpToTime);
                while(Planetarium.GetUniversalTime() < warpToTime)
                    yield return new WaitForFixedUpdate();
                while(launchParams.payload != null && launchParams.payload.packed)
                    yield return new WaitForFixedUpdate();
                yield return null;
                if(!preLaunchCheck())
                    abortLaunchInternal("Pre-launch checks failed.",
                        AcceleratorState.LOADED);
            }
        }

        private IEnumerator<YieldInstruction> launchPayload()
        {
            yield return null;
            if(!(selfCheck()
                 && preLaunchCheck()))
            {
                abortLaunchInternal("Pre-launch checks failed.", AcceleratorState.LOADED);
                yield break;
            }
            yield return StartCoroutine(waitAndReCheck(180));
            if(State != AcceleratorState.LAUNCH)
                yield break;
            yield return StartCoroutine(waitAndReCheck(30));
            if(State != AcceleratorState.LAUNCH)
                yield break;
            while(Planetarium.GetUniversalTime() < launchParams.launchUT)
                yield return new WaitForFixedUpdate();
            preLaunchOrbit = new Orbit(vessel.orbit);
            launchParams.maneuverStarted = true;
            launchParams.SetPayloadUnpackDistance(vesselSize * 2);
            loadingDamper.EnableDamper(false);
            launchingDamper.AttractorPower = (float)launchParams.acceleration;
            launchingDamper.Fields.SetValue<float>(nameof(ATMagneticDamper.Attenuation), 0);
            launchingDamper.EnableDamper(true);
            this.Log(launchParams.ToString()); //debug
            while(true)
            {
                yield return new WaitForFixedUpdate();
                if(!launchParams.Valid)
                {
                    abortLaunchInternal("Payload lost.");
                    yield break;
                }
                switch(launchingDamper.VesselsInside.Count)
                {
                    case 0:
                        abortLaunchInternal("Payload left the accelerator.");
                        yield break;
                    case 1:
                        var vesselId = launchingDamper.VesselsInside.First();
                        if(launchParams.payload.persistentId == vesselId)
                        {
                            if(!maneuverIsComplete())
                                continue;
                            endLaunch();
                            UI.AddMessage("Launch succeeded.");
                            yield break;
                        }
                        abortLaunchInternal("Payload changed.",
                            AcceleratorState.ABORT);
                        yield break;
                    default:
                        abortLaunchInternal("Multiple vessels in accelerator.",
                            AcceleratorState.ABORT);
                        yield break;
                }
            }
        }

        private void endLaunch(AcceleratorState nextState = AcceleratorState.IDLE)
        {
            launchCoro = null;
            if(preLaunchOrbit != null)
            {
                var UT = Planetarium.GetUniversalTime();
                var dV = preLaunchOrbit.getOrbitalVelocityAtUT(UT)
                         - vessel.orbit.getOrbitalVelocityAtUT(UT);
                dV = Utils.Orbital2NodeDeltaV(vessel.orbit, dV, UT);
                if(!dV.IsZero())
                {
                    clearManeuverNodes();
                    if(vessel.patchedConicSolver != null)
                        Utils.AddNodeRaw(vessel, dV, UT);
                    else
                        Utils.AddNodeRawToFlightPlanNode(vessel, dV, UT);
                }
                preLaunchOrbit = null;
            }
            changeState(nextState);
        }

        private void abortLaunchInternal(
            string message = null,
            AcceleratorState nextState = AcceleratorState.IDLE
        )
        {
            if(!string.IsNullOrEmpty(message))
                UI.AddMessage(message);
            UI.AddMessage("Launch sequence aborted.");
            endLaunch(nextState);
        }

        private void abortLaunch(
            string message = null,
            AcceleratorState nextState = AcceleratorState.ABORT
        )
        {
            StopCoroutine(launchCoro);
            abortLaunchInternal(message, nextState);
        }
        #endregion

        #region Segments
        private void onNumSegmentsChange(object value)
        {
            numSegments = (int)numSegments;
            if(!updateSegments())
                Fields.SetValue<float>(nameof(numSegments), barrelSegments.Count);
        }

        private bool updateSegments()
        {
            var haveSegments = barrelSegments.Count;
            var numSegments_i = (int)numSegments;
            if(numSegments_i == haveSegments)
                return true;
            if(numSegments_i > haveSegments)
            {
                for(var i = haveSegments; i < numSegments_i; i++)
                {
                    Transform attachmentPoint;
                    if(i == 0)
                        attachmentPoint = part.FindModelTransform(BarrelAttachmentTransform);
                    else
                    {
                        var prevSegment = barrelSegments[i - 1];
                        attachmentPoint =
                            Part.FindHeirarchyTransform(prevSegment.segmentGO.transform,
                                NextSegmentTransform);
                    }
                    if(attachmentPoint == null)
                    {
                        this.Log("Unable to find attachment point transform");
                        return false;
                    }
                    var newSegment = Instantiate(barrelSegmentPrefab, attachmentPoint, false);
                    if(newSegment == null)
                    {
                        this.Log($"Unable to instantiate {barrelSegmentPrefab.GetID()}");
                        return false;
                    }
                    var newSegmentSensor =
                        Part.FindHeirarchyTransform(newSegment.transform,
                            SegmentSensorTransform);
                    if(newSegmentSensor == null)
                    {
                        this.Log(
                            $"Unable to find {SegmentSensorTransform} transform in {barrelSegmentPrefab.GetID()}");
                        Destroy(newSegment);
                        return false;
                    }
                    newSegment.transform.localPosition = Vector3.zero;
                    newSegment.transform.localRotation = Quaternion.identity;
                    if(!launchingDamper.AddDamperExtension(newSegmentSensor))
                    {
                        this.Log($"Unable to add damper extension to {newSegmentSensor.GetID()}");
                        Destroy(newSegment);
                        return false;
                    }
                    attachmentPoint.gameObject.SetActive(true);
                    newSegment.SetActive(true);
                    barrelSegments.Add(new BarrelSegment
                    {
                        segmentGO = newSegment, segmentSensor = newSegmentSensor
                    });
                }
            }
            else
            {
                for(var i = haveSegments - 1; i >= numSegments_i; i--)
                {
                    var segment = barrelSegments[i];
                    launchingDamper.RemoveDamperExtension(segment.segmentSensor);
                    Destroy(segment.segmentGO);
                    barrelSegments.RemoveAt(i);
                }
            }
            UpdateParams();
            StartCoroutine(delayedUpdateRCS());
            return true;
        }

        private IEnumerator<YieldInstruction> delayedUpdateRCS()
        {
            yield return new WaitForEndOfFrame();
            part.Modules
                .GetModules<ExtensibleRCS>()
                .ForEach(rcs => rcs.UpdateThrusterTransforms());
        }

        private void updateCoMOffset()
        {
            part.CoMOffset = Vector3.zero;
            if(barrelSegments.Count == 0)
                return;
            part.UpdateMass();
            var ori = part.partTransform.position;
            var CoM = barrelSegments.Aggregate(
                Vector3.zero,
                (current, segment) =>
                    current
                    + (segment.segmentGO.transform.TransformPoint(SegmentCoM) - ori) * SegmentMass);
            part.CoMOffset = part.partTransform.InverseTransformDirection(CoM / part.mass);
        }

        private static readonly FieldInfo partInertiaTensorFI = typeof(Part).GetField(
            "inertiaTensor",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private void updateInertiaTensor()
        {
            if(part.rb == null)
                return;
            part.rb.ResetInertiaTensor();
            var inertiaTensor = part.rb.inertiaTensor / Mathf.Max(1f, part.rb.mass);
            partInertiaTensorFI.SetValue(part, inertiaTensor);
        }

        public void UpdateParams()
        {
            updateCoMOffset();
            StartCoroutine(CallbackUtil.DelayedCallback(1, updateInertiaTensor));
            if(vessel != null)
            {
                vesselSize = vessel.Bounds().size.magnitude;
                vessel.SetUnpackDistance(vesselSize * 2);
            }
            GameEvents.onVesselWasModified.Fire(vessel);
#if DEBUG
            StartCoroutine(CallbackUtil.DelayedCallback(1,
                () =>
                {
                    if(vessel != null)
                        VesselMass = vessel.GetTotalMass();
                    else if(HighLogic.LoadedSceneIsEditor)
                        VesselMass = EditorLogic.fetch.ship.GetTotalMass();
                    this.Log($"vessel mass: {VesselMass}");
                }));
            this.Log("CoM offset: {}", part.CoMOffset);
            this.Log($"vessel size: {vesselSize}");
#endif
        }
        #endregion

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) =>
            barrelSegments.Count * SegmentMass;

        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.CONSTANTLY;

        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) =>
            barrelSegments.Count * SegmentCost;

        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.CONSTANTLY;

        public Transform GetTransform()
        {
            if(loadingDamper != null && loadingDamper.attractor != null)
                return loadingDamper.attractor;
            return vessel.GetTransform();
        }

        public Vector3 GetObtVelocity() => vessel.GetObtVelocity();

        public Vector3 GetSrfVelocity() => vessel.GetSrfVelocity();

        public Vector3 GetFwdVector()
        {
            if(loadingDamper != null && loadingDamper.attractor != null)
                return loadingDamper.attractorAxisW;
            return vessel.GetFwdVector();
        }

        public Vessel GetVessel() => vessel;

        public string GetName() => part.Title();

        public string GetDisplayName() => GetName();

        public Orbit GetOrbit() => vessel.GetOrbit();

        public OrbitDriver GetOrbitDriver() => vessel.GetOrbitDriver();

        public VesselTargetModes GetTargetingMode() =>
            VesselTargetModes.DirectionVelocityAndOrientation;

        public bool GetActiveTargetable() => false;
    }

    public class OrbitalAcceleratorUpdater : ModuleUpdater<OrbitalAccelerator>
    {
        protected override void on_rescale(ModulePair<OrbitalAccelerator> mp, Scale scale)
        {
            mp.module.SegmentMass = mp.base_module.SegmentMass * scale.absolute.volume;
            mp.module.SegmentCost = mp.base_module.SegmentCost * scale.absolute.volume;
            mp.module.UpdateParams();
        }
    }
}
