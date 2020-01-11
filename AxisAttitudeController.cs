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

        private AttitudePIDCascade pitchPID, yawPID;
        private PIDf_Controller2 rollPID;

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
            pitchPID = GLB.PitchYawController.Clone<AttitudePIDCascade>();
            rollPID = GLB.RollController.Clone<PIDf_Controller2>();
            yawPID = GLB.PitchYawController.Clone<AttitudePIDCascade>();
            pitchPID.name = "pitch"; //debug
            yawPID.name = "yaw"; //debug
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
            steering.x = pitchPID.Update(AttitudeError.y, -AV.x, maxAA.x);
            steering.y = rollPID.Action;
            steering.z = yawPID.Update(AttitudeError.z, -AV.z, maxAA.z);
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
    public class AttitudePIDCascade : ConfigNodeObject
    {
        public string name = ""; //debug
        [Persistent] private float avFilter = 1f;
        [Persistent] private float odFilter = 1f;
        [Persistent] private float angularErrorTolerance = 0.001f; //deg
        [Persistent] private float maxAngularVelocity = 1f; // deg/s
        [Persistent] private float maxAngularAcceleration = 0.1f; // deg/s2
        [Persistent] private float avPID_D = 0.1f; // deg/s2
        [Persistent] private float avPID_D_threshold = 0.1f; // deg/s2

        [Persistent] private PIDf_Controller3 avPID = new PIDf_Controller3();

        private LowPassFilterF avActionFilter = new LowPassFilterF();
        private OscillationDetectorF OD = new OscillationDetectorF(0.5f, 3, 100, 500, 5);

        public float Update(float angleError, float angularVelocity, float maxAA)
        {
            var aaNorm = Utils.ClampH(maxAngularAcceleration / maxAA, 1);
            var aaCap = Mathf.Min(maxAngularAcceleration, maxAA);
            var avError = angularVelocity;
            var absAngleError = Mathf.Abs(angleError);
            if(absAngleError > angularErrorTolerance)
            {
                // When angular velocity < 0 it decreases the angleError
                var eta = angularVelocity.Equals(0) ? 1 : angleError / -angularVelocity;
                var maxAV = eta >= 0
                    ? eta * aaCap
                    : maxAngularVelocity;
                // The avError > 0 means that AV is greater than it should be.
                avError += Utils.Clamp(angleError * maxAV,
                    -maxAngularVelocity,
                    maxAngularVelocity);
            }
            var tau = avFilter * TimeWarp.fixedDeltaTime;
            // avPID.Action is the required angular acceleration
            // so the avError is re-normalized to AA scale
            avPID.Min = -aaCap;
            avPID.Max = aaCap;
            avPID.D = absAngleError > avPID_D_threshold ? 0 : avPID_D;
            avPID.setTau(tau);
            avPID.Update(avError * aaCap / maxAngularVelocity);
            avActionFilter.Tau = tau;
            avPID.Action = avActionFilter.Update(avPID.Action);
            avPID.Action *= 1 - odFilter * OD.Update(avPID.Action, TimeWarp.fixedDeltaTime);
            avPID.Action = -Utils.Clamp(avPID.Action / aaCap, -aaNorm, aaNorm);
            DebugUtils.CSV($"AxisAttitudeCascade-{name}.csv",
                Time.timeSinceLevelLoad,
                angleError,
                angularVelocity,
                avError,
                avPID.Action); //debug
            return avPID.Action;
        }
    }
}
