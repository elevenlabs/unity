// How the bundled @elevenlabs/client reports the end of a WebGL WebRTC session.
//
// BridgedWebRTCConnection forwards the client's onDisconnect payload to C#,
// where ParseDisconnectionDetails turns `reason: "error"` into
// DisconnectionReason.Error. These tests drive the real WebRTCConnection
// through the bridge's own factory, with LiveKit's Room replaced by a fake
// whose events the test fires, and pin which reason each kind of end reports.

import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DisconnectReason as DisconnectReasonEnum } from "livekit-client";

const room = vi.hoisted(() => ({
  handlers: new Map<string, (...args: unknown[]) => void>(),
  state: "connected",
}));

vi.mock("livekit-client", async (importOriginal) => {
  const original = await importOriginal<typeof import("livekit-client")>();
  class FakeRoom {
    name = "conv_test123";
    get state() {
      return room.state;
    }
    localParticipant = {
      publishData: vi.fn(() => Promise.resolve()),
      getTrackPublication: vi.fn(),
      audioTrackPublications: new Map(),
    };
    remoteParticipants = new Map();
    connect = vi.fn(() => Promise.resolve());
    disconnect = vi.fn(() => Promise.resolve());
    on(event: string, handler: (...args: unknown[]) => void) {
      room.handlers.set(event, handler);
      if (event === original.RoomEvent.Connected) queueMicrotask(handler);
      return this;
    }
    once(event: string, handler: (...args: unknown[]) => void) {
      return this.on(event, handler);
    }
    off() {
      return this;
    }
  }
  return { ...original, Room: FakeRoom };
});

import { DisconnectReason, RoomEvent } from "livekit-client";
import { connectionFactories } from "./factories.js";

type Connection = {
  onDisconnect(callback: (details: unknown) => void): void;
};

async function startSession() {
  const connection = (await connectionFactories.createWebRTCConnection({
    conversationToken: "test-token",
    connectionType: "webrtc",
    textOnly: true,
  })) as Connection;
  const onDisconnect = vi.fn();
  connection.onDisconnect(onDisconnect);
  return onDisconnect;
}

function fire(event: RoomEvent, ...args: unknown[]) {
  room.handlers.get(event)?.(...args);
}

const name = (reason: DisconnectReasonEnum) => DisconnectReason[reason];

beforeEach(() => {
  room.handlers.clear();
  room.state = "connected";
});

describe("WebRTC session end", () => {
  it.each(
    [
      DisconnectReason.SIGNAL_CLOSE,
      DisconnectReason.CONNECTION_TIMEOUT,
      DisconnectReason.MEDIA_FAILURE,
      DisconnectReason.AGENT_ERROR,
    ].map((reason) => ({ reason, name: name(reason) })),
  )(
    "reports an error when the room disconnects with $name",
    async ({ reason }) => {
      const onDisconnect = await startSession();

      fire(RoomEvent.Disconnected, reason);

      expect(onDisconnect).toHaveBeenCalledOnce();
      expect(onDisconnect).toHaveBeenCalledWith(
        expect.objectContaining({ reason: "error" }),
      );
    },
  );

  it("reports an error when the agent drops out while the room is reconnecting", async () => {
    const onDisconnect = await startSession();

    fire(RoomEvent.ParticipantDisconnected, { identity: "agent_123" });
    room.state = "reconnecting";
    fire(RoomEvent.ConnectionStateChanged, "reconnecting");
    await Promise.resolve();

    expect(onDisconnect).toHaveBeenCalledOnce();
    expect(onDisconnect).toHaveBeenCalledWith(
      expect.objectContaining({ reason: "error" }),
    );
  });

  it("reports the agent when the server closes the room", async () => {
    const onDisconnect = await startSession();

    fire(RoomEvent.Disconnected, DisconnectReason.ROOM_DELETED);

    expect(onDisconnect).toHaveBeenCalledWith(
      expect.objectContaining({ reason: "agent" }),
    );
  });

  it("reports the agent when it leaves a connected room", async () => {
    const onDisconnect = await startSession();

    fire(RoomEvent.ParticipantDisconnected, { identity: "agent_123" });
    await Promise.resolve();

    expect(onDisconnect).toHaveBeenCalledWith(
      expect.objectContaining({ reason: "agent" }),
    );
  });
});
