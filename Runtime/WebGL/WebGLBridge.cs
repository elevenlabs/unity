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

        // SendMessage targets. Each primitive's phase replaces these stubs with real dispatch.

        /// <summary>Called by JS via SendMessage when a promise settles (ok or err).</summary>
        public void OnPromiseSettled(string message) =>
            BridgeLog.Info($"OnPromiseSettled: {message}");

        /// <summary>Called by JS via SendMessage when an observer event fires.</summary>
        public void OnObserverEvent(string message) =>
            BridgeLog.Info($"OnObserverEvent: {message}");

        /// <summary>Called by JS via SendMessage when JS invokes a registered C# async handler.</summary>
        public void OnHandlerInvoked(string message) =>
            BridgeLog.Info($"OnHandlerInvoked: {message}");

        private void OnDestroy()
        {
            // Each primitive's phase wires real teardown (CancelAll / DisposeAll / RejectAll) here.
        }
    }
}
