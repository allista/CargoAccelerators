using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AT_Utils;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        [KSPField(isPersistant = true,
            guiActive = true,
            guiActiveEditor = true,
            guiName = "Segments")]
        [UI_FloatRange(scene = UI_Scene.All, minValue = 0, maxValue = 20, stepIncrement = 1)]
        public float numSegments;

        private struct BarrelSegment
        {
            public GameObject segmentGO;
            public Transform segmentSensor;
        }

        private readonly List<BarrelSegment> barrelSegments = new List<BarrelSegment>();

        private void buildSegment(object value)
        {
            if(!startConstruction())
                BuildSegment = false;
        }

        private void onNumSegmentsChange(object value)
        {
            if(State == AcceleratorState.IDLE)
            {
                numSegments = (int)numSegments;
                if(updateSegments())
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
            part.ResetModelRenderersCache();
        }

        private bool updateSegments()
        {
            var haveSegments = barrelSegments.Count;
            var numSegments_i = (int)numSegments;
            if(numSegments_i == haveSegments)
                return true;
            if(numSegments_i > haveSegments)
            {
                for(var i = haveSegments; i < numSegments_i; i++)
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
                for(var i = haveSegments - 1; i >= numSegments_i; i--)
                {
                    var segment = barrelSegments[i];
                    launchingDamper.RemoveDamperExtension(segment.segmentSensor);
                    Destroy(segment.segmentGO);
                    barrelSegments.RemoveAt(i);
                }
            }
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
                    this.Debug($"Scaffold instance tree: {DebugUtils.formatTransformTree(segmentScaffold.transform)}");
                }
                if(newDeploymentProgress < 1 && !removeDockingNode())
                    return false;
                if(newDeploymentProgress >= 1)
                    newDeploymentProgress = 1;
                segmentScaffold.transform.localScale =
                    Vector3.Lerp(ScaffoldStartScale, Vector3.one, newDeploymentProgress);
                segmentScaffold.transform.hasChanged = true;
            }
            deploymentProgress = newDeploymentProgress;
            if(deploymentProgress >= 1)
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
            if(barrelSegments.Count == 0 && constructionProgress <= 0)
                return;
            part.UpdateMass();
            var ori = part.partTransform.position;
            var CoM = barrelSegments.Aggregate(
                Vector3.zero,
                (current, segment) =>
                    current
                    + (segment.segmentGO.transform.TransformPoint(SegmentCoM) - ori) * SegmentMass);
            if(constructionProgress > 0)
            {
                var attachmentPoint = getAttachmentTransform();
                CoM += (attachmentPoint.TransformPoint(SegmentCoM) - ori)
                       * (SegmentMass * constructionProgress);
            }
            part.CoMOffset = part.partTransform.InverseTransformDirection(CoM / part.mass);
        }

        private static readonly FieldInfo partInertiaTensorFI = typeof(Part).GetField(
            "inertiaTensor",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private Coroutine inertiaTensorUpdater;

        private void updateInertiaTensor()
        {
            if(part.rb != null)
            {
                part.rb.ResetInertiaTensor();
                var inertiaTensor = part.rb.inertiaTensor / Mathf.Max(1f, part.rb.mass);
                partInertiaTensorFI.SetValue(part, inertiaTensor);
            }
            inertiaTensorUpdater = null;
        }

        private void delayedUpdateInertiaTensor()
        {
            if(inertiaTensorUpdater != null)
                return;
            inertiaTensorUpdater = StartCoroutine(CallbackUtil.DelayedCallback(1, updateInertiaTensor));
        }

        private void updateVesselSize()
        {
            if(vessel == null)
                return;
            vesselSize = vessel.Bounds().size.magnitude;
            vessel.SetUnpackDistance(vesselSize * 2);
        }

        private void updatePhysicsParams()
        {
            updateCoMOffset();
            delayedUpdateInertiaTensor();
#if DEBUG
            StartCoroutine(CallbackUtil.DelayedCallback(1,
                () =>
                {
                    if(vessel != null)
                        VesselMass = vessel.GetTotalMass();
                    else if(HighLogic.LoadedSceneIsEditor)
                        VesselMass = EditorLogic.fetch.ship.GetTotalMass();
                }));
#endif
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
