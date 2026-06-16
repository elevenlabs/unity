using NUnit.Framework;

namespace ElevenLabs.WebGL.Tests
{
    public class BridgeArgEncoderTests
    {
        [Test]
        public void Encode_NoArgs_ReturnsEmptyArray()
        {
            Assert.AreEqual("[]", BridgeArgEncoder.Encode());
        }

        [Test]
        public void Encode_NullArg_ReturnsArrayWithJsonNull()
        {
            Assert.AreEqual("[null]", BridgeArgEncoder.Encode((object)null));
        }

        [Test]
        public void Encode_IntArg_SerializesAsPlainValue()
        {
            Assert.AreEqual("[42]", BridgeArgEncoder.Encode(42));
        }

        [Test]
        public void Encode_StringArg_SerializesAsPlainValue()
        {
            Assert.AreEqual("[\"hello\"]", BridgeArgEncoder.Encode("hello"));
        }

        [Test]
        public void Encode_BoolArg_SerializesAsPlainValue()
        {
            Assert.AreEqual("[true]", BridgeArgEncoder.Encode(true));
        }

        [Test]
        public void Encode_JsObject_EmitsRefMarker()
        {
            var obj = new JsObject(7);
            Assert.AreEqual("[{\"$ref\":7}]", BridgeArgEncoder.Encode(obj));
        }

        [Test]
        public void Encode_JsFunction_EmitsFnMarker()
        {
            var fn = new JsFunction(3);
            Assert.AreEqual("[{\"$fn\":3}]", BridgeArgEncoder.Encode(fn));
        }

        [Test]
        public void Encode_BridgeCallback_EmitsCbMarker()
        {
            var cb = new BridgeCallback(5);
            Assert.AreEqual("[{\"$cb\":5}]", BridgeArgEncoder.Encode(cb));
        }

        [Test]
        public void Encode_MixedArgs_EncodesEachByType()
        {
            string json = BridgeArgEncoder.Encode(
                "text",
                99,
                new JsObject(1),
                new JsFunction(2),
                new BridgeCallback(3)
            );
            Assert.AreEqual("[\"text\",99,{\"$ref\":1},{\"$fn\":2},{\"$cb\":3}]", json);
        }
    }
}
