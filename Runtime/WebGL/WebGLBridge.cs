using UnityEngine;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Singleton MonoBehaviour that serves as the JS→C# message dispatch point for the
    /// ElevenLabs WebGL bridge. Unity's <c>SendMessage</c> targets public instance methods
    /// on this GameObject by name; each primitive wires its real handler in its own phase.
    /// </summary>
    public sealed class WebGLBridge : MonoBehaviour
    {
        /// <summary>Name of the bridge GameObject, shared with the jslib side via <see cref="ElevenLabsBridgeNative.EL_SetBridgeName"/>.</summary>
        internal const string GameObjectName = "__ElevenLabsBridge__";

        private static WebGLBridge _instance;

        /// <summary>
        /// Ensures the bridge GameObject is created before any JS code can SendMessage to it.
        /// Called automatically at startup via <see cref="RuntimeInitializeOnLoadMethodAttribute"/>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EnsureCreated() => _ = Instance;

        /// <summary>Gets the singleton bridge instance, creating it on first access.</summary>
        public static WebGLBridge Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject(GameObjectName);
                    DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    _instance = go.AddComponent<WebGLBridge>();
#if UNITY_WEBGL && !UNITY_EDITOR
                    ElevenLabsBridgeNative.EL_SetBridgeName(GameObjectName);
#endif
                }
                return _instance;
            }
        }

        // SendMessage targets are added in Phase 3 of generic-bridge-primitives.md
        // alongside the registries they dispatch into.

        private void OnDestroy()
        {
            // Phase 3 wires real teardown (registry cancellation/disposal) here.
        }
    }
}
