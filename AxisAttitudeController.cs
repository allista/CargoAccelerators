using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using AT_Utils;
using UnityEngine;

namespace CargoAccelerators
{
    public class AxisAttitudeController
    {
        private static Globals GLB => Globals.Instance;
        private readonly OrbitalAccelerator host;
        private Vessel vessel => host.vessel;
        private OrbitalAccelerator.LaunchParams launchParams => host.launchParams;
        private ATMagneticDamper launchingDamper => host.launchingDamper;
        private bool connected;

        private PIDf_Controller2 rollPID;
        private ConstAttitudeController pitchController, yawController;

        private List<ITorqueProvider> torqueProviders = new List<ITorqueProvider>();

        public bool HasUserInput { get; private set; }

        /// <summary>
        /// The angular error (in degrees) between the payload node burn vector
        /// and the accelerator's axis.
        /// The Vector3 stores as its coordinates the following:
        /// x - angle between the burn vector and the axis
        /// y - pitch error
        /// z - yaw error 
        /// </summary>
        public Vector3 AttitudeError { get; private set; }

        public bool Aligned { get; private set; }

        private bool isActive =>
            HighLogic.LoadedSceneIsFlight
            && !FlightDriver.Pause
            && host != null
            && host.AutoAlignEnabled
            && launchParams != null
            && vessel != null;

        private Vector3 steering;

        public AxisAttitudeController(OrbitalAccelerator accelerator)
        {
            host = accelerator;
            InitPIDs();
            Connect();
            UpdateTorqueProviders();
        }

        ~AxisAttitudeController() => Disconnect();

        public void InitPIDs()
        {
            rollPID = GLB.RollController.Clone<PIDf_Controller2>();
            pitchController = GLB.PitchYawController.Clone<ConstAttitudeController>();
            yawController = GLB.PitchYawController.Clone<ConstAttitudeController>();
            pitchController.name = "pitch"; //debug
            yawController.name = "yaw"; //debug
        }

        public void Connect()
        {
            if(connected || host == null || vessel == null)
                return;
            vessel.OnPreAutopilotUpdate += calculateSteering;
            vessel.OnAutopilotUpdate += applySteering;
            connected = true;
        }

        public void Disconnect()
        {
            if(!connected || host == null || vessel == null)
                return;
            vessel.OnPreAutopilotUpdate -= calculateSteering;
            vessel.OnAutopilotUpdate -= applySteering;
            torqueProviders.Clear();
            connected = false;
        }

        public void UpdateAttitudeError()
        {
            var nodeBurnVector = launchParams.GetManeuverVector();
            var axis = launchingDamper.attractorAxisW;
            var attitudeError = Utils.Angle2((Vector3)nodeBurnVector, axis);
            var locRot = Quaternion.Inverse(vessel.ReferenceTransform.rotation);
            var rot = Utils.FromToRotation(locRot * axis, locRot * nodeBurnVector);
            var pitch = Utils.CenterRad(Math.Atan2(2 * (rot.w * rot.x + rot.z * rot.y),
                            2 * (rot.w * rot.w + rot.z * rot.z) - 1))
                        * Mathf.Rad2Deg;
            var yaw = Utils.CenterRad(Math.Atan2(2 * (rot.w * rot.z + rot.x * rot.y),
                          1 - 2 * (rot.y * rot.y + rot.z * rot.z)))
                      * Mathf.Rad2Deg;
            AttitudeError = new Vector3(attitudeError, (float)pitch, (float)yaw);
            Aligned = attitudeError < GLB.MAX_ATTITUDE_ERROR;
        }

        public void UpdateTorqueProviders()
        {
            if(host == null || vessel == null)
                return;
            torqueProviders = vessel.FindPartModulesImplementing<ITorqueProvider>();
        }

        public void Reset()
        {
            rollPID.Reset();
            pitchController.Reset();
            yawController.Reset();
        }

        private Vector3 getTorque()
        {
            var torque = Vector6.zero;
            var locRot = Quaternion.Inverse(vessel.ReferenceTransform.rotation);
            var CoM = vessel.CurrentCoM;
            var partShielded = host.part.ShieldedFromAirstream;
            for(int i = 0, count = torqueProviders.Count; i < count; i++)
            {
                var torqueProvider = torqueProviders[i];
                if(torqueProvider is ModuleRCS rcs)
                {
                    if(!rcs.moduleIsEnabled
                       || !rcs.rcsEnabled
                       || !rcs.rcs_active
                       || rcs.isJustForShow
                       || rcs.flameout
                       || partShielded && !rcs.shieldedCanThrust)
                        continue;
                    var thrustM = rcs.thrusterPower * rcs.thrustPercentage / 100;
                    var nT = rcs.thrusterTransforms.Count;
                    while(nT-- > 0)
                    {
                        var thruster = rcs.thrusterTransforms[nT];
                        var lever = thruster.position - CoM;
                        var thrustDir = rcs.useZaxis ? thruster.forward : thruster.up;
                        var specificTorque = locRot * Vector3.Cross(lever, thrustDir);
                        if(!rcs.enablePitch)
                            specificTorque.x = 0;
                        if(!rcs.enableRoll)
                            specificTorque.y = 0;
                        if(!rcs.enableYaw)
                            specificTorque.z = 0;
                        torque.Add(specificTorque * thrustM);
                    }
                }
                else
                {
                    torqueProvider.GetPotentialTorque(out var pos, out var neg);
                    if(torqueProvider is ModuleReactionWheel rw)
                    {
                        var limit = rw.authorityLimiter / 100;
                        pos *= limit;
                        neg *= limit;
                    }
                    torque.Add(pos);
                    torque.Add(-neg);
                }
            }
            return torque.Max;
        }

