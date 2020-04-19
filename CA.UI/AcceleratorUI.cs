using AT_Utils.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CA.UI
{
    public class AcceleratorUI : ScreenBoundRect
    {
        public Button closeButton,
            colorsButton,
            acquirePayloadButton,
            ejectPayloadButton,
            launchButton,
            abortButton;

        public Toggle autoAlignToggle;

        public Text title,
            payloadName,
            countdown,
            deltaV,
            acceleration,
            duration,
            energy,
            attitudeError,
            pitchError,
            yawError,
            maxDeltaV,
            relVelocity,
            angularVelocity;

        public Text displacement,
            message,
            status;

        public RectTransform messagePanel;
        public ClickableLabel messagePanelController;

        protected override void Awake()
        {
            base.Awake();
            title.text = "";
            ClearPayloadInfo();
            message.text = "";
            status.text = "";
            messagePanel.gameObject.SetActive(false);
        }

        public void ClearPayloadInfo()
        {
            payloadName.text = "";
            countdown.text = "";
            deltaV.text = "";
            acceleration.text = "";
            duration.text = "";
            energy.text = "";
            attitudeError.text = "";
            pitchError.text = "";
            yawError.text = "";
            maxDeltaV.text = "";
            relVelocity.text = "";
            angularVelocity.text = "";
            displacement.text = "";
        }

        public void UpdateCountdown(double timeToManeuver)
        {
            countdown.text = timeToManeuver > 0
                ? $"T- {timeToManeuver:F1} s"
                : $"T+ {-timeToManeuver:F1} s";
            if(timeToManeuver > 10)
                countdown.color = Colors.Warning;
            else if(timeToManeuver > 0)
                countdown.color = Colors.Danger;
            else
                countdown.color = Colors.Inactive;
        }

        public void SetManeuverInfo(
            float maneuverDeltaV,
            float maxAcceleration,
            float maneuverDuration,
            float maneuverEnergy
        )
        {
            deltaV.text = $"{FormatUtils.formatBigValue(maneuverDeltaV, "m/s", "F2")}";
            acceleration.text = $"{maxAcceleration / FormatUtils.G0:F2}g";
            duration.text = $"{maneuverDuration:F3}s";
            energy.text = FormatUtils.formatBigValue(maneuverEnergy, "EC");
        }

        public void UpdateAttitudeError(float direct, float pitch, float yaw, bool aligned)
        {
            attitudeError.text = $"{direct:F3}°";
            pitchError.text = $"{pitch:F3}°";
            yawError.text = $"{yaw:F3}°";
            attitudeError.color = aligned ? Colors.Good : Colors.Neutral;
        }

        public void UpdatePayloadInfo(
            float dVmax,
            float relV,
            float relAV,
            float dist,
            bool dVmax_ok,
            bool relV_ok,
            bool relAV_ok,
            bool dist_ok
        )
        {
            maxDeltaV.text = FormatUtils.formatBigValue(dVmax, "m/s");
            relVelocity.text = $"{relV:F3}m/s";
            angularVelocity.text = $"{relAV:F3}°/s";
            displacement.text = $"{dist:F3}m";
            maxDeltaV.color = dVmax_ok ? Colors.Good : Colors.Neutral;
            relVelocity.color = relV_ok ? Colors.Good : Colors.Neutral;
            angularVelocity.color = relAV_ok ? Colors.Good : Colors.Neutral;
            displacement.color = dist_ok ? Colors.Good : Colors.Neutral;
        }
    }
}
