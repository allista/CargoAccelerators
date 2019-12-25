using System;
using System.Collections.Generic;
using AT_Utils;
using UnityEngine;

namespace CargoAccelerators
{
    public enum AcceleratorState { OFF, LOAD, FIRE }

    public class OrbitalAccelerator : PartModule, IPartMassModifier, IPartCostModifier
    {
        [KSPField] public string LoadingDamperID = "LoadingDamper";
        [KSPField] public string LaunchingDamperID = "LaunchingDamper";

        [KSPField] public string BarrelAttachmentTransform = "BarrelAttachment";
        [KSPField] public string SegmentTransform = "BarrelSegment";
        [KSPField] public string SegmentSensorTransform = "BarrelSegmentSensor";
        [KSPField] public string NextSegmentTransform = "NextSegment";

        [KSPField] public float SegmentMass;
        [KSPField] public float SegmentCost;
        [KSPField] public Vector3 SegmentCoM;

        [KSPField(isPersistant = true,
            guiActive = true,
            guiActiveEditor = true,
            guiName = "Segments")]
        [UI_FloatRange(scene = UI_Scene.All, minValue = 0, maxValue = 15, stepIncrement = 1)]
        public float numSegments;

        [KSPField(isPersistant = true)] public AcceleratorState State;

        [KSPField(guiActive = true,
            guiActiveEditor = true,
            guiActiveUnfocused = true,
            unfocusedRange = 100,
            guiName = "Accelerator State")]
        [UI_ChooseOption]
        public string StateChoice = string.Empty;

        public ATMagneticDamper loadingDamper;
        public ExtensibleMagneticDamper launchingDamper;
        public GameObject barrelSegmentPrefab;

        private struct BarrelSegment
        {
            public GameObject segmentGO;
            public Transform segmentSensor;
        }

        private readonly List<BarrelSegment> barrelSegments = new List<BarrelSegment>();

        public override void OnAwake()
        {
            base.OnAwake();
            var T = part.FindModelTransform(SegmentTransform);
            if(T != null)
                T.gameObject.SetActive(false);
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
#if DEBUG
            this.Log(
                $"prefab model tree: {DebugUtils.formatTransformTree(part.partInfo.partPrefab.transform)}"); //debug
#endif
            var T = part.partInfo.partPrefab.FindModelTransform(SegmentTransform);
            if(T == null)
            {
                this.Log($"Unable to find {SegmentTransform} model transform");
                this.EnableModule(false);
                return;
            }
            barrelSegmentPrefab = T.gameObject;
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
            if(!updateSegments())
            {
                this.EnableModule(false);
                return;
            }
            Fields[nameof(numSegments)].OnValueModified += onNumSegmentsChange;
            var states = Enum.GetNames(typeof(AcceleratorState));
            var stateField = Fields[nameof(StateChoice)];
            Utils.SetupChooser(states, states, stateField);
            stateField.OnValueModified += onStateChange;
            updateState();
        }

        private void OnDestroy()
        {
            Fields[nameof(numSegments)].OnValueModified -= onNumSegmentsChange;
            Fields[nameof(StateChoice)].OnValueModified -= onStateChange;
        }

        private void updateState()
        {
            StateChoice = Enum.GetName(typeof(AcceleratorState), State);
            loadingDamper.AttractorEnabled = true;
            loadingDamper.InvertAttractor = false;
            launchingDamper.AttractorEnabled = true;
            launchingDamper.InvertAttractor = true;
            switch(State)
            {
                case AcceleratorState.OFF:
                    loadingDamper.EnableDamper(false);
                    launchingDamper.EnableDamper(false);
                    break;
                case AcceleratorState.LOAD:
                    loadingDamper.EnableDamper(true);
                    launchingDamper.EnableDamper(false);
                    break;
                case AcceleratorState.FIRE:
                    loadingDamper.EnableDamper(false);
                    launchingDamper.Fields.SetValue<float>(nameof(ATMagneticDamper.Attenuation), 0);
                    launchingDamper.EnableDamper(true);
                    break;
                default:
                    goto case AcceleratorState.OFF;
            }
        }

        private void onStateChange(object value)
        {
            if(!Enum.TryParse<AcceleratorState>(StateChoice, out var state))
                return;
            State = state;
            updateState();
        }

