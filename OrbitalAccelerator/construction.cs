using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using AT_Utils;
using JetBrains.Annotations;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        private enum ConstructionState
        {
            IDLE,
            DEPLOYING,
            PAUSE,
            CONSTRUCTING,
            FINISHED,
        }

        [SerializeField] public ConstructionRecipe constructionRecipe;

        [KSPField] public Vector3 ScaffoldStartScale = new Vector3(1, 1, 0.01f);
        [KSPField] public float ScaffoldDeployTime = 600f;
        [KSPField] public float SpecialistMinWorkforce = 0.5f;

        [KSPField(isPersistant = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Build next segment",
            guiActive = true,
            guiActiveUnfocused = true,
            unfocusedRange = 500)]
        [UI_Toggle(scene = UI_Scene.Flight, enabledText = "Constructing", disabledText = "Off")]
        public bool BuildSegment;

        [UsedImplicitly]
        [KSPField(guiActive = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Construction Progress",
            guiFormat = "P1",
            guiActiveUnfocused = true,
            unfocusedRange = 500)]
        public float ConstructionProgress;

        [KSPField(isPersistant = true,
            guiActive = true,
            guiName = "Construction",
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiActiveUnfocused = true,
            unfocusedRange = 500)]
        private ConstructionState constructionState = ConstructionState.IDLE;

        [KSPField(isPersistant = true)] private float deploymentProgress = -1;
        [KSPField(isPersistant = true)] private double constructedMass;
        [KSPField(isPersistant = true)] private double trashMass;

        private float workforce;
        private double lastConstructionUT = -1;
        private const double maxTimeStep = 3600.0;

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
            workforce = vessel != null
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
            ConstructionProgress = (float)constructedMass / SegmentMass;
        }

        private void saveConstructionState(ConfigNode node)
        {
            // node.AddValue(nameof(constructedMass), constructedMass);
        }

        private void fixConstructionState()
        {
            this.Debug($"deployment {deploymentProgress}, State {State}, C.State {constructionState}");
            if(deploymentProgress < 0)
                return;
            if(State != AcceleratorState.UNDER_CONSTRUCTION)
                changeState(AcceleratorState.UNDER_CONSTRUCTION);
            if(deploymentProgress < 1)
                constructionState = ConstructionState.DEPLOYING;
            else if(constructionState == ConstructionState.IDLE)
                constructionState = ConstructionState.PAUSE;
            this.Debug($"State {State}, C.State {constructionState}");
        }

        private void onBuildSegmentChange(object value)
        {
            startConstruction();
        }

        private void startConstruction()
        {
            BuildSegment = false;
            if(constructionRecipe == null)
                return;
            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch(State)
            {
                case AcceleratorState.IDLE when constructionState == ConstructionState.IDLE:
                    UI.ClearMessages();
                    BuildSegment = true;
                    deploymentProgress = 0;
                    constructedMass = 0;
                    lastConstructionUT = -1;
                    ConstructionProgress = 0;
                    constructionState = ConstructionState.DEPLOYING;
                    changeState(AcceleratorState.UNDER_CONSTRUCTION);
                    updateWorkforce();
                    return;
                case AcceleratorState.UNDER_CONSTRUCTION when constructionState == ConstructionState.PAUSE:
                    constructionState = constructedMass < SegmentMass
                        ? ConstructionState.CONSTRUCTING
                        : ConstructionState.FINISHED;
                    updateWorkforce();
                    return;
                default:
                    return;
            }
        }

        private void stopConstruction(string message = null)
        {
            constructionState = constructedMass < SegmentMass
                ? ConstructionState.PAUSE
                : ConstructionState.FINISHED;
            TimeWarp.SetRate(0, false);
            if(!string.IsNullOrEmpty(message))
                Utils.Message(message);
        }

        private void constructionUpdate()
        {
            switch(constructionState)
            {
                case ConstructionState.IDLE:
                    BuildSegment = false;
                    break;
                case ConstructionState.DEPLOYING:
                    BuildSegment = true;
                    if(deploymentProgress < 1)
                    {
                        updateScaffold(deploymentProgress + TimeWarp.deltaTime / ScaffoldDeployTime);
                        updateVesselSize();
                        break;
                    }
                    constructionState = ConstructionState.PAUSE;
                    break;
                case ConstructionState.PAUSE:
                    BuildSegment = false;
                    break;
                case ConstructionState.CONSTRUCTING:
                    BuildSegment = true;
                    ConstructionProgress = (float)constructedMass / SegmentMass;
                    break;
                case ConstructionState.FINISHED:
                    BuildSegment = false;
                    ConstructionProgress = 1;
                    if(!updateScaffold(-1))
                    {
                        constructionState = ConstructionState.PAUSE;
                        break;
                    }
                    if(!updateSegments((int)numSegments + 1))
                    {
                        updateScaffold(1);
                        constructionState = ConstructionState.PAUSE;
                        break;
                    }
                    trashMass = 0;
                    constructedMass = 0;
                    ConstructionProgress = 0;
                    constructionState = ConstructionState.IDLE;
                    changeState(AcceleratorState.IDLE);
                    UpdateParams();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void constructionFixedUpdate()
        {
            if(constructionState != ConstructionState.CONSTRUCTING)
                return;
            if(constructedMass >= SegmentMass)
                stopConstruction("Segment construction finished");
            if(constructionRecipe == null)
            {
                stopConstruction("No construction blueprints are present");
                return;
            }
            if(workforce <= 0)
            {
                stopConstruction("No qualified workers are present");
                return;
            }
            var dT = ConstructionUtils.GetDeltaTime(ref lastConstructionUT);
            if(dT <= 0)
                return;
            while(dT > 0 && constructedMass < SegmentMass)
            {
                var chunk = Math.Min(dT, maxTimeStep);
                var work = Math.Min(workforce * chunk, constructionRecipe.RequiredWork(SegmentMass - constructedMass));
                var constructed = constructionRecipe.ProduceMass(part, work, out var trash);
                trashMass += trash;
                if(constructed <= 0)
                {
                    stopConstruction("Segment construction paused.\nNo enough resources.");
                    break;
                }
                constructedMass += constructed;
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
        // ReSharper disable once CollectionNeverUpdated.Global
        [Persistent]
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
    }
}
