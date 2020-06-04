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
            UNDER_CONSTRUCTION,
        }
        
        private void changeState(AcceleratorState newState)
        {
            State = newState;
            UI.UpdateState();
        }

        private void disableDampers()
        {
            if(launchParams != null)
            {
                launchParams.Cleanup();
                launchParams = null;
                UI.UpdatePayloadInfo();
            }
            if(loadingDamper.DamperEnabled)
                loadingDamper.EnableDamper(false);
            if(launchingDamper.DamperEnabled)
                launchingDamper.EnableDamper(false);
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
            if(part.packed)
                return;
            var relV = (launchParams.payload.obt_velocity
                        - vessel.obt_velocity).sqrMagnitude;
            var payloadAV = launchParams.payload.angularVelocity.sqrMagnitude;
            var dist = (launchParams.payload.CurrentCoM
                        - loadingDamper.attractor.position).magnitude;
            UI.Controller.UpdatePayloadInfo((float)launchParams.maxDeltaV,
                (float)Math.Sqrt(relV),
                Mathf.Sqrt(payloadAV) * Mathf.Rad2Deg,
                dist,
                launchParams.maxDeltaV > launchParams.nodeDeltaVm,
                relV < GLB.MAX_RELATIVE_VELOCITY_SQR,
                payloadAV < GLB.MAX_ANGULAR_VELOCITY_SQR,
                dist < GLB.MAX_DISPLACEMENT);
        }
    }
}
