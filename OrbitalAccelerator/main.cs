using AT_Utils;
using CargoAccelerators.UI;
using JetBrains.Annotations;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator : SerializableFiledsPartModule, IPartMassModifier, IPartCostModifier,
        ITargetable
    {
        private static Globals GLB => Globals.Instance;

        [KSPField] public string LoadingDamperID = "LoadingDamper";
        [KSPField] public string LaunchingDamperID = "LaunchingDamper";
        [KSPField] public float MaxConnectionDistance = 300;

        [KSPField(isPersistant = true)] public AcceleratorState State = AcceleratorState.IDLE;
        [KSPField(isPersistant = true)] public bool AutoAlignEnabled;

        private AcceleratorWindow UI;
        private ConstructionWindow cUI;
        private AxisAttitudeController axisController;
        public ATMagneticDamper loadingDamper;
        public ExtensibleMagneticDamper launchingDamper;
        private float vesselSize;

        public override string GetInfo()
        {
            var info = StringBuilderCache.Acquire();
            segmentsInfo(info);
            constructionInfo(info);
            return info.ToStringAndRelease().Trim();
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
            Fields[nameof(ShowConstructionUI)].OnValueModified -= showConstructionUI;
            GameEvents.onVesselWasModified.Remove(onVesselWasModified);
            GameEvents.onVesselCrewWasModified.Remove(onVesselCrewWasModified);
            axisController?.Disconnect();
            axisController = null;
            UI?.Close();
            cUI?.Close();
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
            if(!updateSegments((int)numSegments) || !updateScaffold(DeploymentProgress))
                this.ConfigurationInvalid("Unable to initialize dynamic model components");
            // num segments field controls for development
            var numSegmentsField = Fields[nameof(numSegments)];
            numSegmentsField.OnValueModified += onNumSegmentsChange;
            if(numSegmentsField.uiControlEditor is UI_FloatRange numSegmentsControlEditor)
                numSegmentsControlEditor.maxValue = MaxSegments;
            if(numSegmentsField.uiControlFlight is UI_FloatRange numSegmentsControlFlight)
                numSegmentsControlFlight.maxValue = MaxSegments;
            numSegmentsField.guiActive = GLB.TestingMode;
            Fields[nameof(ShowUI)].OnValueModified += showUI;
            Fields[nameof(ShowConstructionUI)].OnValueModified += showConstructionUI;
            axisController = new AxisAttitudeController(this);
            UI = new AcceleratorWindow(this);
            if(ShowUI)
                UI.Show(this);
            cUI = new ConstructionWindow(this);
            if(ShowConstructionUI)
                cUI.Show(this);
            fixConstructionState();
            updateWorkforce();
            UpdateParams();
            UpdateSegmentCost();
        }

        private void onVesselWasModified(Vessel vsl)
        {
            if(axisController == null || vsl != vessel || vsl == null)
                return;
            axisController.UpdateTorqueProviders();
            updateWorkforce();
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
