//   NamedDockingNode.cs
//
//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2018 Allis Tauri

using System;
using AT_Utils;

namespace CargoAccelerators
{
    public class TemporaryDockingNode : NamedDockingNode
    {
        private int moduleIndex = -1;

        protected override void OnDestroy()
        {
            if(moduleIndex >= 0 && FlightGlobals.fetch != null && part != null && vessel != null)
            {
                foreach(var vsl in FlightGlobals.Vessels)
                {
                    var targetInfo = vsl.protoVessel?.targetInfo;
                    if(targetInfo == null
                       || targetInfo.targetType != ProtoTargetInfo.Type.PartModule
                       || targetInfo.partUID != part.flightID)
                        continue;
                    if(targetInfo.partModuleIndex < part.Modules.Count
                       && part.Modules[targetInfo.partModuleIndex] != this)
                        continue;
                    vsl.protoVessel.targetInfo = new ProtoTargetInfo(vessel);
                    vsl.targetObject = vessel;
                    this.Debug($"Unset {this.GetID()} as the target of the {vsl.GetID()}");
                }
            }
            base.OnDestroy();
        }

        public override void OnStart(StartState st)
        {
            moduleIndex = part.Modules.IndexOf(this);
            if(!HighLogic.LoadedSceneIsFlight)
                return;
            if(dockedPartUId != 0)
            {
                otherNode = FindOtherNode();
                if(otherNode != null)
                {
                    otherNode.otherNode = this;
                    otherNode.dockingNodeModuleIndex = part.Modules.IndexOf(this);
                }
            }
            base.OnStart(st);
        }

        public static TemporaryDockingNode AddToPart(Part part, ConfigNode config, ConfigNode save, bool forceStart)
        {
            var dockingPort = part.AddModule(config, true) as TemporaryDockingNode;
            if(dockingPort == null)
            {
                part.Error($"Unable to add TemporaryDockingNode module from the config node: {config}");
                return null;
            }
            try
            {
                if(save != null)
                {
                    part.Debug($"Loading TemporaryDockingNode from save: {save}");
                    dockingPort.Load(save);
                }
                if(forceStart)
                {
                    var startState = part.GetModuleStartState();
                    dockingPort.resHandler.SetPartModule(dockingPort);
                    dockingPort.ApplyUpgrades(startState);
                    dockingPort.OnStart(startState);
                    dockingPort.OnStartFinished(startState);
                    dockingPort.ApplyAdjustersOnStart();
                    dockingPort.OnInitialize();
                    dockingPort.UpdateStagingToggle();
                }
            }
            catch(Exception ex)
            {
                part.Error($"Module {dockingPort.GetID()} was not initialized: {ex}");
                part.Modules.Remove(dockingPort);
                Destroy(dockingPort);
                return null;
            }
            if(part.vessel != null)
                part.vessel.dockingPorts.AddUnique(dockingPort);
            return dockingPort;
        }

        public bool RemoveSelf()
        {
            if(otherNode != null)
            {
                Utils.Message(otherNode.vessel == vessel
                    ? $"Undock the \"{otherNode.vesselInfo.name}\""
                    : $"\"{otherNode.vessel.vesselName}\" is too close");
                return false;
            }
            part.Modules.Remove(this);
            part.dockingPorts.Remove(this);
            if(vessel != null)
                vessel.dockingPorts.Remove(this);
            return true;
        }
    }
}
