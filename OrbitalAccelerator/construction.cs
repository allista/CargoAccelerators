using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using AT_Utils;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        public enum ConstructionState
        {
            IDLE,
            DEPLOYING,
            PAUSE,
            CONSTRUCTING,
            FINISHED,
            ABORTED
        }

        [SerializeField] public ConstructionRecipe constructionRecipe;

        [KSPField] public Vector3 ScaffoldStartScale = new Vector3(1, 1, 0.01f);
        [KSPField] public float ScaffoldDeployTime = 600f;
        [KSPField] public float SpecialistMinWorkforce = 0.5f;

        [KSPField(isPersistant = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Construction Controls",
            guiActive = true,
            guiActiveUnfocused = true,
            guiActiveEditor = true,
            unfocusedRange = 500)]
        [UI_Toggle(scene = UI_Scene.All, enabledText = "Enabled", disabledText = "Disabled")]
        public bool ShowConstructionUI;

        [field: KSPField(isPersistant = true)]
        public ConstructionState cState { get; private set; } = ConstructionState.IDLE;

        [field: KSPField(isPersistant = true)] public float DeploymentProgress { get; private set; } = -1;
        [field: KSPField(isPersistant = true)] public double ConstructedMass { get; private set; }

        [KSPField(isPersistant = true)] private double trashMass;

        public float Workforce { get; private set; }
        private double lastConstructionUT = -1;
        private const double maxTimeStep = 3600.0;

        public bool CanConstruct =>
            (State == AcceleratorState.IDLE || State == AcceleratorState.UNDER_CONSTRUCTION)
            && cState != ConstructionState.FINISHED
            && cState != ConstructionState.ABORTED;

        public bool CanAbortConstruction => cState != ConstructionState.IDLE;

        public double GetRemainingConstructionTime() =>
            constructionRecipe != null && constructionRecipe.MassProduction > 0 && Workforce > 0
                ? (SegmentMass - ConstructedMass) / constructionRecipe.MassProduction / Workforce
                : double.NaN;

        private void showConstructionUI(object value)
        {
            if(ShowConstructionUI)
                cUI.Show(this);
            else
                cUI.Close();
        }

        private void constructionInfo(StringBuilder info)
        {
            if(constructionRecipe == null)
                return;
            info.AppendLine($"Segment construction:");
            info.Append(constructionRecipe.GetInfo(SegmentMass));
            var maxConstructionTime = SegmentMass / constructionRecipe.MassProduction / SpecialistMinWorkforce;
            info.AppendLine(
                $"- Max. time: {Utils.formatTimeDelta(maxConstructionTime)}");
        }

        private void updateWorkforce() =>
            Workforce = vessel != null
                ? ConstructionUtils.VesselWorkforce<ConstructionSkill>(vessel, SpecialistMinWorkforce)
                : 0;

        private void loadConstructionState(ConfigNode node)
        {
            // node.TryGetValue(nameof(constructedMass), ref constructedMass);
            if(constructionRecipe != null)
                return;
            var recipeNode = node.GetNode(nameof(constructionRecipe));
            if(recipeNode != null)
                constructionRecipe = ConfigNodeObject.FromConfig<ConstructionRecipe>(recipeNode);
            else
                this.Error($"Unable to find {nameof(constructionRecipe)} node in: {node}");
        }

        private void saveConstructionState(ConfigNode node)
        {
            // node.AddValue(nameof(constructedMass), constructedMass);
        }

        private void fixConstructionState()
        {
            this.Debug($"deployment {DeploymentProgress}, State {State}, C.State {cState}");
            if(DeploymentProgress < 0)
                return;
            if(State != AcceleratorState.UNDER_CONSTRUCTION)
                changeState(AcceleratorState.UNDER_CONSTRUCTION);
            if(cState == ConstructionState.IDLE)
                cState = ConstructionState.PAUSE;
            this.Debug($"State {State}, C.State {cState}");
        }

        public void StartConstruction()
        {
            if(constructionRecipe == null)
                return;
            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch(State)
            {
                case AcceleratorState.IDLE when cState == ConstructionState.IDLE:
                    UI.ClearMessages();
                    DeploymentProgress = 0;
                    ConstructedMass = 0;
                    lastConstructionUT = -1;
                    cState = ConstructionState.DEPLOYING;
                    changeState(AcceleratorState.UNDER_CONSTRUCTION);
                    updateWorkforce();
                    return;
                case AcceleratorState.UNDER_CONSTRUCTION when cState == ConstructionState.PAUSE:
                    cState = DeploymentProgress >= 0 && DeploymentProgress < 1
                        ? ConstructionState.DEPLOYING
                        : ConstructedMass < SegmentMass
                            ? ConstructionState.CONSTRUCTING
                            : ConstructionState.FINISHED;
                    updateWorkforce();
                    return;
                default:
                    return;
            }
        }

        public void StopConstruction(string message = null)
        {
            cState = ConstructedMass < SegmentMass
                ? ConstructionState.PAUSE
                : ConstructionState.FINISHED;
            TimeWarp.SetRate(0, false);
            if(!string.IsNullOrEmpty(message))
                Utils.Message(message);
        }

        public void AbortConstruction()
        {
            switch(cState)
            {
                case ConstructionState.IDLE:
                    break;
                case ConstructionState.DEPLOYING:
                    cState = ConstructionState.ABORTED;
                    break;
                case ConstructionState.PAUSE:
                case ConstructionState.CONSTRUCTING:
                    if(ConstructedMass > 0)
                    {
                        constructionRecipe?.ReturnMass(part, ConstructedMass, GLB.RecyclingRatio);
                        trashMass = 0;
                        ConstructedMass = 0;
                        UpdateParams();
                    }
                    cState = ConstructionState.ABORTED;
                    break;
                case ConstructionState.FINISHED:
                    break;
                case ConstructionState.ABORTED:
                    if(updateScaffold(-1))
                    {
                        DeploymentProgress = -1;
                        cState = ConstructionState.IDLE;
                        changeState(AcceleratorState.IDLE);
                        updateVesselSize();
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void constructionUpdate()
        {
            switch(cState)
            {
                case ConstructionState.IDLE:
                    break;
                case ConstructionState.DEPLOYING:
                    if(DeploymentProgress < 1)
                    {
                        updateScaffold(DeploymentProgress + TimeWarp.deltaTime / ScaffoldDeployTime);
                        updateVesselSize();
                        break;
                    }
                    cState = ConstructionState.PAUSE;
                    break;
                case ConstructionState.PAUSE:
                    break;
                case ConstructionState.CONSTRUCTING:
                    break;
                case ConstructionState.FINISHED:
                    if(!updateScaffold(-1))
                    {
                        cState = ConstructionState.PAUSE;
                        break;
                    }
                    if(!updateSegments((int)numSegments + 1))
                    {
                        updateScaffold(1);
                        cState = ConstructionState.PAUSE;
                        break;
                    }
                    trashMass = 0;
                    ConstructedMass = 0;
                    DeploymentProgress = -1;
                    cState = ConstructionState.IDLE;
                    changeState(AcceleratorState.IDLE);
                    UpdateParams();
                    break;
                case ConstructionState.ABORTED:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void constructionFixedUpdate()
        {
            if(cState != ConstructionState.CONSTRUCTING)
                return;
            if(ConstructedMass >= SegmentMass)
                StopConstruction("Segment construction finished");
            if(constructionRecipe == null)
            {
                StopConstruction("No construction blueprints are present");
                return;
            }
            if(Workforce <= 0)
            {
                StopConstruction("No qualified workers are present");
                return;
            }
            var dT = ConstructionUtils.GetDeltaTime(ref lastConstructionUT);
            if(dT <= 0)
                return;
            while(dT > 0 && ConstructedMass < SegmentMass)
            {
                var chunk = Math.Min(dT, maxTimeStep);
                var work = Math.Min(Workforce * chunk, constructionRecipe.RequiredWork(SegmentMass - ConstructedMass));
                var constructed = constructionRecipe.ProduceMass(part, work, out var trash);
                trashMass += trash;
                if(constructed <= 0)
                {
                    StopConstruction("Segment construction paused.\nNo enough resources.");
                    break;
                }
                ConstructedMass += constructed;
                dT -= chunk;
            }
            updatePhysicsParams();
        }
    }

    [SuppressMessage("ReSharper", "ConvertToConstant.Global"),
     SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global"),
     SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public class RecipeComponent : ResourceInfo
    {
        /// <summary>
        /// If the resource has density > 0 and UseUnits == false, then this is
        /// the mass of the resource required per mass of the output.
        /// Otherwise this is the amount of units of the resource per mass of the output.
        /// </summary>
        [Persistent]
        public float UsePerMass = 1;

        /// <summary>
        /// If true, forces the use of the resource by units rather than by mass.
        /// Ignored for mass-less resources.
        /// </summary>
        [Persistent]
        public bool UseUnits = false;

        /// <summary>
        /// Pre-calculated value of the usage of this resource in units per 1 t of construction mass.
        /// </summary>
        public float UnitsPerMass { get; private set; }

        public override void Load(ConfigNode node)
        {
            base.Load(node);
            UnitsPerMass = UsePerMass;
            if(!UseUnits && def.density > 0)
                UnitsPerMass /= def.density;
        }

        public string GetInfo(float forMass = 1) =>
            $"{def.displayName}: {Utils.formatBigValue(UnitsPerMass * forMass, "u")}";
    }

    [SuppressMessage("ReSharper", "ConvertToConstant.Global"),
     SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global"),
     SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public class ConstructionRecipe : ConfigNodeObject
    {
        /// <summary>
        /// Production of the construction mass in tons per second.
        /// </summary>
        [Persistent]
        public float MassProduction = 1;

        /// <summary>
        /// If any of the resources in the recipe is returned by RequestResource
        /// in the amount less than this part of the demand, the construction stops.
        /// </summary>
        [Persistent]
        public float ShutdownThreshold = 0.99f;

        /// <summary>
        /// The recipe for the production of the construction mass.
        /// </summary>
        [Persistent]
        [SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
        public PersistentList<RecipeComponent> Inputs = new PersistentList<RecipeComponent>();

        public float CostPerMass { get; private set; }

        public override void Load(ConfigNode node)
        {
            base.Load(node);
            var cost = 0f;
            foreach(var r in Inputs)
            {
                if(r.def.unitCost <= 0)
                    continue;
                cost += r.UnitsPerMass * r.def.unitCost;
            }
            CostPerMass = cost;
        }

        public double RequiredWork(double massToProduce) => massToProduce / MassProduction;

        public string GetInfo(float forMass = 1)
        {
            var info = StringBuilderCache.Acquire();
            foreach(var r in Inputs)
                info.AppendLine($"- {r.GetInfo(forMass)}");
            info.AppendLine($"- Total cost: {Utils.formatBigValue(CostPerMass * forMass, "£")}");
            return info.ToStringAndRelease().Trim();
        }

        public IEnumerable<CA.UI.ResourceInfo> GetResourceInfos(float forMass)
        {
            foreach(var r in Inputs)
            {
                yield return new CA.UI.ResourceInfo
                {
                    name = r.def.displayName, unit = "u", amount = r.UnitsPerMass * forMass
                };
            }
            yield return new CA.UI.ResourceInfo { name = "Total cost", unit = "£", amount = CostPerMass * forMass };
        }

        public double ProduceMass(Part fromPart, double work, out double trashMass)
        {
            var success = true;
            var constructedMass = work * MassProduction;
            var usedMass = 0.0;
            foreach(var r in Inputs)
            {
                var demand = r.UnitsPerMass * constructedMass;
                var consumed = fromPart.RequestResource(r.id, demand);
                if(r.def.density > 0)
                    usedMass += consumed * r.def.density;
                if(consumed / demand > ShutdownThreshold)
                    continue;
                success = false;
                break;
            }
            if(success)
            {
                if(constructedMass > usedMass)
                {
                    constructedMass = usedMass;
                    trashMass = 0;
                }
                else
                {
                    trashMass = usedMass - constructedMass;
                }
                return constructedMass;
            }
            trashMass = usedMass;
            return 0;
        }

        public void ReturnMass(Part toPart, double mass, double ratio)
        {
            var massToReturn = mass;
            var chunk = massToReturn / 10.0;
            var success = true;
            while(success && massToReturn > 0)
            {
                chunk = Math.Min(chunk, massToReturn);
                foreach(var r in Inputs)
                {
                    var demand = r.UnitsPerMass * chunk;
                    if(r.def.density > 0)
                        demand *= ratio;
                    var consumed = toPart.RequestResource(r.id, -demand);
                    if(consumed / demand <= ShutdownThreshold)
                        success = false;
                }
                massToReturn -= chunk;
            }
        }
    }
}
