#if NIGHTBUILD
using UnityEngine;

namespace CargoAccelerators
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class GlobalsReloader : MonoBehaviour
    {
        private static GlobalsReloader instance;

        private static Callback onGlobalsLoad = () => { };

        public static void AddListener(Callback cb) => onGlobalsLoad += cb;
        public static void RemoveListener(Callback cb) => onGlobalsLoad -= cb;

        private void Awake()
        {
            if(instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if(Input.GetKeyDown(KeyCode.LeftAlt)
               && Input.GetKeyDown(KeyCode.Home))
            {
                Globals.Load();
                onGlobalsLoad();
            }
        }
    }
}
#endif
