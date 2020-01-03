using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AT_Utils;
using KSP.Localization;
using UnityEngine;

namespace CargoAccelerators
{
    public enum AcceleratorState { OFF, LOAD, FIRE }

    public class OrbitalAccelerator : PartModule, IPartMassModifier, IPartCostModifier, ITargetable
    {
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

        [KSPField(isPersistant = true)] public AcceleratorState State;

        [KSPField(guiActive = true,
            guiActiveEditor = true,
            guiActiveUnfocused = true,
            unfocusedRange = 100,
            guiName = "Accelerator State")]
        [UI_ChooseOption]
        public string StateChoice = string.Empty;

#if DEBUG
        [KSPField(guiActive = true,
            guiActiveEditor = true,
            guiName = "Vessel Mass",
            guiUnits = "t",
            guiFormat = "F1")]
        public float VesselMass;
#endif

        public ATMagneticDamper loadingDamper;
        public ExtensibleMagneticDamper launchingDamper;
        public GameObject barrelSegmentPrefab;
        private float vesselRadius;
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
            var states = Enum.GetNames(typeof(AcceleratorState));
            var stateField = Fields[nameof(StateChoice)];
            Utils.SetupChooser(states, states, stateField);
            stateField.OnValueModified += onStateChange;
            loadingDamper.OnDamperAutoEnabled += autoLoad;
            setState(State);
        }

        private void OnDestroy()
        {
            Fields[nameof(numSegments)].OnValueModified -= onNumSegmentsChange;
            Fields[nameof(StateChoice)].OnValueModified -= onStateChange;
            if(loadingDamper != null)
                loadingDamper.OnDamperAutoEnabled -= autoLoad;
            if(FlightGlobals.fetch == null)
                return;
            if(ReferenceEquals(FlightGlobals.fetch.VesselTarget, this))
                vessel.StartCoroutine(CallbackUtil.DelayedCallback(1,
                    FlightGlobals.fetch.SetVesselTarget,
                    vessel,
                    false));
        }

        #region State
        private void autoLoad() => setState(AcceleratorState.LOAD);

        private void setState(AcceleratorState state)
        {
            State = state;
            var choice = Enum.GetName(typeof(AcceleratorState), State);
            if(StateChoice != choice)
            {
                StateChoice = choice;
                MonoUtilities.RefreshPartContextWindow(part);
            }
            actuateState();
        }

        private void actuateState()
        {
            if(State != AcceleratorState.FIRE && launchCoro != null)
            {
                abortLaunch();
                return;
            }
            loadingDamper.AttractorEnabled = true;
            loadingDamper.InvertAttractor = false;
            launchingDamper.AttractorEnabled = true;
            launchingDamper.InvertAttractor = true;
            switch(State)
            {
                case AcceleratorState.OFF:
                    loadingDamper.AutoEnable = true;
                    loadingDamper.EnableDamper(false);
                    launchingDamper.EnableDamper(false);
                    break;
                case AcceleratorState.LOAD:
                    loadingDamper.EnableDamper(true);
                    launchingDamper.EnableDamper(false);
                    break;
                case AcceleratorState.FIRE:
                    launchCoro = StartCoroutine(launchPayload());
                    break;
                default:
                    goto case AcceleratorState.OFF;
            }
        }

        private void onStateChange(object value)
        {
            if(!Enum.TryParse<AcceleratorState>(StateChoice, out var state))
                return;
            State = state;
            actuateState();
        }
        #endregion

        #region Launch
        private class LaunchParams
        {
            private readonly Vessel host;
            public Vessel payload;
            public string payloadTitle;
            private VesselRanges payloadRanges;

            public ManeuverNode node;
            public Vector3d nodeDeltaV;
            public double nodeDeltaVm;
            public double launchUT;
            public double rawDuration;
            public double duration;
            public double energy;
            public double acceleration;
            public double maxAccelerationTime;
            public double maxDeltaV;

            public bool Valid =>
                payload != null
                && node?.nextPatch != null;

            public LaunchParams(Vessel vsl) => host = vsl;

            public void SetPayloadUnpackDistance(float distance)
            {
                payloadRanges = payload.SetUnpackDistance(distance);
            }

            public void Cleanup()
            {
                if(node != null && node.solver == host.patchedConicSolver)
                    node.RemoveSelf();
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
                    Utils.Message("Unable to find payload.");
                    return false;
                }
                payloadTitle = Localizer.Format(payload.vesselName);
                if(payload.patchedConicSolver != null
                   && payload.patchedConicSolver.maneuverNodes.Count > 0)
                    node = payload.patchedConicSolver.maneuverNodes[0];
                else if(payload.flightPlanNode.CountNodes > 0)
                {
                    var payloadNode = new ManeuverNode();
                    payloadNode.Load(payload.flightPlanNode.nodes[0]);
                    node = host.patchedConicSolver.AddManeuverNode(payloadNode.UT);
                    node.DeltaV = payloadNode.DeltaV;
                    host.patchedConicSolver.UpdateFlightPlan();
                }
                else
                {
                    Utils.Message("Payload doesn't have a maneuver node.");
                    return false;
                }
                nodeDeltaV = node.DeltaV;
                nodeDeltaVm = node.DeltaV.magnitude;
                return true;
            }

