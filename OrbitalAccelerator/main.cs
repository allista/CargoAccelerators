using AT_Utils;
using CargoAccelerators.UI;
using JetBrains.Annotations;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator : SerializableFiledsPartModule, IPartMassModifier, IPartCostModifier,
        ITargetable
    {
        private static Globals GLB => Globals.Instance;

        [KSPField] public string LoadingDamperID = "LoadingDamper";
        [KSPField] public string LaunchingDamperID = "LaunchingDamper";

        [KSPField(isPersistant = true)] public AcceleratorState State = AcceleratorState.IDLE;
        [KSPField(isPersistant = true)] public bool AutoAlignEnabled;

        private AcceleratorWindow UI;
        private AxisAttitudeController axisController;
        public ATMagneticDamper loadingDamper;
        public ExtensibleMagneticDamper launchingDamper;
        private float vesselSize;
        private float launchingAttractorOrigPower;

        private bool findTransforms()
        {
            var success = true;
            var T = part.FindModelTransform(SegmentTransform);
            if(T == null)
            {
                this.Error($"Unable to find {SegmentTransform} model transform");
                success = false;
            }
            barrelSegmentPrefab = T.gameObject;
            barrelSegmentPrefab.SetActive(false);
            T = part.FindModelTransform(BarrelAttachmentTransform);
            if(T == null)
            {
                this.Error($"Unable to find {BarrelAttachmentTransform} model transform");
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
                this.Error($"Unable to find {ScaffoldTransform} model transform");
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
            GameEvents.onVesselCrewWasModified.Add(onVesselCrewWasModified);
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
            GameEvents.onVesselCrewWasModified.Remove(onVesselCrewWasModified);
            axisController?.Disconnect();
            axisController = null;
            UI?.Close();
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            loadDockingPortConfig(node);
            loadConstructionState(node);
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            saveDockingPortState(node);
            saveConstructionState(node);
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
#if DEBUG
            this.Debug(
                $"prefab model tree: {DebugUtils.formatTransformTree(part.partInfo.partPrefab.transform)}");
#endif
            loadingDamper = ATMagneticDamper.GetDamper(part, LoadingDamperID);
            if(loadingDamper == null)
            {
                this.ConfigurationInvalid($"Unable to find loading damper with ID {LoadingDamperID}");
                return;
            }
            launchingDamper =
                ATMagneticDamper.GetDamper(part, LaunchingDamperID) as ExtensibleMagneticDamper;
            if(launchingDamper == null)
            {
                this.ConfigurationInvalid($"Unable to find launching damper with ID {LoadingDamperID}");
                return;
            }
            launchingAttractorOrigPower = launchingDamper.AttractorPower;
            if(!updateSegments((int)numSegments) || !updateScaffold(deploymentProgress))
                this.ConfigurationInvalid("Unable to initialize dynamic model components");
            var numSegmentsField = Fields[nameof(numSegments)];
            numSegmentsField.OnValueModified += onNumSegmentsChange;
            if(numSegmentsField.uiControlEditor is UI_FloatRange numSegmentsControlEditor)
                numSegmentsControlEditor.maxValue = MaxSegments;
            if(numSegmentsField.uiControlFlight is UI_FloatRange numSegmentsControlFlight)
                numSegmentsControlFlight.maxValue = MaxSegments;
            Fields[nameof(ShowUI)].OnValueModified += showUI;
            Fields[nameof(BuildSegment)].OnValueModified += onBuildSegmentChange;
            axisController = new AxisAttitudeController(this);
            UI = new AcceleratorWindow(this);
            if(ShowUI)
                UI.Show(this);
            fixConstructionState();
            updateWorkforce();
            UpdateParams();
        }

        private void onVesselWasModified(Vessel vsl)
        {
            if(axisController == null || vsl != vessel || vsl == null)
                return;
            axisController.UpdateTorqueProviders();
        }

        private void onVesselCrewWasModified(Vessel vsl)
        {
            if(vsl != vessel || vsl == null)
                return;
            updateWorkforce();
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
