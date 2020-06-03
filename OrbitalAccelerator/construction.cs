using System;
using System.Diagnostics.CodeAnalysis;
using AT_Utils;
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

        [KSPField(isPersistant = true,
            guiName = "Build next segment",
            guiActive = true,
            guiActiveEditor = true,
            guiActiveUnfocused = true,
            unfocusedRange = 50)]
        [UI_Toggle(scene = UI_Scene.Flight, enabledText = "Constructing", disabledText = "Idle")]
        public bool BuildSegment;

        [KSPField(isPersistant = true, guiActive = true, guiName = "Construction")]
        private ConstructionState constructionState = ConstructionState.IDLE;

        [KSPField(isPersistant = true)] private float deploymentProgress = -1;
        [KSPField(isPersistant = true)] private double constructedMass;

        private float workforce;
        private double lastConstructionUT = -1;
        private const double maxTimeStep = 3600.0;

        private void updateWorkforce() =>
            workforce = vessel != null ? ConstructionUtils.VesselWorkforce<ConstructionSkill>(vessel, 0.5f) : 0;

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
                    break;
                case ConstructionState.FINISHED:
                    BuildSegment = false;
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
                    constructedMass = 0;
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
                var constructed = constructionRecipe.ProduceMass(part, work);
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
     SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global")]
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
    }

    [SuppressMessage("ReSharper", "ConvertToConstant.Global"),
     SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global"),
     SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public class ConstructionRecipe : ConfigNodeObject
    {
        [Persistent] public float MassProduction = 1;
        [Persistent] public float ShutdownThreshold = 0.99f;

        // ReSharper disable once CollectionNeverUpdated.Global
        [Persistent] public PersistentList<RecipeComponent> Inputs = new PersistentList<RecipeComponent>();


        public double RequiredWork(double massToProduce) => massToProduce / MassProduction;

        public double ProduceMass(Part fromPart, double work)
        {
            var success = true;
            var constructedMass = work * MassProduction;
            foreach(var r in Inputs)
            {
                var demand = r.UsePerMass * constructedMass;
                if(!r.UseUnits && r.def.density > 0)
                    demand /= r.def.density;
                var consumed = fromPart.RequestResource(r.id, demand);
                if(consumed / demand > ShutdownThreshold)
                    continue;
                success = false;
                break;
            }
            return success ? constructedMass : 0;
        }
    }
}
