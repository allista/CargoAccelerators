using System;
using System.Collections.Generic;
using System.Linq;
using AT_Utils;
using KSP.Localization;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        [KSPField(isPersistant = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Partial launch",
            guiActive = true,
            guiActiveUnfocused = true,
            unfocusedRange = 50)]
        [UI_Toggle(scene = UI_Scene.Flight, enabledText = "Allowed", disabledText = "Forbidden")]
        public bool PartialLaunch;

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
            public double launchWindow;
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

            public void SwitchToPayload()
            {
                if(payload != null && FlightGlobals.ActiveVessel != payload)
                    FlightGlobals.ForceSetActiveVessel(payload);
            }

            public void Cleanup()
            {
                if(payloadRanges == null || payload == null)
                    return;
                payload.vesselRanges = payloadRanges;
            }

            public Vector3d GetManeuverVector()
            {
                return payload != null && node?.nextPatch != null
                    ? (node.nextPatch.GetFrameVelAtUT(node.UT)
                       - payload.orbit.GetFrameVelAtUT(node.UT)).xzy
                    : Vector3d.zero;
            }

            public ManeuverNode GetManeuverNode()
            {
                if(payload == null || node?.nextPatch == null)
                    return null;
                var remainingDeltaV = node.nextPatch.GetFrameVelAtUT(node.UT)
                                      - payload.orbit.GetFrameVelAtUT(node.UT);
                var newNode = new ManeuverNode
                {
                    UT = node.UT,
                    DeltaV = Utils.Orbital2NodeDeltaV(payload.orbit, remainingDeltaV, node.UT),
                    patch = new Orbit(payload.orbit),
                    nextPatch = new Orbit(node.nextPatch)
                };
                Utils.Debug($"new node: {newNode.ToConfigString()}\norbDeltaV: {Utils.Node2OrbitalDeltaV(newNode)}\nnode burn vector: {Utils.formatVector(node.GetBurnVector(payload.orbit))}\nnew node burn vec: {Utils.formatVector(newNode.GetBurnVector(payload.orbit))}");
                return newNode;
            }

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
                nodeDeltaVm = 0;
                node = null;
                var now = Planetarium.GetUniversalTime();
                ManeuverNode payloadNode = null;
                if(payload.patchedConicSolver != null
                   && payload.patchedConicSolver.maneuverNodes.Count > 0)
                {
                    var nodes = payload.patchedConicSolver.maneuverNodes;
                    for(int i = 0, len = nodes.Count; i < len; i++)
                    {
                        if(nodes[i].UT <= now)
                            continue;
                        payloadNode = nodes[0];
                        break;
                    }
                    if(payloadNode != null)
                    {
                        node = new ManeuverNode
                        {
                            UT = payloadNode.UT,
                            DeltaV = payloadNode.DeltaV,
                            patch = payload.orbit,
                            nextPatch = new Orbit(payloadNode.nextPatch)
                        };
                    }
                }
                else if(payload.flightPlanNode.CountNodes > 0)
                {
                    var nodes = payload.flightPlanNode.nodes;
                    for(int i = 0, len = nodes.Count; i < len; i++)
                    {
                        payloadNode = new ManeuverNode();
                        payloadNode.Load(nodes[0]);
                        if(payloadNode.UT <= now)
                            continue;
                        node = payloadNode;
                        break;
                    }
                    if(payloadNode != null)
                    {
                        node = payloadNode;
                        var hostNode = accelerator.vessel.patchedConicSolver.AddManeuverNode(node.UT);
                        hostNode.DeltaV = node.DeltaV;
                        accelerator.vessel.patchedConicSolver.UpdateFlightPlan();
                        node.patch = payload.orbit;
                        node.nextPatch = hostNode.nextPatch;
                        hostNode.RemoveSelf();
                    }
                }
                if(node == null)
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
                launchWindow = -middleDuration * GLB.LAUNCH_WINDOW;
            }

            public override string ToString()
            {
                return $@"launchParams for payload: {payload.GetID()}
nodeDeltaV: {node?.DeltaV}, |{nodeDeltaVm}|
acceleration: {acceleration}
rawDuration: {rawDuration}
duration: {duration}
energy: {energy}";
            }
        }

        public LaunchParams launchParams { get; private set; }
        private Coroutine launchCoro;
        private Orbit preLaunchOrbit;

        private uint? getLoadedVesselId(out string error)
        {
            error = string.Empty;
            if(launchingDamper.VesselsInside.Count == 0)
                return null;
            if(loadingDamper.VesselsInside.Count == 0)
            {
                if(launchingDamper.VesselsInside.Count > 0)
                    error = "A vessel is in acceleration area.";
                return null;
            }
            if(loadingDamper.VesselsInside.Count > 1)
            {
                error = "Multiple vessels in loading area.";
                return null;
            }
            if(launchingDamper.VesselsInside.Count > 1)
            {
                error = "Multiple vessels in acceleration area.";
                return null;
            }
            var loadingId = loadingDamper.VesselsInside.First();
            var launchingId = launchingDamper.VesselsInside.First();
            if(loadingId != launchingId)
            {
                error = "Multiple vessels inside accelerator.";
                return null;
            }
            return loadingId;
        }

        private bool selfCheck()
        {
            if(vessel.isActiveVessel && !vessel.PatchedConicsAttached)
            {
                UI.AddMessage("Patched conics are not available. Upgrade Tracking Station.");
                return false;
            }
            return true;
        }

        private void acquireLoadedPayload()
        {
            UI.ClearMessages();
            var vesselId = getLoadedVesselId(out var error);
            if(!vesselId.HasValue)
            {
                UI.AddMessage(error);
                return;
            }
            acquirePayload(vesselId.Value);
        }

        private void acquirePayload(uint vesselId)
        {
            UI.ClearMessages();
            Utils.ClearManeuverNodes(vessel);
            launchParams = new LaunchParams(this);
            if(launchParams.AcquirePayload(vesselId))
                checkPayloadManeuver();
#if DEBUG
            this.Debug(launchParams.ToString());
#endif
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
            if(launchParams.acceleration > launchingDamper.AttractorMaxPower)
                launchParams.acceleration = launchingDamper.AttractorMaxPower;
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
            var partialManeuver = launchParams.nodeDeltaVm > launchParams.maxDeltaV;
            if(partialManeuver)
            {
                var dV = Utils.formatBigValue((float)launchParams.maxDeltaV, "m/s");
                var dVShortage =
                    Utils.formatBigValue((float)(launchParams.nodeDeltaVm - launchParams.maxDeltaV), "m/s");
                UI.AddMessage(
                    $"This accelerator is too short for the planned maneuver of the \"{launchParams.payloadTitle}\".\nMaximum possible dV is {dV}, which is {dVShortage} less then required.");
                if(!PartialLaunch)
                    return false;
            }
            // check if launch duration is within accelerator limits
            launchParams.CalculateLaunchTiming();
            if(launchParams.duration > launchParams.maxAccelerationTime)
            {
                var timeShortage =
                    Utils.formatBigValue((float)(launchParams.duration - launchParams.maxAccelerationTime), "s");
                var maxTime = Utils.formatBigValue((float)launchParams.maxAccelerationTime, "s");
                if(!partialManeuver)
                    UI.AddMessage(
                        $"This accelerator is too short for the planned maneuver of the \"{launchParams.payloadTitle}\".\nMaximum possible acceleration time is {maxTime}, which is {timeShortage} less then required.");
                if(!PartialLaunch)
                    return false;
                launchParams.duration = launchParams.maxAccelerationTime;
                launchParams.rawDuration = Math.Min(launchParams.rawDuration, launchParams.maxAccelerationTime);
            }
            // check if there's enough energy
            launchParams.energy = launchParams.rawDuration * energyCurrent;
            vessel.GetConnectedResourceTotals(Utils.ElectricCharge.id, out var amountEC, out _);
            if(amountEC < launchParams.energy)
            {
                UI.AddMessage(
                    $"Not enough energy for the maneuver. Additional {launchParams.energy - amountEC} EC is required.");
                if(!PartialLaunch)
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
                    var crewGTolerance = ProtoCrewMember.MaxSustainedG(crewMember);
                    if(crewGTolerance < minAccelerationTolerance)
                        minAccelerationTolerance = crewGTolerance;
                }
            }
            return minAccelerationTolerance * Utils.G0 * 0.98;
        }

        private float lateralDisplacement()
        {
            var d = launchParams.payload.CurrentCoM - launchingDamper.attractor.position;
            return Vector3.ProjectOnPlane(d, launchingDamper.attractorAxisW).magnitude;
        }

        /// <summary>
        /// This check, whenever failed, aborts the launch.
        /// </summary>
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
            if(!AutoAlignEnabled)
            {
                axisController.UpdateAttitudeError();
                if(!axisController.Aligned)
                {
                    UI.AddMessage("Accelerator is not aligned with the maneuver node.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// This check tells if everything is ready for the launch.
        /// It is made frame after frame before time warp until true.
        /// It is also made one final time just before launch; in which
        /// case it causes the launch to abort.
        /// </summary>
        /// <param name="postStatus">If true, post UI message about the failed condition.</param>
        private bool canLaunch(bool postStatus = false)
        {
            if(vessel.angularVelocity.sqrMagnitude > GLB.MAX_ANGULAR_VELOCITY_SQR)
            {
                if(postStatus)
                    UI.AddMessage("Accelerator is rotating.");
                return false;
            }
            if(!AutoAlignEnabled)
                axisController.UpdateAttitudeError();
            if(!axisController.Aligned)
            {
                if(postStatus)
                    UI.AddMessage("Accelerator is not aligned with the maneuver node.");
                return false;
            }
            if(launchParams.payload.angularVelocity.sqrMagnitude > GLB.MAX_ANGULAR_VELOCITY_SQR)
            {
                if(postStatus)
                    UI.AddMessage("Payload is rotating.");
                return false;
            }
            var relVel = launchParams.payload.obt_velocity - vessel.obt_velocity;
            if(relVel.sqrMagnitude > GLB.MAX_RELATIVE_VELOCITY_SQR)
            {
                if(postStatus)
                    UI.AddMessage("Payload is moving.");
                return false;
            }
            if(lateralDisplacement() > GLB.MAX_DISPLACEMENT)
            {
                if(postStatus)
                    UI.AddMessage(
                        "Payload is not at the center of the channel.");
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

        private IEnumerator<YieldInstruction> checkAndWait(int secondsBeforeLaunch)
        {
            if(!preLaunchCheck())
            {
                abortLaunchInternal(nextState: AcceleratorState.LOADED);
                yield break;
            }
            while(Planetarium.GetUniversalTime() < launchParams.launchUT
                  && !canLaunch())
                yield return null;
            var timeLeft = launchParams.launchUT - Planetarium.GetUniversalTime();
            if(timeLeft < launchParams.launchWindow)
            {
                abortLaunchInternal("Missed launch window.",
                    AcceleratorState.LOADED);
                yield break;
            }
            if(timeLeft > secondsBeforeLaunch + 10)
            {
                var warpToTime = launchParams.launchUT - secondsBeforeLaunch;
                TimeWarp.fetch.WarpTo(warpToTime);
                while(Planetarium.GetUniversalTime() < warpToTime)
                    yield return new WaitForFixedUpdate();
                while(launchParams.payload != null && launchParams.payload.packed)
                    yield return new WaitForFixedUpdate();
                yield return null;
            }
        }

        private static readonly int[] controlPoints = { 180, 30, 10 };

        private IEnumerator<YieldInstruction> launchPayload()
        {
            yield return null;
            if(!selfCheck())
            {
                abortLaunchInternal(nextState: AcceleratorState.LOADED);
                yield break;
            }
            launchParams.SwitchToPayload();
            foreach(var controlPoint in controlPoints)
            {
                yield return StartCoroutine(checkAndWait(controlPoint));
                if(State != AcceleratorState.LAUNCH)
                    yield break;
            }
            while(Planetarium.GetUniversalTime() < launchParams.launchUT)
                yield return new WaitForFixedUpdate();
            if(!(preLaunchCheck() && canLaunch(true)))
            {
                abortLaunchInternal("Pre-launch checks failed.",
                    AcceleratorState.LOADED);
                yield break;
            }
#if DEBUG
            this.Debug(launchParams.ToString());
#endif
            preLaunchOrbit = new Orbit(vessel.orbit);
            launchParams.maneuverStarted = true;
            launchParams.SetPayloadUnpackDistance(vesselSize * 2);
            loadingDamper.EnableDamper(false);
            launchingDamper.AttractorPower = (float)launchParams.acceleration;
            launchingDamper.Fields.SetValue<float>(nameof(ATMagneticDamper.Attenuation), 0);
            launchingDamper.EnableDamper(true);
            launchParams.SwitchToPayload();
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
                        sendExecuteManeuver(launchParams.payload, launchParams.GetManeuverNode());
                        yield break;
                    case 1:
                        var vesselId = launchingDamper.VesselsInside.First();
                        if(launchParams.payload.persistentId == vesselId)
                        {
                            if(!maneuverIsComplete())
                                continue;
                            endLaunch(AcceleratorState.FINISH_LAUNCH);
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

        private static void sendExecuteManeuver(IShipconstruct vsl, ManeuverNode node = null)
        {
            var arg = node ?? new object();
            vsl.Parts.ForEach(p =>
                p.SendMessage("ExecuteManeuverNode",
                    arg,
                    SendMessageOptions.DontRequireReceiver));
        }

        private void endLaunch(AcceleratorState nextState)
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
                    Utils.ClearManeuverNodes(vessel);
                    if(vessel.patchedConicSolver != null)
                        Utils.AddNodeRaw(vessel, dV, UT);
                    else
                        Utils.AddNodeRawToFlightPlanNode(vessel, dV, UT);
                    sendExecuteManeuver(vessel);
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
    }
}
