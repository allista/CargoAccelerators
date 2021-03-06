using System;
using System.Collections.Generic;
using System.Linq;
using AT_Utils.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CA.UI
{
    public class ConstructionUI : ScreenBoundRect
    {
        public GameObject resourceInfoPrefab;

        public RectTransform resourcesList;

        public PanelledUI controlsPane,
            constructionPane;

        public Button closeButton,
            colorsButton,
            startStopButton,
            abortButton;

        public Text title,
            startStopButtonText,
            state,
            progress,
            workers,
            workforce,
            remainingTime,
            resourcesTitle;

        public Colorizer startStopColorizer,
            stateColorizer,
            progressColorizer;

        public TooltipTrigger startStopTooltip;

        private readonly Dictionary<string, ResourceInfoUI> resources = new Dictionary<string, ResourceInfoUI>();

        protected override void Awake()
        {
            base.Awake();
            startStopButtonText.text = "Start";
            title.text = "";
            state.text = "";
            progress.text = "";
            workers.text = "";
            workforce.text = "";
            remainingTime.text = "";
            resourcesTitle.text = "";
            stateColorizer.SetColor(Colors.Inactive);
            progressColorizer.SetColor(Colors.Inactive);
            startStopButton.SetInteractable(false);
            abortButton.SetInteractable(false);
        }

        public void UpdateProgress(double current, double max)
        {
            if(current >= 0 && max > 0)
            {
                var ratio = Math.Min(current / max, 1);
                progress.text = $"{ratio:P1}";
                progressColorizer.SetColor(new ColorSetting(Colors.FractionGradient.Evaluate((float)ratio)));
            }
            else
            {
                progress.text = "";
                progressColorizer.SetColor(Colors.Neutral);
            }
        }

        public void UpdateWorkforce(int kerbals, float workforceValue)
        {
            workers.text = $"{kerbals:D}";
            workforce.text = $"{workforceValue:F1}";
        }

        public void UpdateResources(IEnumerable<ResourceInfo> currentResources)
        {
            if(resourceInfoPrefab == null)
                return;
            var currentNames = new HashSet<string>();
            foreach(var res in currentResources)
            {
                currentNames.Add(res.name);
                if(!resources.TryGetValue(res.name, out var info))
                {
                    var go = Instantiate(resourceInfoPrefab, resourcesList);
                    info = go.GetComponent<ResourceInfoUI>();
                    if(info == null)
                    {
                        Destroy(go);
                        Debug.LogError($"Prefab {resourceInfoPrefab.name} does not contain ResourceInfoUI component");
                        resourceInfoPrefab = null;
                        return;
                    }
                    resources.Add(res.name, info);
                    info.UpdateInfo(res);
                }
                else
                    info.UpdateInfo(res.amount);
            }
            foreach(var resourceName in resources.Keys.ToList())
            {
                if(currentNames.Contains(resourceName))
                    continue;
                var res = resources[resourceName];
                Destroy(res.gameObject);
                resources.Remove(resourceName);
            }
        }
    }
}
