using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using AT_Utils;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        [KSPField] public string BarrelAttachmentTransform = "BarrelAttachment";
        [KSPField] public string SegmentTransform = "BarrelSegment";
        [KSPField] public string SegmentSensorTransform = "BarrelSegmentSensor";
        [KSPField] public string ScaffoldTransform = "BarrelScaffold";
        [KSPField] public string NextSegmentTransform = "NextSegment";

        [KSPField(guiActive = true,
            guiActiveEditor = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Segment Mass",
            guiUnits = "t",
            guiFormat = "F1")]
        public float SegmentMass;

        /// <summary>
        /// The cost of a segment as calculated from construction recipe
        /// </summary>
        private float segmentCost;

        [KSPField] public Vector3 SegmentCoM;

        [KSPField(isPersistant = true,
            guiActiveEditor = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Segments")]
        [UI_FloatRange(scene = UI_Scene.All, minValue = 0, maxValue = 20, stepIncrement = 1)]
        public float numSegments;

        [KSPField] public int MaxSegments = 20;

        public Transform barrelAttachmentTransform;
        public GameObject barrelSegmentPrefab;
        public GameObject segmentScaffoldPrefab;
        public GameObject segmentScaffold;

        private struct BarrelSegment
        {
            public GameObject segmentGO;
            public Transform segmentSensor;
        }

        private readonly List<BarrelSegment> barrelSegments = new List<BarrelSegment>();

        private void segmentsInfo(StringBuilder info)
        {
            UpdateSegmentCost();
            info.AppendLine($"Max. segments: {MaxSegments}");
            info.AppendLine($"Segment mass: {Utils.formatBigValue(SegmentMass, "t")}");
        }

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

        private void onNumSegmentsChange(object value)
        {
            if(State == AcceleratorState.IDLE)
            {
                if(updateSegments((int)numSegments))
                {
                    UpdateParams();
                    return;
                }
            }
            numSegments = barrelSegments.Count;
        }

        private Transform getAttachmentTransform()
        {
            Transform attachmentPoint;
            var lastSegmentIdx = barrelSegments.Count - 1;
            if(lastSegmentIdx < 0)
                attachmentPoint = barrelAttachmentTransform;
            else
            {
                var prevSegment = barrelSegments[lastSegmentIdx];
                attachmentPoint =
                    Part.FindHeirarchyTransform(prevSegment.segmentGO.transform,
                        NextSegmentTransform);
            }
            if(attachmentPoint == null)
                this.Log("ERROR: Unable to find attachment point transform");
            return attachmentPoint;
        }

        private static FieldInfo rendererlistscreated_FI = typeof(Part).GetField("rendererlistscreated",
            BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.NonPublic);

        private void resetRendererCaches()
        {
            rendererlistscreated_FI.SetValue(part, false);
            part.ResetModelSkinnedMeshRenderersCache();
            part.ResetModelMeshRenderersCache();
            part.ResetModelRenderersCache();
        }

        private bool updateSegments(int newSegments)
        {
            var haveSegments = barrelSegments.Count;
            if(newSegments == haveSegments)
                return true;
            if(newSegments > haveSegments)
            {
                for(var i = haveSegments; i < newSegments; i++)
                {
                    var attachmentPoint = getAttachmentTransform();
                    if(attachmentPoint == null)
                        return false;
                    var newSegment = Instantiate(barrelSegmentPrefab, attachmentPoint, false);
                    if(newSegment == null)
                    {
                        this.Error($"Unable to instantiate {barrelSegmentPrefab.GetID()}");
                        return false;
                    }
                    var newSegmentSensor =
                        Part.FindHeirarchyTransform(newSegment.transform,
                            SegmentSensorTransform);
                    if(newSegmentSensor == null)
                    {
                        this.Error(
                            $"Unable to find {SegmentSensorTransform} transform in {barrelSegmentPrefab.GetID()}");
                        Destroy(newSegment);
                        return false;
                    }
                    newSegment.transform.localPosition = Vector3.zero;
                    newSegment.transform.localRotation = Quaternion.identity;
                    if(!launchingDamper.AddDamperExtension(newSegmentSensor))
                    {
                        this.Error($"Unable to add damper extension to {newSegmentSensor.GetID()}");
                        Destroy(newSegment);
                        return false;
                    }
                    attachmentPoint.gameObject.SetActive(true);
                    newSegment.SetActive(true);
                    barrelSegments.Add(new BarrelSegment { segmentGO = newSegment, segmentSensor = newSegmentSensor });
                }
            }
            else
            {
                for(var i = haveSegments - 1; i >= newSegments; i--)
                {
                    var segment = barrelSegments[i];
                    launchingDamper.RemoveDamperExtension(segment.segmentSensor);
                    Destroy(segment.segmentGO);
                    barrelSegments.RemoveAt(i);
                }
            }
            numSegments = barrelSegments.Count;
            resetRendererCaches();
            StartCoroutine(delayedUpdateRCS());
            return true;
        }

        private bool updateScaffold(float newDeploymentProgress)
        {
            if(newDeploymentProgress < 0)
            {
                if(segmentScaffold != null)
                {
                    if(!removeDockingNode())
                        return false;
                    if(HighLogic.LoadedSceneIsFlight)
                        FXMonger.Explode(part, segmentScaffold.transform.position, 0);
                    Destroy(segmentScaffold);
                    segmentScaffold = null;
                    resetRendererCaches();
                }
            }
            else
            {
                if(segmentScaffold == null)
                {
                    this.Debug($"Creating new scaffold");
                    var attachmentPoint = getAttachmentTransform();
                    if(attachmentPoint == null)
                        return false;
                    segmentScaffold = Instantiate(segmentScaffoldPrefab, attachmentPoint, false);
                    segmentScaffold.transform.localPosition = Vector3.zero;
                    segmentScaffold.transform.localRotation = Quaternion.identity;
                    if(!string.IsNullOrEmpty(constructionPortTransformPrefabName))
                    {
                        var dockingNode =
                            Part.FindHeirarchyTransform(segmentScaffold.transform, constructionPortTransformPrefabName);
                        if(dockingNode != null)
                        {
                            dockingNode.gameObject.name = constructionPortTransformName;
                        }
                        else
                            this.Error($"Unable to find {constructionPortTransformPrefabName} transform");
                    }
                    segmentScaffold.SetActive(true);
                    resetRendererCaches();
#if DEBUG
                    this.Debug($"Scaffold instance tree: {DebugUtils.formatTransformTree(segmentScaffold.transform)}");
#endif
                }
                if(newDeploymentProgress < 1 && !removeDockingNode())
                    return false;
                if(newDeploymentProgress >= 1)
                    newDeploymentProgress = 1;
                segmentScaffold.transform.localScale =
                    Vector3.Lerp(ScaffoldStartScale, Vector3.one, newDeploymentProgress);
                segmentScaffold.transform.hasChanged = true;
            }
            DeploymentProgress = newDeploymentProgress;
            if(DeploymentProgress >= 1)
            {
                this.Debug($"Deployment finished. Docking node: {constructionPort.GetID()}");
                setupDockingNode();
            }
            return true;
        }

        private IEnumerator<YieldInstruction> delayedUpdateRCS()
        {
            yield return new WaitForEndOfFrame();
            part.Modules
                .GetModules<ExtensibleRCS>()
                .ForEach(rcs => rcs.UpdateThrusterTransforms());
        }

        private void updateCoMOffset()
        {
            part.CoMOffset = Vector3.zero;
            if(barrelSegments.Count == 0 && ConstructedMass <= 0)
                return;
            part.UpdateMass();
            var ori = part.partTransform.position;
            var CoM = barrelSegments.Aggregate(
                Vector3.zero,
                (current, segment) =>
                    current
                    + (segment.segmentGO.transform.TransformPoint(SegmentCoM) - ori) * SegmentMass);
            if(ConstructedMass > 0)
            {
                var attachmentPoint = getAttachmentTransform();
                CoM += (attachmentPoint.TransformPoint(SegmentCoM) - ori) * (float)ConstructedMass;
            }
            part.CoMOffset = part.partTransform.InverseTransformDirection(CoM / part.mass);
        }

        private Coroutine inertiaTensorUpdater;

        private IEnumerator<YieldInstruction> updateInertiaTensor()
        {
            yield return new WaitForFixedUpdate();
            part.UpdateInertiaTensor();
            inertiaTensorUpdater = null;
        }

        private void delayedUpdateInertiaTensor()
        {
            if(inertiaTensorUpdater == null)
                inertiaTensorUpdater = StartCoroutine(updateInertiaTensor());
        }

        private void updateVesselSize()
        {
            if(vessel == null)
                return;
            vesselSize = vessel.Bounds().size.magnitude;
            vessel.SetUnpackDistance(vesselSize * 10, true);
        }

        private void updatePhysicsParams()
        {
            updateCoMOffset();
            delayedUpdateInertiaTensor();
#if DEBUG
            StartCoroutine(CallbackUtil.DelayedCallback(3,
                () =>
                {
                    if(vessel != null)
                        VesselMass = vessel.GetTotalMass();
                    else if(HighLogic.LoadedSceneIsEditor)
                        VesselMass = EditorLogic.fetch.ship.GetTotalMass();
                }));
#endif
        }

        public void UpdateSegmentCost()
        {
            segmentCost = constructionRecipe?.CostPerMass * SegmentMass ?? 0;
        }

        public void UpdateParams()
        {
            updateVesselSize();
            updatePhysicsParams();
            if(HighLogic.LoadedSceneIsEditor)
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }
    }
}