            public void calculateLaunchTiming()
            {
                // duration of the maneuver without acceleration tuning
                rawDuration = nodeDeltaVm / acceleration;
                var middleDeltaV = nodeDeltaVm / 2;
                // duration of the maneuver with acceleration tuning
                var fullAccelerationTime =
                    (int)Math.Ceiling(Math.Max(nodeDeltaVm
                                               / acceleration
                                               / TimeWarp.fixedDeltaTime
                                               - FINE_TUNE_FRAMES,
                        0))
                    * TimeWarp.fixedDeltaTime;
                var dVrem = nodeDeltaVm - acceleration * fullAccelerationTime;
                var middleDuration = dVrem < middleDeltaV ? rawDuration / 2 : fullAccelerationTime;
                duration = fullAccelerationTime;
                while(dVrem > MANEUVER_DELTA_V_TOL)
                {
                    var a = dVrem / TimeWarp.fixedDeltaTime / (FINE_TUNE_FRAMES + 1);
                    dVrem -= a * TimeWarp.fixedDeltaTime;
                    if(dVrem > middleDeltaV)
                        middleDuration += TimeWarp.fixedDeltaTime;
                    duration += TimeWarp.fixedDeltaTime;
                }
                // calculate launch start UT
                launchUT = node.UT - middleDuration;
                Utils.Log($"middle dV {middleDeltaV}, t {middleDuration}"); //debug
            }

