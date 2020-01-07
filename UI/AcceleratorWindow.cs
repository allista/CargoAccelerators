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
            Controller.closeButton.onClick.AddListener(close);
            Controller.acquirePayloadButton.onClick.AddListener(accelerator.AcquirePayload);
            Controller.ejectPayloadButton.onClick.AddListener(accelerator.EjectPayload);
            Controller.abortButton.onClick.AddListener(accelerator.AbortOperations);
            Controller.autoAlignToggle.onValueChanged.AddListener(accelerator.ToggleAutoAlign);
            Controller.launchButton.onClick.AddListener(accelerator.LaunchPayload);
            Controller.title.text = accelerator.Title();
            Controller.messagePanelController.onLabelClicked.AddListener(ClearMessages);
            UpdatePayloadInfo();
            updateMessage();
        }

        private void close()
        {
            if(accelerator != null)
                accelerator.Fields.SetValue(nameof(OrbitalAccelerator.ShowUI), false);
            else
                Close();
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
                    (float)launchParams.duration);
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
