#nullable enable

using System.Collections.Generic;
using ElevenLabs.Agents;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Locks down the JSON shape of <see cref="BridgedSession.BuildSessionConfig"/>.
    /// The C# layer hands this JObject opaquely to the JS factories, so this is
    /// the only test that catches a renamed or dropped field — including a
    /// drift in the upstream <c>SessionConfig</c> contract that would otherwise
    /// silently break the conversation handshake.
    /// </summary>
    public class BridgedSessionBuildSessionConfigTests
    {
        [Test]
        public void OmitsDynamicVariables_WhenNull()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                ConnectionType = ConnectionType.WebSocket,
            };

            JObject config = BridgedSession.BuildSessionConfig(options);

            Assert.IsFalse(
                config.ContainsKey("dynamicVariables"),
                "dynamicVariables must be absent when DynamicVariables is null — "
                    + "an empty object pollutes the SDK union-type validation."
            );
        }

        [Test]
        public void OmitsDynamicVariables_WhenEmpty()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                DynamicVariables = new Dictionary<string, object>(),
            };

            JObject config = BridgedSession.BuildSessionConfig(options);

            Assert.IsFalse(
                config.ContainsKey("dynamicVariables"),
                "dynamicVariables must be absent when the dictionary is empty."
            );
        }

        [Test]
        public void EmitsDynamicVariables_PreservingStringNumberAndBoolTypes()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                DynamicVariables = new Dictionary<string, object>
                {
                    ["color"] = "red",
                    ["count"] = 3,
                    ["isReady"] = true,
                },
            };

            JObject config = BridgedSession.BuildSessionConfig(options);

            JToken? dyn = config["dynamicVariables"];
            Assert.IsNotNull(
                dyn,
                "dynamicVariables must be present when the dictionary is non-empty."
            );
            Assert.AreEqual(JTokenType.Object, dyn!.Type);

            var obj = (JObject)dyn;
            Assert.AreEqual(JTokenType.String, obj["color"]!.Type);
            Assert.AreEqual("red", obj["color"]!.Value<string>());
            Assert.AreEqual(JTokenType.Integer, obj["count"]!.Type);
            Assert.AreEqual(3, obj["count"]!.Value<int>());
            Assert.AreEqual(JTokenType.Boolean, obj["isReady"]!.Type);
            Assert.AreEqual(true, obj["isReady"]!.Value<bool>());
        }
    }
}