            public override string ToString()
            {
                return $@"launchParams for payload: {payload.GetID()}
nodeDeltaV: {nodeDeltaV}, |{nodeDeltaVm}|
acceleration: {acceleration}
rawDuration: {rawDuration}
duration: {duration}
energy: {energy}";
            }
        }

        private LaunchParams launchParams;
        private Coroutine launchCoro;
        private Orbit preLaunchOrbit;

        private const double MAX_ANGULAR_VELOCITY_SQR = 0.000010132118; //0.02 deg/s
        private const double MAX_RELATIVE_VELOCITY_SQR = 0.00025; //0.05 m/s
        private const double MANEUVER_DELTA_V_TOL = 0.01;
        private const int FINE_TUNE_FRAMES = 3;

        private bool selfCheck()
        {
            if(vessel.isActiveVessel && !vessel.PatchedConicsAttached)
            {
                Utils.Message("Patched conics are not available. Upgrade Tracking Station.");
                return false;
            }
            if(vessel.angularVelocity.sqrMagnitude > MAX_ANGULAR_VELOCITY_SQR)
            {
                Utils.Message("The accelerator is rotating. Stop the rotation and try again.");
                return false;
            }
            return true;
        }

        private bool checkPayload()
        {
            if(launchParams.payload.angularVelocity.sqrMagnitude > MAX_ANGULAR_VELOCITY_SQR)
            {
                Utils.Message("Payload is rotating. Stop the rotation and try again.");
                return false;
            }
            var relV2 = (launchParams.payload.orbit.vel - vessel.orbit.vel).sqrMagnitude;
            if(relV2 > MAX_RELATIVE_VELOCITY_SQR)
            {
                Utils.Message("Payload is moving. Wait for it to stop and try again.");
                return false;
            }
            return true;
        }

        private void clearManeuverNodes()
        {
            if(vessel.patchedConicSolver == null)
            {
                vessel.flightPlanNode.ClearData();
                return;
            }
            var nodes = vessel.patchedConicSolver.maneuverNodes;
            for(var i = nodes.Count - 1; i >= 0; i--)
                nodes[i].RemoveSelf();
        }

        private bool acquirePayload()
        {
            var numberOfVessels = launchingDamper.VesselsInside.Count;
            if(numberOfVessels != 1)
            {
                Utils.Message(numberOfVessels == 0
                    ? "No payload in acceleration area."
                    : "Multiple vessels in acceleration area.");
                return false;
            }
            clearManeuverNodes();
            launchParams = new LaunchParams(vessel);
            if(!launchParams.AcquirePayload(launchingDamper.VesselsInside.First()))
                return false;
            if(!checkPayload())
                return false;
            if(!checkPayloadManeuver())
                return false;
            this.Log($"Payload acquired: {launchParams}"); //debug
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
                Utils.Message(
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
                Utils.Message(
                    $"This accelerator is too short for the planned maneuver of the \"{launchParams.payloadTitle}\".\nMaximum possible dV is {dVShortage:F1} m/s less then required.");
                return false;
            }
            // check if launch duration is within accelerator limits
            launchParams.calculateLaunchTiming();
            if(launchParams.duration > launchParams.maxAccelerationTime)
            {
                var timeShortage = launchParams.duration - launchParams.maxAccelerationTime;
                Utils.Message(
                    $"This accelerator is too short for the planned maneuver of the \"{launchParams.payloadTitle}\".\nMaximum possible acceleration time is {timeShortage:F1} s less then required.");
                return false;
            }
            // check if there's enough energy
            launchParams.energy = launchParams.rawDuration * energyCurrent;
            vessel.GetConnectedResourceTotals(Utils.ElectricCharge.id, out var amountEC, out _);
            if(amountEC < launchParams.energy)
            {
                Utils.Message(
                    $"Not enough energy for the maneuver. Additional {launchParams.energy - amountEC} EC is required.");
                return false;
            }
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
            var nodeBurnVector = launchParams.GetManeuverVector();
            var attitudeError =
                Utils.Angle2((Vector3)nodeBurnVector, launchingDamper.attractorAxisW);
            this.Log($"Attitude error: {attitudeError}"); //debug
            if(attitudeError > 0.05f)
            {
                Utils.Message("Accelerator is not aligned with the maneuver node");
                return false;
            }
            return true;
        }

        private bool maneuverIsComplete()
        {
            var nodeBurnVector = launchParams.GetManeuverVector();
            var remainingDeltaV = nodeBurnVector.magnitude;
            var nodeDotAxis = Vector3d.Dot(nodeBurnVector, launchingDamper.attractorAxisW); //debug
            var attitudeError =
                Utils.Angle2((Vector3)nodeBurnVector, launchingDamper.attractorAxisW); //debug
            this.Log(
                $"Maneuver dV: {nodeBurnVector} |{remainingDeltaV}|, along axis {nodeDotAxis}, attitude error {attitudeError} deg"); //debug
            if(nodeDotAxis < MANEUVER_DELTA_V_TOL)
                return true;
            if(nodeDotAxis / launchParams.acceleration
               < TimeWarp.fixedDeltaTime * FINE_TUNE_FRAMES)
            {
                // dividing by 4 because the next physical frame the old AttractorPower is still in effect
                launchingDamper.AttractorPower =
                    (float)nodeDotAxis / TimeWarp.fixedDeltaTime / (FINE_TUNE_FRAMES + 1);
                this.Log($"Decreasing acceleration to: {launchingDamper.AttractorPower}"); //debug
            }
            return false;
        }

        private IEnumerator<YieldInstruction> launchPayload()
        {
            yield return null;
            if(!(selfCheck()
                 && acquirePayload()
                 && preLaunchCheck()))
            {
                abortLaunchInternal();
                yield break;
            }
            var timeLeft = launchParams.launchUT - Planetarium.GetUniversalTime();
            if(timeLeft > 0)
                Utils.Message($"Waiting for the node: {timeLeft:F1} s");
            if(timeLeft > 15)
                TimeWarp.fetch.WarpTo(launchParams.launchUT - 10);
            while(Planetarium.GetUniversalTime() < launchParams.launchUT)
                yield return new WaitForFixedUpdate();
            Utils.Message($"Launching: {launchParams.payloadTitle}");
            preLaunchOrbit = new Orbit(vessel.orbit);
            launchParams.SetPayloadUnpackDistance(vesselRadius * 2);
            loadingDamper.AutoEnable = false;
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
                            Utils.Message("Launch succeeded.");
                            yield break;
                        }
                        abortLaunchInternal("Payload changed.");
                        yield break;
                    default:
                        abortLaunchInternal("Multiple vessels in accelerator.");
                        yield break;
                }
            }
        }

        private void endLaunch()
        {
            launchParams?.Cleanup();
            launchParams = null;
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
            setState(AcceleratorState.OFF);
        }

        private void abortLaunchInternal(string message = null)
        {
            Utils.Message("Launch sequence aborted.");
            if(!string.IsNullOrEmpty(message))
                Utils.Message(message);
            endLaunch();
        }

        private void abortLaunch()
        {
            if(launchCoro == null)
                return;
            StopCoroutine(launchCoro);
            abortLaunchInternal();
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
            this.Log(
                $"Orig IT {partInertiaTensorFI.GetValue(part)}, new IT {inertiaTensor}"); //debug
            partInertiaTensorFI.SetValue(part, inertiaTensor);
        }

        public void UpdateParams()
        {
            updateCoMOffset();
            StartCoroutine(CallbackUtil.DelayedCallback(1, updateInertiaTensor));
            if(vessel != null)
            {
                vesselRadius = vessel.Bounds().size.magnitude;
                vessel.SetUnpackDistance(vesselRadius * 2);
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
            this.Log($"vessel radius: {vesselRadius}");
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
