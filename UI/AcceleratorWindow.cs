using System;
using System.Collections.Generic;
using AT_Utils;
using AT_Utils.UI;
using CA.UI;

namespace CargoAccelerators.UI
{
    public class AcceleratorWindow : UIWindowBase<AcceleratorUI>
    {
        private readonly OrbitalAccelerator accelerator;
        private OrbitalAccelerator.LaunchParams launchParams => accelerator.launchParams;
        private readonly List<string> messages = new List<string>();

        public AcceleratorWindow(OrbitalAccelerator accelerator)
            : base(Globals.Instance.AssetBundle)
        {
            this.accelerator = accelerator;
        }

        protected override void init_controller()
        {
            base.init_controller();
            Controller.closeButton.onClick.AddListener(close);
            Controller.colorsButton.onClick.AddListener(toggleColors);
            Controller.acquirePayloadButton.onClick.AddListener(accelerator.AcquirePayload);
            Controller.ejectPayloadButton.onClick.AddListener(accelerator.EjectPayload);
            Controller.abortButton.onClick.AddListener(accelerator.AbortOperations);
            Controller.autoAlignToggle.SetIsOnAndColorWithoutNotify(accelerator.AutoAlignEnabled);
            Controller.autoAlignToggle.onValueChanged.AddListener(accelerator.ToggleAutoAlign);
            Controller.launchButton.onClick.AddListener(accelerator.LaunchPayload);
            Controller.title.text = accelerator.Title();
            Controller.messagePanelController.onLabelClicked.AddListener(ClearMessages);
            UpdatePayloadInfo();
            UpdateState();
            updateMessage();
        }

        private void close()
        {
            if(accelerator != null)
                accelerator.Fields.SetValue(nameof(OrbitalAccelerator.ShowUI), false);
            else
                Close();
        }

        private void toggleColors()
        {
            Controller.ToggleStylesUI();
        }

        private void updateMessage()
        {
            if(Controller == null)
                return;
            if(messages.Count > 0)
            {
                Controller.message.text = string.Join("\n", messages);
                Controller.messagePanel.gameObject.SetActive(true);
#if DEBUG
                Utils.Log(Controller.message.text);
#endif
            }
            else
            {
                Controller.message.text = "";
                Controller.messagePanel.gameObject.SetActive(false);
            }
        }

        public void UpdatePayloadInfo()
        {
            if(Controller == null || accelerator == null)
                return;
            if(launchParams == null)
                Controller.ClearPayloadInfo();
            else
            {
                Controller.payloadName.text = launchParams.payloadTitle;
                Controller.SetManeuverInfo((float)launchParams.nodeDeltaVm,
                    (float)launchParams.acceleration,
                    (float)launchParams.duration,
                    (float)launchParams.energy);
            }
        }

        public void UpdateState()
        {
            if(Controller == null || accelerator == null)
                return;
            var hasPayload = launchParams != null && launchParams.Valid;
            switch(accelerator.State)
            {
                case OrbitalAccelerator.AcceleratorState.UNDER_CONSTRUCTION:
                    Controller.status.text = "Accelerator is under construction";
                    Controller.status.color = Colors.Warning;
                    Controller.acquirePayloadButton.SetInteractable(false);
                    Controller.abortButton.SetInteractable(false);
                    Controller.ejectPayloadButton.SetInteractable(false);
                    Controller.launchButton.SetInteractable(false);
                    break;
                case OrbitalAccelerator.AcceleratorState.IDLE:
                    Controller.status.text = "Accelerator is idle";
                    Controller.status.color = Colors.Neutral;
                    Controller.acquirePayloadButton.SetInteractable(false);
                    Controller.abortButton.SetInteractable(false);
                    Controller.ejectPayloadButton.SetInteractable(false);
                    Controller.launchButton.SetInteractable(false);
                    break;
                case OrbitalAccelerator.AcceleratorState.LOADED:
                    Controller.status.text = hasPayload
                        ? "Connection with the payload established"
                        : "Payload is in the loading area";
                    Controller.status.color = Colors.Warning;
                    Controller.acquirePayloadButton.SetInteractable(true);
                    Controller.abortButton.SetInteractable(false);
                    Controller.ejectPayloadButton.SetInteractable(true);
                    Controller.launchButton.SetInteractable(hasPayload);
                    break;
                case OrbitalAccelerator.AcceleratorState.ACQUIRE_PAYLOAD:
                    Controller.status.text = "Establishing connection with the payload";
                    Controller.status.color = Colors.Neutral;
                    break;
                case OrbitalAccelerator.AcceleratorState.EJECT:
                    Controller.status.text = "Ejecting payload";
                    Controller.status.color = Colors.Warning;
                    Controller.acquirePayloadButton.SetInteractable(false);
                    Controller.abortButton.SetInteractable(true);
                    Controller.ejectPayloadButton.SetInteractable(false);
                    Controller.launchButton.SetInteractable(false);
                    break;
                case OrbitalAccelerator.AcceleratorState.LAUNCH:
                case OrbitalAccelerator.AcceleratorState.FINISH_LAUNCH:
                    Controller.status.text = "Launch in progress";
                    Controller.status.color = Colors.Good;
                    Controller.acquirePayloadButton.SetInteractable(false);
                    Controller.abortButton.SetInteractable(true);
                    Controller.ejectPayloadButton.SetInteractable(false);
                    Controller.launchButton.SetInteractable(false);
                    break;
                case OrbitalAccelerator.AcceleratorState.ABORT:
                    Controller.status.text = "Aborting";
                    Controller.status.color = Colors.Danger;
                    Controller.acquirePayloadButton.SetInteractable(false);
                    Controller.abortButton.SetInteractable(false);
                    Controller.ejectPayloadButton.SetInteractable(false);
                    Controller.launchButton.SetInteractable(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public void AddMessage(string message)
        {
            messages.Add(message);
            updateMessage();
        }

        public void SetMessage(string message)
        {
            messages.Clear();
            messages.Add(message);
            updateMessage();
        }

        public void ClearMessages()
        {
            messages.Clear();
            updateMessage();
        }
    }
}