        private void calculateSteering(FlightCtrlState s)
        {
            if(!isActive)
                return;
            HasUserInput = !Mathfx.Approx(s.pitch, s.pitchTrim, GLB.USER_INPUT_TOL)
                           || !Mathfx.Approx(s.roll, s.rollTrim, GLB.USER_INPUT_TOL)
                           || !Mathfx.Approx(s.yaw, s.yawTrim, GLB.USER_INPUT_TOL);
            if(HasUserInput)
                return;
            UpdateAttitudeError();
            steering.Zero();
            var torque = getTorque();
            var maxAA = Utils.AngularAcceleration(torque, vessel.MOI) * Mathf.Rad2Deg;
            var AV = vessel.angularVelocity * Mathf.Rad2Deg;
            // x is direct error, y is pitch; see AttitudeError description
            rollPID.Update(AV.y);
            steering.x = pitchController.Update(AttitudeError.y, -AV.x, maxAA.x);
            steering.y = rollPID.Action;
            steering.z = yawController.Update(AttitudeError.z, -AV.z, maxAA.z);
        }

        private void applySteering(FlightCtrlState s)
        {
            if(!isActive || HasUserInput)
                return;
            s.pitch = Utils.Clamp(steering.x, -1, 1);
            s.roll = Utils.Clamp(steering.y, -1, 1);
            s.yaw = Utils.Clamp(steering.z, -1, 1);
        }
    }

    [SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Local")]
    [SuppressMessage("ReSharper", "ConvertToConstant.Local")]
    public class ConstAttitudeController : ConfigNodeObject
    {
        public string name = ""; //debug
        [Persistent] private float avFilter = 1f;
        [Persistent] private float odFilter = 1f;

        [Persistent] private float accelerateThreshold = 0.5f;
        [Persistent] private float decelerateThresholdLower = 0.9f;
        [Persistent] private float decelerateThresholdUpper = 0.99f;
        [Persistent] private float upperLowerActionThreshold = 0.01f;
        [Persistent] private float angleErrorToActionP = 3f;

        [Persistent] private float angularErrorTolerance = 0.001f; //deg
        [Persistent] private float maxAngularVelocity = 1f; // deg/s
        [Persistent] private float maxAngularAcceleration = 0.1f; // deg/s2

        [Persistent] private PIDf_Controller3 PID = new PIDf_Controller3();
        private PIDf_Controller3 pid;

        private LowPassFilterF avActionFilter = new LowPassFilterF();
        private OscillationDetectorF OD = new OscillationDetectorF(0.5f, 3, 100, 500, 5);

        public override void Load(ConfigNode node)
        {
            base.Load(node);
            pid = PID.Clone<PIDf_Controller3>();
        }

        public void Reset()
        {
            OD.Reset();
            avActionFilter.Reset();
            pid.Reset();
        }

        public float Update(float angleError, float angularVelocity, float maxAA)
        {
            var aaNorm = Utils.ClampH(maxAngularAcceleration / maxAA, 1);
            var aaCap = Mathf.Min(maxAngularAcceleration, maxAA);
            var tau = avFilter * TimeWarp.fixedDeltaTime;
            avActionFilter.Tau = tau;
            pid.setTau(tau);
            var errorDecreases = angleError < 0 && angularVelocity > 0
                                 || angleError > 0 && angularVelocity < 0;
            if(Mathf.Abs(angleError) < angularErrorTolerance)
                pid.Update(Utils.Clamp(angularVelocity / aaCap, -1, 1) * aaNorm);
            else if(errorDecreases)
            {
                var accelToStop = angleError.Equals(0)
                    ? 0
                    : angularVelocity * angularVelocity / 2 / angleError;
                var accelToStopAbs = Mathf.Abs(accelToStop);
                var decelerateThreshold = Mathf.Abs(pid.Action) < upperLowerActionThreshold
                    ? decelerateThresholdUpper
                    : decelerateThresholdLower;
                if(accelToStopAbs > decelerateThreshold * aaCap)
                    pid.Update(-Utils.Clamp(accelToStop, -aaCap, aaCap) / maxAA);
                else if(accelToStopAbs < accelerateThreshold * aaCap
                        && Math.Abs(angularVelocity) < maxAngularVelocity)
                    pid.Update(Utils.Clamp(angleError * angleErrorToActionP, -1, 1) * aaNorm);
                else
                    pid.Update(0);
            }
            else
                pid.Update(Utils.Clamp(angleError * angleErrorToActionP + angularVelocity / aaCap,
                               -1,
                               1)
                           * aaNorm);
            var action = tau > 0 ? avActionFilter.Update(pid.Action) : pid.Action;
            if(odFilter > 0)
                action *= 1 - odFilter * OD.Update(pid.Action, TimeWarp.fixedDeltaTime);
            DebugUtils.CSV($"AxisAttitudeCascade-{name}.csv",
                Time.timeSinceLevelLoad,
                angleError,
                angularVelocity,
                pid.Action,
                action); //debug
            return -action;
        }
    }
}
