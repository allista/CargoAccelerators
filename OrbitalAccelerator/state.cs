using System;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        public enum AcceleratorState
        {
            IDLE,
            LOADED,
            ACQUIRE_PAYLOAD,
            EJECT,
            LAUNCH,
            ABORT,
            FINISH_LAUNCH,
            UNDER_CONSTRUCTION,
            CONNECT_TO_PAYLOAD,
            CONNECTED,
        }

        private void changeState(AcceleratorState newState)
        {
            State = newState;
            UI.UpdateState();
        }

        private void destroyLaunchParams()
        {
            if(launchParams == null)
                return;
            launchParams.Cleanup();
            launchParams = null;
            UI.UpdatePayloadInfo();
        }

        private void disableDampers()
        {
            destroyLaunchParams();
            if(loadingDamper.DamperEnabled)
                loadingDamper.EnableDamper(false);
            if(launchingDamper.DamperEnabled)
                launchingDamper.EnableDamper(false);
        }

        private void checkPayloadConnection(AcceleratorState onSuccessState)
        {
            UI.UpdatePayloadInfo();
            ToggleAutoAlign(false);
            changeState(launchParams != null
                ? onSuccessState
                : AcceleratorState.IDLE);
        }

        private bool checkVesselDistance(Vessel vsl)
        {
            if(vsl.loaded
               && (loadingDamper.attractor.position
                   - vsl.CurrentCoM).magnitude
               < MaxConnectionDistance)
                return true;
            ToggleAutoAlign(false);
            UI.AddMessage("Too far away for remote connection.\n"
                          + $"Have to be closer than {MaxConnectionDistance:F0} m.");
            return false;
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
                case AcceleratorState.UNDER_CONSTRUCTION:
                    disableDampers();
                    constructionUpdate();
                    break;
                case AcceleratorState.IDLE:
                    destroyLaunchParams();
                    if(launchingDamper.DamperEnabled)
                        launchingDamper.EnableDamper(false);
                    if(getLoadedVesselId(out var error).HasValue)
                        changeState(AcceleratorState.ACQUIRE_PAYLOAD);
                    else if(!string.IsNullOrEmpty(error))
                        UI.SetMessage(error);
                    break;
                case AcceleratorState.CONNECT_TO_PAYLOAD:
                    Vessel connectToVessel = null;
                    if(vessel.isActiveVessel)
                    {
                        if(vessel.targetObject != null)
                            connectToVessel = vessel.targetObject.GetVessel();
                    }
                    else
                        connectToVessel = FlightGlobals.ActiveVessel;
                    if(connectToVessel != null
                       && checkVesselDistance(connectToVessel))
                    {
                        acquirePayload(connectToVessel.persistentId);
                        checkPayloadConnection(AcceleratorState.CONNECTED);
                        break;
                    }
                    changeState(AcceleratorState.IDLE);
                    break;
                case AcceleratorState.CONNECTED:
                    if(launchingDamper.DamperEnabled)
                        launchingDamper.EnableDamper(false);
                    if(launchParams == null
                       || getLoadedVesselId(out _).HasValue
                       || !checkVesselDistance(launchParams.payload))
                        changeState(AcceleratorState.IDLE);
                    break;
                case AcceleratorState.LOADED:
                    if(!getLoadedVesselId(out _).HasValue)
                        changeState(AcceleratorState.IDLE);
                    else if(launchParams == null)
                        changeState(AcceleratorState.ACQUIRE_PAYLOAD);
                    break;
                case AcceleratorState.ACQUIRE_PAYLOAD:
                    acquireLoadedPayload();
                    checkPayloadConnection(AcceleratorState.LOADED);
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
                    loadingDamper.AutoEnable = false;
                    if(launchCoro == null)
                        launchCoro = StartCoroutine(launchPayload());
                    break;
                case AcceleratorState.FINISH_LAUNCH:
                    launchingDamper.AttractorEnabled = false;
                    if(launchingDamper.VesselsInside.Count == 0)
                        changeState(AcceleratorState.IDLE);
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
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void FixedUpdate()
        {
            if(!HighLogic.LoadedSceneIsFlight || FlightDriver.Pause)
                return;
            if(State == AcceleratorState.ABORT)
            {
                var payloadRelV = (launchParams.payload.orbit.vel - vessel.orbit.vel).xzy;
                if(Vector3d.Dot(payloadRelV, launchingDamper.attractorAxisW) < 0.1)
                {
                    launchingDamper.EnableDamper(false);
                    changeState(AcceleratorState.IDLE);
                }
            }
            constructionFixedUpdate();
        }

        private void LateUpdate()
        {
            if(!HighLogic.LoadedSceneIsFlight || FlightDriver.Pause || !UI.IsShown)
                return;
            if(launchParams == null || !launchParams.Valid)
                return;
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
            if(part.packed 
               || State == AcceleratorState.CONNECTED)
                return;
            var relV = (launchParams.payload.obt_velocity
                        - vessel.obt_velocity).sqrMagnitude;
            var payloadAV = launchParams.payload.angularVelocity.sqrMagnitude;
            var d = lateralDisplacement();
            UI.Controller.UpdatePayloadInfo((float)launchParams.maxDeltaV,
                (float)Math.Sqrt(relV),
                Mathf.Sqrt(payloadAV) * Mathf.Rad2Deg,
                d,
                launchParams.maxDeltaV > launchParams.nodeDeltaVm,
                relV < GLB.MAX_RELATIVE_VELOCITY_SQR,
                payloadAV < GLB.MAX_ANGULAR_VELOCITY_SQR,
                d < GLB.MAX_DISPLACEMENT);
        }
    }
}
