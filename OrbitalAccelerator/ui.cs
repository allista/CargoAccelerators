using AT_Utils;
using AT_Utils.UI;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        [KSPField(isPersistant = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Accelerator Controls",
            guiActive = true,
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
                    TimeWarp.SetRate(0, false);
                    changeState(AcceleratorState.ABORT);
                    break;
            }
        }

        public void ToggleAutoAlign(bool enable)
        {
            AutoAlignEnabled = enable;
            axisController.Reset();
            if(UI.IsShown)
                UI.Controller.autoAlignToggle.SetIsOnAndColorWithoutNotify(enable);
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

#if DEBUG
        [KSPField(guiActive = true,
            guiActiveEditor = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Vessel Mass",
            guiUnits = "t",
            guiFormat = "F1")]
        public float VesselMass;

        [KSPEvent(active = true,
            guiActive = true,
            groupName = "OrbitalAcceleratorGroup",
            groupDisplayName = "Orbital Accelerator",
            guiName = "Reload CA Globals")]
        public void ReloadGlobals()
        {
            Globals.Load();
            axisController.InitPIDs();
            Fields[nameof(numSegments)].guiActive = GLB.TestingMode;
        }

        private void OnRenderObject()
        {
            Utils.GLDrawPoint(part.partTransform.TransformPoint(part.CoMOffset), Color.green);
        }
#endif
    }
}
