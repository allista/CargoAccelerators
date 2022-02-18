using AT_Utils.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CA.UI
{
    public struct ResourceInfo
    {
        public string name;
        public string unit;
        public float amount;
    }

    public class ResourceInfoUI : MonoBehaviour
    {
        public Text resourceLabel,
            resourceAmount;

        public string unit = "u";

        public void UpdateInfo(float amount)
        {
            resourceAmount.text = FormatUtils.formatBigValue(amount, unit);
        }

        public void UpdateInfo(float amount, string label)
        {
            resourceLabel.text = label;
            UpdateInfo(amount);
        }

        public void UpdateInfo(ResourceInfo info)
        {
            unit = info.unit;
            UpdateInfo(info.amount, info.name);
        }
    }
}
