using System.Diagnostics.CodeAnalysis;
using AT_Utils;

namespace CargoAccelerators
{
    [SuppressMessage("ReSharper", "ConvertToConstant.Global"),
     SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global")]
    public class Globals : PluginGlobals<Globals>
    {
        public readonly UIBundle AssetBundle = UIBundle.Create("CargoAccelerators/ca_ui.ksp");

        [Persistent] public ConstAttitudeController PitchYawController = new ConstAttitudeController();
        [Persistent] public PIDf_Controller2 AvDampingController = new PIDf_Controller2();
        [Persistent] public float USER_INPUT_TOL = 0.01f;

        [Persistent] public float MAX_ATTITUDE_ERROR = 0.05f; //deg
        [Persistent] public float MAX_ANGULAR_VELOCITY_SQR = 0.000010132118f; //0.02 deg/s
        [Persistent] public float MAX_RELATIVE_VELOCITY_SQR = 0.0025f; //0.05 m/s
        [Persistent] public float MAX_DISPLACEMENT = 0.1f; //m
        [Persistent] public float MANEUVER_DELTA_V_TOL = 0.01f;
        [Persistent] public int FINE_TUNE_FRAMES = 3;

        /// <summary>
        /// The part of the half of the maneuver duration within which it is still ok to launch
        /// </summary>
        [Persistent]
        public float LAUNCH_WINDOW = 0.2f;

        [Persistent] public bool TestingMode = false;
    }
}
