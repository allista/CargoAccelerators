using System.Linq;
using AT_Utils;
using UnityEngine;

namespace CargoAccelerators
{
    public partial class OrbitalAccelerator
    {
        private const string dockingNodeConfigName = "ConstructionPort";
        private const string dockingNodeSaveName = "ConstructionPortSave";

        [SerializeField] public ConfigNode dockingNodeConfig;
        [SerializeField] public ConfigNode dockingNodeSave;
        public string constructionPortTransformPrefabName;
        public string constructionPortTransformName;
        public TemporaryDockingNode constructionPort;

        private void loadDockingPortConfig(ConfigNode node)
        {
            // only save it the first time, when the module is loaded from part config
            if(dockingNodeConfig == null)
            {
                dockingNodeConfig = node.GetNode(dockingNodeConfigName);
                if(dockingNodeConfig != null)
                {
                    constructionPortTransformPrefabName = dockingNodeConfig.GetValue("nodeTransformName");
                    constructionPortTransformName = $"{constructionPortTransformPrefabName}_Instance";
                    dockingNodeConfig.SetValue("nodeTransformName", constructionPortTransformName);
                }
                else
                    this.Error($"Unable to find {dockingNodeConfigName} node in: {node}");
            }
            // the save comes each time on Load
            dockingNodeSave = node.GetNode(dockingNodeSaveName);
        }

        private void saveDockingPortState(ConfigNode node)
        {
            if(constructionPort != null)
                constructionPort.Save(node.AddNode(dockingNodeSaveName));
        }

        private bool setupDockingNode(bool forceStart = true)
        {
            if(DeploymentProgress < 1 || constructionPort != null)
                return true;
            this.Debug($"Setting up docking port");
            if(dockingNodeConfig == null)
            {
                this.Error($"{dockingNodeConfigName}: config node not found");
                return false;
            }
            constructionPort = part.FindModulesImplementing<TemporaryDockingNode>()
                .FirstOrDefault(m => m.nodeTransformName == constructionPortTransformName);
            if(constructionPort != null)
            {
                this.Debug($"Found existing docking port: {constructionPort.GetID()}");
                return true;
            }
            this.Debug($"Creating new docking port from: {dockingNodeConfig}");
            constructionPort = TemporaryDockingNode.AddToPart(part, dockingNodeConfig, dockingNodeSave, forceStart);
            if(constructionPort == null)
            {
                this.Error($"{dockingNodeConfigName}: unable to add part module from the config node");
                return false;
            }
            this.Debug($"Added new docking port: {constructionPort.GetID()}");
            return true;
        }

        private bool removeDockingNode()
        {
            if(constructionPort == null)
                return true;
            if(!constructionPort.RemoveSelf())
                return false;
            Destroy(constructionPort);
            constructionPort = null;
            dockingNodeSave = null;
            return true;
        }
    }
}