        private void onNumSegmentsChange(object value)
        {
            numSegments = (int)numSegments;
            if(!updateSegments())
                Fields.SetValue<float>(nameof(numSegments), barrelSegments.Count);
        }

        private bool updateSegments()
        {
            var haveSegments = barrelSegments.Count;
            var numSegments_i = (int)numSegments;
            this.Log($"updateSegments: have {haveSegments}, need {numSegments_i}"); //debug
            if(numSegments_i == haveSegments)
                return true;
            if(numSegments_i > haveSegments)
            {
                for(var i = haveSegments; i < numSegments_i; i++)
                {
                    Transform attachmentPoint;
                    if(i == 0)
                        attachmentPoint = part.FindModelTransform(BarrelAttachmentTransform);
                    else
                    {
                        var prevSegment = barrelSegments[i - 1];
                        attachmentPoint =
                            Part.FindHeirarchyTransform(prevSegment.segmentGO.transform,
                                NextSegmentTransform);
                    }
                    if(attachmentPoint == null)
                    {
                        this.Log("Unable to find attachment point transform");
                        return false;
                    }
                    var newSegment = Instantiate(barrelSegmentPrefab, attachmentPoint, false);
                    if(newSegment == null)
                    {
                        this.Log($"Unable to instantiate {barrelSegmentPrefab.GetID()}");
                        return false;
                    }
                    var newSegmentSensor =
                        Part.FindHeirarchyTransform(newSegment.transform,
                            SegmentSensorTransform);
                    if(newSegmentSensor == null)
                    {
                        this.Log(
                            $"Unable to find {SegmentSensorTransform} transform in {barrelSegmentPrefab.GetID()}");
                        Destroy(newSegment);
                        return false;
                    }
                    newSegment.transform.localPosition = Vector3.zero;
                    newSegment.transform.localRotation = Quaternion.identity;
                    if(!launchingDamper.AddDamperExtension(newSegmentSensor))
                    {
                        this.Log($"Unable to add damper extension to {newSegmentSensor.GetID()}");
                        Destroy(newSegment);
                        return false;
                    }
                    attachmentPoint.gameObject.SetActive(true);
                    newSegment.SetActive(true);
                    barrelSegments.Add(new BarrelSegment
                    {
                        segmentGO = newSegment, segmentSensor = newSegmentSensor
                    });
                }
            }
            else
            {
                for(var i = haveSegments - 1; i >= numSegments_i; i--)
                {
                    var segment = barrelSegments[i];
                    launchingDamper.RemoveDamperExtension(segment.segmentSensor);
                    Destroy(segment.segmentGO);
                    barrelSegments.RemoveAt(i);
                }
            }
            UpdateParams();
#if DEBUG
            this.Log($"new model tree: {DebugUtils.formatTransformTree(part.transform)}"); //debug
#endif
            return true;
        }

        private void updateCoMOffset()
        {
            part.CoMOffset = Vector3.zero;
            if(barrelSegments.Count == 0)
                return;
            part.UpdateMass();
            var ori = part.partTransform.position;
            var CoM = barrelSegments.Aggregate(
                Vector3.zero,
                (current, segment) =>
                    current
                    + (segment.segmentGO.transform.TransformPoint(SegmentCoM) - ori) * SegmentMass);
            part.CoMOffset = part.partTransform.InverseTransformDirection(CoM / part.mass);
        }

        public void UpdateParams()
        {
            updateCoMOffset();
#if DEBUG
            this.Log("CoM offset: {}", part.CoMOffset);
#endif
        }

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) =>
            barrelSegments.Count * SegmentMass;

        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.CONSTANTLY;

        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) =>
            barrelSegments.Count * SegmentCost;

        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.CONSTANTLY;
    }

    public class OrbitalAcceleratorUpdater : ModuleUpdater<OrbitalAccelerator>
    {
        protected override void on_rescale(ModulePair<OrbitalAccelerator> mp, Scale scale)
        {
            mp.module.SegmentMass = mp.base_module.SegmentMass * scale.absolute.volume;
            mp.module.SegmentCost = mp.base_module.SegmentCost * scale.absolute.volume;
            mp.module.UpdateParams();
        }
    }
}
