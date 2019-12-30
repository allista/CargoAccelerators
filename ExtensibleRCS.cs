using System.Collections.Generic;
using UnityEngine;

namespace CargoAccelerators
{
    public class ExtensibleRCS : ModuleRCS
    {
        private AudioSource _audioRef;

        private AudioSource audioRef
        {
            get
            {
                if(_audioRef != null)
                    return _audioRef;
                _audioRef = part.GetComponent<AudioSource>();
                if(_audioRef != null)
                    return _audioRef;
                _audioRef = part.gameObject.AddComponent<AudioSource>();
                _audioRef.playOnAwake = false;
                _audioRef.loop = true;
                _audioRef.rolloffMode = AudioRolloffMode.Logarithmic;
                _audioRef.dopplerLevel = 0.0f;
                _audioRef.volume = GameSettings.SHIP_VOLUME;
                _audioRef.spatialBlend = 1f;
                return _audioRef;
            }
        }

        public void UpdateThrusterTransforms()
        {
            foreach(var fxGroup in thrusterFX)
            {
                var partGroupIdx = part.fxGroups.FindIndex(g => g.name == fxGroup.name);
                if(partGroupIdx < 0)
                    continue;
                var partGroup = part.fxGroups[partGroupIdx];
                partGroup.fxEmittersNewSystem.ForEach(ps =>
                {
                    if(ps != null)
                        Destroy(ps.gameObject);
                });
                part.fxGroups.RemoveAt(partGroupIdx);
            }
            thrusterFX.Clear();
            thrusterTransforms =
                new List<Transform>(part.FindModelTransforms(thrusterTransformName));
            thrustForces = new float[thrusterTransforms.Count];
            SetupFX();
            thrusterFX.ForEach(fx => fx.begin(audioRef));
        }
    }
}
