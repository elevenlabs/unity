#nullable enable

using System.Collections.Generic;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
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

        [Test]
        public void OmitsUserId_WhenNullOrEmpty()
        {
            JObject withNull = BridgedSession.BuildSessionConfig(
                new ConversationOptions { AgentId = "agent-test", UserId = null }
            );
            Assert.IsFalse(withNull.ContainsKey("userId"));

            JObject withEmpty = BridgedSession.BuildSessionConfig(
                new ConversationOptions { AgentId = "agent-test", UserId = string.Empty }
            );
            Assert.IsFalse(withEmpty.ContainsKey("userId"));
        }

        [Test]
        public void EmitsUserId_WhenSet()
        {
            JObject config = BridgedSession.BuildSessionConfig(
                new ConversationOptions { AgentId = "agent-test", UserId = "user-42" }
            );
            Assert.AreEqual("user-42", config["userId"]!.Value<string>());
        }

        [Test]
        public void OmitsCustomLlmExtraBody_WhenNullOrEmpty()
        {
            JObject withNull = BridgedSession.BuildSessionConfig(
                new ConversationOptions { AgentId = "agent-test", CustomLlmExtraBody = null }
            );
            Assert.IsFalse(withNull.ContainsKey("customLlmExtraBody"));

            JObject withEmpty = BridgedSession.BuildSessionConfig(
                new ConversationOptions
                {
                    AgentId = "agent-test",
                    CustomLlmExtraBody = new Dictionary<string, object>(),
                }
            );
            Assert.IsFalse(withEmpty.ContainsKey("customLlmExtraBody"));
        }

        [Test]
        public void EmitsCustomLlmExtraBody_PreservingPrimitiveTypes()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                CustomLlmExtraBody = new Dictionary<string, object>
                {
                    ["temperature"] = 0.7,
                    ["max_tokens"] = 256,
                    ["stop"] = "###",
                },
            };

            JObject config = BridgedSession.BuildSessionConfig(options);
            var body = (JObject)config["customLlmExtraBody"]!;
            Assert.AreEqual(JTokenType.Float, body["temperature"]!.Type);
            Assert.AreEqual(0.7, body["temperature"]!.Value<double>());
            Assert.AreEqual(JTokenType.Integer, body["max_tokens"]!.Type);
            Assert.AreEqual(256, body["max_tokens"]!.Value<int>());
            Assert.AreEqual(JTokenType.String, body["stop"]!.Type);
            Assert.AreEqual("###", body["stop"]!.Value<string>());
        }

        [Test]
        public void OmitsOverrides_WhenNull()
        {
            JObject config = BridgedSession.BuildSessionConfig(
                new ConversationOptions { AgentId = "agent-test", Overrides = null }
            );
            Assert.IsFalse(config.ContainsKey("overrides"));
        }

        [Test]
        public void OmitsOverrides_WhenAllSubtreesNull()
        {
            JObject config = BridgedSession.BuildSessionConfig(
                new ConversationOptions
                {
                    AgentId = "agent-test",
                    Overrides = new ConversationConfigOverride(),
                }
            );
            Assert.IsFalse(
                config.ContainsKey("overrides"),
                "An overrides object with no populated subtree is wire noise — drop it."
            );
        }

        [Test]
        public void OmitsAgentSubtree_WhenAllFieldsUnset()
        {
            JObject config = BridgedSession.BuildSessionConfig(
                new ConversationOptions
                {
                    AgentId = "agent-test",
                    Overrides = new ConversationConfigOverride
                    {
                        Agent = new ConversationConfigOverrideAgent(),
                        Tts = new ConversationConfigOverrideTts { VoiceId = "voice-42" },
                    },
                }
            );
            var overrides = (JObject)config["overrides"]!;
            Assert.IsFalse(
                overrides.ContainsKey("agent"),
                "An agent override with every field null should not appear on the wire."
            );
            Assert.IsTrue(overrides.ContainsKey("tts"));
        }

        [Test]
        public void EmitsAgentOverride_AsCamelCase_WithSnakeCasePromptPassthrough()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                Overrides = new ConversationConfigOverride
                {
                    Agent = new ConversationConfigOverrideAgent
                    {
                        FirstMessage = "Hello!",
                        Language = "en",
                        Prompt = new ConversationConfigOverrideAgentPrompt
                        {
                            Prompt = "Be terse.",
                            Llm = "gpt-4o-mini",
                            ToolIds = new[] { "tool-a", "tool-b" },
                        },
                    },
                },
            };

            JObject config = BridgedSession.BuildSessionConfig(options);
            var agent = (JObject)config["overrides"]!["agent"]!;
            Assert.AreEqual("Hello!", agent["firstMessage"]!.Value<string>());
            Assert.AreEqual("en", agent["language"]!.Value<string>());
            // agent.prompt is a wire-shape pass-through — JS SDK forwards the
            // object unchanged, so snake_case keys must survive.
            var prompt = (JObject)agent["prompt"]!;
            Assert.AreEqual("Be terse.", prompt["prompt"]!.Value<string>());
            Assert.AreEqual("gpt-4o-mini", prompt["llm"]!.Value<string>());
            var toolIds = (JArray)prompt["tool_ids"]!;
            Assert.AreEqual(2, toolIds.Count);
            Assert.AreEqual("tool-a", toolIds[0].Value<string>());
        }

        [Test]
        public void EmitsTtsOverride_AsCamelCase_PreservingZeroValues()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                Overrides = new ConversationConfigOverride
                {
                    // Zero is meaningful for stability/speed — only HasValue==false omits.
                    Tts = new ConversationConfigOverrideTts
                    {
                        VoiceId = "voice-42",
                        Stability = 0.0,
                        Speed = 1.0,
                        SimilarityBoost = 0.8,
                    },
                },
            };

            JObject config = BridgedSession.BuildSessionConfig(options);
            var tts = (JObject)config["overrides"]!["tts"]!;
            Assert.AreEqual("voice-42", tts["voiceId"]!.Value<string>());
            Assert.AreEqual(0.0, tts["stability"]!.Value<double>());
            Assert.AreEqual(1.0, tts["speed"]!.Value<double>());
            Assert.AreEqual(0.8, tts["similarityBoost"]!.Value<double>());
        }

        [Test]
        public void EmitsConversationOverride_AsCamelCase()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                Overrides = new ConversationConfigOverride
                {
                    Conversation = new ConversationConfigOverrideConversation { TextOnly = true },
                },
            };

            JObject config = BridgedSession.BuildSessionConfig(options);
            var conv = (JObject)config["overrides"]!["conversation"]!;
            Assert.AreEqual(true, conv["textOnly"]!.Value<bool>());
        }

        [Test]
        public void DropsTurnSubtree_OnBridgedPath()
        {
            // JS SDK's SessionConfig.overrides does not expose `turn` — it
            // only takes effect once the native transport (#9) lands. The
            // typed surface still accepts the field so call sites compile.
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                Overrides = new ConversationConfigOverride
                {
                    Turn = new ConversationConfigOverrideTurn
                    {
                        SoftTimeoutConfig = new ConversationConfigOverrideTurnSoftTimeoutConfig
                        {
                            Message = "Hmm…",
                        },
                    },
                },
            };
            JObject config = BridgedSession.BuildSessionConfig(options);
            Assert.IsFalse(
                config.ContainsKey("overrides"),
                "Turn-only overrides have no JS-SDK passthrough yet; bridged config stays empty."
            );
        }
    }
}
