using System.Collections.Generic;
using System.Reflection;
using UniLinq;
using UnityEngine;

namespace CargoAccelerators
{
    public class ExtensibleRCS : ModuleRCS
    {
        [KSPField] public string fxAudioPath = string.Empty;
        private AudioClip audioClip;
        private AudioSource _audioSource;

        private AudioSource audioSource
        {
            get
            {
                if(_audioSource != null)
                    return _audioSource;
                _audioSource = part.gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.loop = true;
                _audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
                _audioSource.dopplerLevel = 0.0f;
                _audioSource.volume = GameSettings.SHIP_VOLUME;
                _audioSource.spatialBlend = 1f;
                return _audioSource;
            }
        }

        public override void OnAwake()
        {
            base.OnAwake();
            GameEvents.onGamePause.Add(audioSource.Pause);
            GameEvents.onGameUnpause.Add(audioSource.UnPause);
        }

        private void OnDestroy()
        {
            GameEvents.onGamePause.Remove(audioSource.Pause);
            GameEvents.onGameUnpause.Remove(audioSource.UnPause);
            var baseOnDestroyM = typeof(ModuleRCS)
                .GetMethod(nameof(OnDestroy),
                    BindingFlags.Instance
                    | BindingFlags.NonPublic
                    | BindingFlags.Public);
            baseOnDestroyM?.Invoke(this, new object[] { });
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            audioClip = GameDatabase.Instance.GetAudioClip(fxAudioPath);
        }

        public new void Update()
        {
            base.Update();
            var maxForce = thrustForces.Max();
            if(!part.packed && maxForce > 0)
            {
                audioSource.pitch = Mathf.Clamp(maxForce / thrusterPower, 0.1f, 1);
                // ReSharper disable once InvertIf
                if(!audioSource.isPlaying)
                {
                    audioSource.clip = audioClip;
                    audioSource.Play();
                }
            }
            else if(audioSource.isPlaying)
                audioSource.Stop();
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
            thrusterFX.ForEach(fx => { fx.begin(audioSource); });
        }
    }
}
