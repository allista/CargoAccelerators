using System;
using AT_Utils;
using AT_Utils.UI;
using CA.UI;

namespace CargoAccelerators.UI
{
    public class ConstructionWindow : UIWindowBase<ConstructionUI>
    {
        private readonly OrbitalAccelerator accelerator;

        public ConstructionWindow(OrbitalAccelerator accelerator)
            : base(Globals.Instance.AssetBundle)
        {
            this.accelerator = accelerator;
        }

        protected override void init_controller()
        {
            base.init_controller();
            Controller.closeButton.onClick.AddListener(close);
            Controller.colorsButton.onClick.AddListener(toggleColors);
            Controller.startStopButton.onClick.AddListener(startStop);
            Controller.title.text = accelerator.Title();
            Update();
        }

        private void close()
        {
            if(accelerator != null)
                accelerator.Fields.SetValue(nameof(OrbitalAccelerator.ShowConstructionUI), false);
            else
                Close();
        }

        private void startStop()
        {
            if(accelerator == null)
                return;
            switch(accelerator.cState)
            {
                case OrbitalAccelerator.ConstructionState.IDLE:
                    DialogFactory.Show("The accelerator cannot operate during construction.",
                        "Attention",
                        () =>
                        {
                            if(accelerator != null)
                                accelerator.StartConstruction();
                        },
                        confirmText: "Start",
                        cancelText: "Cancel",
                        context: this);
                    break;
                case OrbitalAccelerator.ConstructionState.PAUSE:
                    accelerator.StartConstruction();
                    break;
                case OrbitalAccelerator.ConstructionState.FINISHED:
                case OrbitalAccelerator.ConstructionState.ABORTED:
                    break;
                case OrbitalAccelerator.ConstructionState.DEPLOYING:
                case OrbitalAccelerator.ConstructionState.CONSTRUCTING:
                    accelerator.StopConstruction();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public void Update()
        {
            if(Controller == null || accelerator == null || accelerator.constructionRecipe == null)
                return;
            if(accelerator.vessel == null)
            {
                Controller.controlsPane.SetActive(false);
                Controller.constructionPane.SetActive(false);
                Controller.resourcesTitle.text = "Segment construction requirements";
                Controller.UpdateResources(
                    accelerator.constructionRecipe.GetResourceInfos(accelerator.SegmentMass));
            }
            else
            {
                Controller.controlsPane.SetActive(true);
                Controller.constructionPane.SetActive(true);
                Controller.UpdateWorkforce(accelerator.vessel.GetCrewCount(), accelerator.Workforce);
                var rem = accelerator.GetRemainingConstructionTime();
                Controller.remainingTime.text = double.IsNaN(rem) ? "N/A" : Utils.formatTimeDelta(rem);
                Controller.resourcesTitle.text = "Remaining construction requirements";
                Controller.UpdateResources(
                    accelerator.constructionRecipe.GetResourceInfos(accelerator.SegmentMass
                                                                    - (float)accelerator.ConstructedMass));
                switch(accelerator.cState)
                {
                    case OrbitalAccelerator.ConstructionState.IDLE:
                        Controller.state.text = accelerator.State == OrbitalAccelerator.AcceleratorState.IDLE
                            ? "Construction possible"
                            : "Accelerator is in use";
                        Controller.startStopButtonText.text = "Start";
                        Controller.startStopTooltip.text = "Start construction";
                        Controller.startStopColorizer.SetColor(Colors.Good);
                        Controller.stateColorizer.SetColor(Colors.Neutral);
                        Controller.UpdateProgress(-1, 1);
                        break;
                    case OrbitalAccelerator.ConstructionState.DEPLOYING:
                        Controller.state.text = "Deploying scaffold";
                        Controller.startStopButtonText.text = "Pause";
                        Controller.startStopTooltip.text = "Pause deployment";
                        Controller.startStopColorizer.SetColor(Colors.Warning);
                        Controller.stateColorizer.SetColor(Colors.Warning);
                        Controller.UpdateProgress(accelerator.DeploymentProgress, 1);
                        break;
                    case OrbitalAccelerator.ConstructionState.PAUSE:
                        Controller.state.text = accelerator.DeploymentProgress < 1
                            ? "Deployment paused"
                            : "Construction paused";
                        Controller.startStopButtonText.text = "Resume";
                        Controller.startStopTooltip.text = accelerator.DeploymentProgress < 1
                            ? "Resume deployment"
                            : "Resume construction";
                        Controller.startStopColorizer.SetColor(Colors.Good);
                        Controller.stateColorizer.SetColor(Colors.Inactive);
                        if(accelerator.DeploymentProgress < 1)
                            Controller.UpdateProgress(accelerator.DeploymentProgress, 1);
                        else
                            Controller.UpdateProgress(accelerator.ConstructedMass, accelerator.SegmentMass);
                        break;
                    case OrbitalAccelerator.ConstructionState.CONSTRUCTING:
                        Controller.state.text = "Construction in progress";
                        Controller.startStopButtonText.text = "Pause";
                        Controller.startStopTooltip.text = "Pause construction";
                        Controller.startStopColorizer.SetColor(Colors.Warning);
                        Controller.stateColorizer.SetColor(Colors.Good);
                        Controller.UpdateProgress(accelerator.ConstructedMass, accelerator.SegmentMass);
                        break;
                    case OrbitalAccelerator.ConstructionState.FINISHED:
                        Controller.state.text = "Construction complete";
                        Controller.startStopButtonText.text = "Start";
                        Controller.startStopTooltip.text = "Start construction";
                        Controller.stateColorizer.SetColor(Colors.Good);
                        Controller.UpdateProgress(1, 1);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                var noDialogIsOpened = !DialogFactory.ContextIsActive(this);
                Controller.startStopButton.SetInteractable(accelerator.CanConstruct && noDialogIsOpened);
            }
        }
    }
}
