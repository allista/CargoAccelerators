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
                nodeDeltaVm = 0;
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

        private void acquirePayload()
        {
            UI.ClearMessages();
            var numberOfVessels = launchingDamper.VesselsInside.Count;
            if(numberOfVessels != 1)
            {
                UI.AddMessage(numberOfVessels == 0
                    ? "No payload in acceleration area."
                    : "Multiple vessels in acceleration area.");
                return;
            }
            clearManeuverNodes();
            launchParams = new LaunchParams(this);
            if(launchParams.AcquirePayload(launchingDamper.VesselsInside.First()))
                checkPayloadManeuver();
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
            if((launchParams.payload.CurrentCoM
                - loadingDamper.attractor.position).magnitude
               > GLB.MAX_DISPLACEMENT)
            {
                if(postStatus)
                    UI.AddMessage(
                        "Payload is not at the launch position.");
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
            if(timeLeft < 0)
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

        private IEnumerator<YieldInstruction> launchPayload()
        {
            yield return null;
            if(!selfCheck())
            {
                abortLaunchInternal(nextState: AcceleratorState.LOADED);
                yield break;
            }
            yield return StartCoroutine(checkAndWait(180));
            if(State != AcceleratorState.LAUNCH)
                yield break;
            yield return StartCoroutine(checkAndWait(30));
            if(State != AcceleratorState.LAUNCH)
                yield break;
            yield return StartCoroutine(checkAndWait(10));
            if(State != AcceleratorState.LAUNCH)
                yield break;
            while(Planetarium.GetUniversalTime() < launchParams.launchUT)
                yield return new WaitForFixedUpdate();
            if(!(preLaunchCheck() && canLaunch(true)))
            {
                abortLaunchInternal("Pre-launch checks failed.",
                    AcceleratorState.LOADED);
                yield break;
            }
#if DEBUG
            this.Log(launchParams.ToString());
#endif
            preLaunchOrbit = new Orbit(vessel.orbit);
            launchParams.maneuverStarted = true;
            launchParams.SetPayloadUnpackDistance(vesselSize * 2);
            loadingDamper.EnableDamper(false);
            launchingDamper.AttractorPower = (float)launchParams.acceleration;
            launchingDamper.Fields.SetValue<float>(nameof(ATMagneticDamper.Attenuation), 0);
            launchingDamper.EnableDamper(true);
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
    }
}
