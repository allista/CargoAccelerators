using AT_Utils;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        [KSPField(isPersistant = true,
            guiName = "Accelerator Controls",
            guiActive = true,
            guiActiveEditor = true,
            guiActiveUnfocused = true,
            unfocusedRange = 50)]
        [UI_Toggle(scene = UI_Scene.Flight, enabledText = "Enabled", disabledText = "Disabled")]
        public bool ShowUI;

        private void showUI(object value)
        {
            if(ShowUI)
                UI.Show(this);
            else
                UI.Close();
        }

        public void AcquirePayload()
        {
            if(State != AcceleratorState.LOADED)
                return;
            UI.ClearMessages();
            changeState(AcceleratorState.ACQUIRE_PAYLOAD);
        }

        public void EjectPayload()
        {
            if(State != AcceleratorState.LOADED)
                return;
            UI.ClearMessages();
            changeState(AcceleratorState.EJECT);
        }

        public void AbortOperations()
        {
            switch(State)
            {
                case AcceleratorState.EJECT:
                    UI.ClearMessages();
                    changeState(AcceleratorState.IDLE);
                    break;
                case AcceleratorState.LAUNCH:
                    UI.ClearMessages();
                    changeState(AcceleratorState.ABORT);
                    break;
            }
        }

        public void ToggleAutoAlign(bool enable)
        {
            AutoAlignEnabled = enable;
            axisController.Reset();
            if(UI.IsShown)
                UI.Controller.autoAlignToggle.SetIsOnWithoutNotify(enable);
            if(!enable)
                return;
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            vessel.Parts.ForEach(p =>
                p.SendMessage("DisableAttitudeControl",
                    this,
                    SendMessageOptions.DontRequireReceiver));
        }

        public void LaunchPayload()
        {
            if(State != AcceleratorState.LOADED
               || launchParams == null
               || !launchParams.Valid)
                return;
            UI.ClearMessages();
            changeState(AcceleratorState.LAUNCH);
        }

        private bool startConstruction()
        {
            if(State != AcceleratorState.IDLE)
                return false;
            UI.ClearMessages();
            deploymentProgress = 0;
            constructionProgress = -1;
            changeState(AcceleratorState.DEPLOY_SCAFFOLD);
            return true;
        }

#if DEBUG
        [KSPField(guiActive = true,
            guiActiveEditor = true,
            guiName = "Vessel Mass",
            guiUnits = "t",
            guiFormat = "F1")]
        public float VesselMass;

        [KSPField(isPersistant = true,
            guiName = "Build next segment",
            guiActive = true,
            guiActiveEditor = true,
            guiActiveUnfocused = true,
            unfocusedRange = 50)]
        [UI_Toggle(scene = UI_Scene.Flight, enabledText = "Constructing", disabledText = "Operational")]
        public bool BuildSegment;
        
        [KSPField(isPersistant = true,
            guiName = "Allow construction",
            guiActive = true,
            guiActiveEditor = true,
            guiActiveUnfocused = true,
            unfocusedRange = 50)]
        [UI_Toggle(scene = UI_Scene.Flight, enabledText = "Constructing", disabledText = "Waiting")]
        public bool AllowConstruction;

        [KSPEvent(active = true, guiActive = true, guiName = "Reload CA Globals")]
        public void ReloadGlobals()
        {
            Globals.Load();
            axisController.InitPIDs();
        }

        private void OnRenderObject()
        {
            Utils.GLDrawPoint(part.partTransform.TransformPoint(part.CoMOffset), Color.green);
        }
#endif
    }
}
