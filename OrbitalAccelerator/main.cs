using AT_Utils;
using CargoAccelerators.UI;
using JetBrains.Annotations;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator : PartModule, IPartMassModifier, IPartCostModifier, ITargetable
    {
        private static Globals GLB => Globals.Instance;

        [KSPField] public string LoadingDamperID = "LoadingDamper";
        [KSPField] public string LaunchingDamperID = "LaunchingDamper";

        [KSPField] public string BarrelAttachmentTransform = "BarrelAttachment";
        [KSPField] public string SegmentTransform = "BarrelSegment";
        [KSPField] public string SegmentSensorTransform = "BarrelSegmentSensor";
        [KSPField] public string ScaffoldTransform = "BarrelScaffold";
        [KSPField] public string NextSegmentTransform = "NextSegment";

        [KSPField] public int MaxSegments = 20;
        [KSPField] public float SegmentMass;
        [KSPField] public float SegmentCost;
        [KSPField] public Vector3 SegmentCoM;
        [KSPField] public Vector3 ScaffoldStartScale = new Vector3(1, 1, 0.01f);
        [KSPField] public float ScaffoldDeployTime = 60f;

        [KSPField(isPersistant = true)] public AcceleratorState State = AcceleratorState.IDLE;
        [KSPField(isPersistant = true)] public bool AutoAlignEnabled;
        [KSPField(isPersistant = true)] private float deploymentProgress = -1;
        [KSPField(isPersistant = true)] private float constructionProgress = -1;

        private AcceleratorWindow UI;
        private AxisAttitudeController axisController;
        public ATMagneticDamper loadingDamper;
        public ExtensibleMagneticDamper launchingDamper;
        public Transform barrelAttachmentTransform;
        public GameObject barrelSegmentPrefab;
        public GameObject segmentScaffoldPrefab;
        public GameObject segmentScaffold;
        private float vesselSize;
        private float launchingAttractorOrigPower;

        private bool findTransforms()
        {
            var success = true;
            var T = part.FindModelTransform(SegmentTransform);
            if(T == null)
            {
                this.Log($"Unable to find {SegmentTransform} model transform");
                success = false;
            }
            barrelSegmentPrefab = T.gameObject;
            barrelSegmentPrefab.SetActive(false);
            T = part.FindModelTransform(BarrelAttachmentTransform);
            if(T == null)
            {
                this.Log($"Unable to find {BarrelAttachmentTransform} model transform");
                success = false;
            }
            barrelAttachmentTransform = T;
            T = part.FindModelTransform(ScaffoldTransform);
            if(T != null)
            {
                segmentScaffoldPrefab = T.gameObject;
                segmentScaffoldPrefab.SetActive(false);
            }
            else
                this.Log($"Unable to find {ScaffoldTransform} model transform");
            return success;
        }

        public override void OnAwake()
        {
            base.OnAwake();
            if(!findTransforms())
            {
                this.ConfigurationInvalid("Unable to find all required model components");
                return;
            }
            GameEvents.onVesselWasModified.Add(onVesselWasModified);
        }

        private void OnDestroy()
        {
            if(vessel != null
               && vessel.gameObject.activeInHierarchy
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
        
        public override void OnStart(StartState state)
        {
            base.OnStart(state);
#if DEBUG
            this.Log(
                $"prefab model tree: {DebugUtils.formatTransformTree(part.partInfo.partPrefab.transform)}");
#endif
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
            if(!updateScaffold())
            {
                this.EnableModule(false);
                return;
            }
            UpdateParams();
            var numSegmentsField = Fields[nameof(numSegments)];
            numSegmentsField.OnValueModified += onNumSegmentsChange;
            if(numSegmentsField.uiControlEditor is UI_FloatRange numSegmentsControlEditor)
                numSegmentsControlEditor.maxValue = MaxSegments;
            if(numSegmentsField.uiControlFlight is UI_FloatRange numSegmentsControlFlight)
                numSegmentsControlFlight.maxValue = MaxSegments;
            Fields[nameof(ShowUI)].OnValueModified += showUI;
            Fields[nameof(BuildSegment)].OnValueModified += buildSegment;
            axisController = new AxisAttitudeController(this);
            UI = new AcceleratorWindow(this);
            if(ShowUI)
                UI.Show(this);
        }
        
        private void onVesselWasModified(Vessel vsl)
        {
            if(axisController == null || vsl != vessel || vsl == null)
                return;
            axisController.UpdateTorqueProviders();
        }
        
        /// <summary>
        /// It is a component message handler.
        /// The "DisableAttitudeControl" message is sent from TCA mod when
        /// its own attitude control is enabled.
        /// </summary>
        [UsedImplicitly]
        private void DisableAttitudeControl(object value)
        {
            if(value.Equals(this))
                return;
            ToggleAutoAlign(false);
        }
    }
}
