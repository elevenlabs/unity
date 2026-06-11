// Audio-glue composition for the WebGL default-mode audio path (Plan B task 2.3).
//
// Composes the SDK's input/output wiring primitives + a Unity-local
// audio-payload-stripping callback wrapper into a single `attachDefaultAudio`
// factory exposed to C# via the generic JsBridge.InvokeFactoryAsync surface.
//
// Why this lives Unity-side and not in @elevenlabs/client:
//   - "Default audio" only reads as "default" from the Unity bridge's POV — the
//     SDK ships the building blocks; composition belongs to the consumer.
//   - withoutAudioPayload is an 8-line pure wrapper over the public
//     IncomingSocketEvent type; if the SDK ever refactors the audio event
//     shape, tsc fails loud here rather than masking the change.
//
// Scope: WebSocket connections only. WebRTC connections expose `.input` and
// `.output` already wired by livekit-client internally — the C# side reads
// those directly via jsObject.Get<JsObject>("input"/"output") and skips this
// factory entirely.

import {
  attachInputToConnection,
  attachConnectionToOutput,
} from "@elevenlabs/client/internal/unity";
import type { IncomingSocketEvent } from "@elevenlabs/client";

type OnMessage = (event: IncomingSocketEvent) => void;

// Pure callback wrapper: forwards `audio` events with audio_event.audio_base_64
// stripped, and all other events unchanged. Does not mutate the input event.
//
// The base64 audio payload is many kilobytes per chunk; the C# bridge only
// needs event_id / alignment for interruption + timeline bookkeeping, while
// the actual audio bytes stay in JS via attachConnectionToOutput.
export function withoutAudioPayload(callback: OnMessage): OnMessage {
  return (event) => {
    if (event.type === "audio") {
      const { audio_base_64: _stripped, ...restAudioEvent } = event.audio_event;
      // restAudioEvent is structurally Omit<AudioEvent, "audio_base_64">.
      // The OnMessageCallback signature still declares audio_base_64 as
      // required (`string`), so we cast — runtime correctness is what counts
      // for the C# bookkeeping subscriber, which only reads event_id /
      // alignment.
      callback({
        ...event,
        audio_event: restAudioEvent as typeof event.audio_event,
      });
    } else {
      callback(event);
    }
  };
}

// Shape of the connection/input/output args the factory receives after the
// primitives' $EL_Rehydrate has resolved {$ref:N} markers from C# back into
// live JS objects. Loose typing — the C# call site is responsible for
// passing handles to the right kinds of objects (WebSocketConnection,
// MediaDeviceInput, MediaDeviceOutput).
type WsConnection = Parameters<typeof attachInputToConnection>[1] &
  Parameters<typeof attachConnectionToOutput>[0] & {
    onMessage(callback: OnMessage): void;
  };
type Input = Parameters<typeof attachInputToConnection>[0];
type Output = Parameters<typeof attachConnectionToOutput>[1];

// $EL_AudioGlueFactories is a jslib library entry (non-function). Emscripten
// hoists it as _EL_AudioGlueFactories so the __postset below can iterate and
// register each entry with _EL_RegisterFactory.
//
// Args arrive positionally from C#:
//   [connection, input, output, bridgeCallback]
// where the first three are {$ref:N} markers rehydrated to live SDK objects
// and bridgeCallback is a {$cb:N} marker rehydrated to a JS function that
// fires _EL_InvokeCallback back into C# when called.
//
// Return shape is "function" (per Plan B task 2.3) — the dispatcher allocates
// a JsFunction handle for the returned detach closure so C# can call it later
// to tear down the wiring.
export const $EL_AudioGlueFactories: Record<
  string,
  (...args: unknown[]) => unknown
> = {
  attachDefaultAudio: (...args: unknown[]) => {
    const [connection, input, output, bridgeCallback] = args as [
      WsConnection,
      Input,
      Output,
      OnMessage,
    ];
    const detachIn = attachInputToConnection(input, connection);
    const detachOut = attachConnectionToOutput(connection, output);
    connection.onMessage(withoutAudioPayload(bridgeCallback));
    return () => {
      detachIn();
      detachOut();
      // connection.onMessage is single-callback in the SDK; teardown of the
      // subscription happens when the C# side disposes the connection handle
      // (which triggers connection.close() in the bridged C# wrapper).
    };
  },
};

export const $EL_AudioGlueFactories__deps = ["$EL_RegisterFactory"];

export const $EL_AudioGlueFactories__postset =
  "Object.keys(_EL_AudioGlueFactories).forEach(function(k){_EL_RegisterFactory(k,_EL_AudioGlueFactories[k]);});";
