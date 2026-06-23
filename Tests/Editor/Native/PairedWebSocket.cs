#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Pairs a client-side <see cref="WebSocket"/> with a server-side
    /// <see cref="WebSocket"/> over a localhost TCP loopback. Both ends are
    /// constructed via <see cref="WebSocket.CreateFromStream"/>, which skips
    /// the HTTP upgrade handshake — tests that exercise the WebSocket frame
    /// protocol (and the conversation-initiation flow on top of it) don't
    /// need to round-trip a real Sec-WebSocket-Accept dance through
    /// <see cref="System.Net.HttpListener"/>, and Mono's HttpListener WebSocket
    /// support is too buggy in Unity Editor to rely on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Native connection's <see cref="NativeWebSocketConnection.CreateInternalAsync"/>
    /// seam accepts a pre-connected <see cref="WebSocket"/>, which is what
    /// makes this fixture sufficient: production <see cref="System.Net.WebSockets.ClientWebSocket"/>
    /// usage is covered indirectly by the same code path, and the URL-build
    /// half of <see cref="NativeWebSocketConnection.CreateAsync(ConversationOptions, System.Threading.CancellationToken)"/>
    /// is exercised by pure unit tests.
    /// </para>
    /// <para>
    /// Each pair owns its TCP sockets and the listener; <see cref="Dispose"/>
    /// closes them. Disposing the wrapping <see cref="WebSocket"/>s leaves the
    /// streams in a half-closed state that the BCL won't recover from, so the
    /// fixture owns teardown explicitly.
    /// </para>
    /// </remarks>
    internal sealed class PairedWebSocket : IDisposable
    {
        private readonly TcpClient _clientSide;
        private readonly TcpClient _serverSide;

        public WebSocket Client { get; }
        public WebSocket Server { get; }

        private PairedWebSocket(
            WebSocket client,
            WebSocket server,
            TcpClient clientSide,
            TcpClient serverSide
        )
        {
            Client = client;
            Server = server;
            _clientSide = clientSide;
            _serverSide = serverSide;
        }

        public static async Task<PairedWebSocket> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var clientTcp = new TcpClient();
                Task connectTask = clientTcp.ConnectAsync(IPAddress.Loopback, port);
                TcpClient serverTcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                await connectTask.ConfigureAwait(false);

                clientTcp.NoDelay = true;
                serverTcp.NoDelay = true;

                WebSocket clientWs = WebSocket.CreateFromStream(
                    clientTcp.GetStream(),
                    isServer: false,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromMinutes(5)
                );
                WebSocket serverWs = WebSocket.CreateFromStream(
                    serverTcp.GetStream(),
                    isServer: true,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromMinutes(5)
                );

                return new PairedWebSocket(clientWs, serverWs, clientTcp, serverTcp);
            }
            finally
            {
                // Stop the listener as soon as both ends are connected — we
                // don't need to accept further inbound connections.
                listener.Stop();
            }
        }

        public void Dispose()
        {
            try
            {
                Client.Dispose();
            }
            catch { }
            try
            {
                Server.Dispose();
            }
            catch { }
            try
            {
                _clientSide.Close();
            }
            catch { }
            try
            {
                _serverSide.Close();
            }
            catch { }
        }
    }
}
