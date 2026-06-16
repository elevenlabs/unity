using NUnit.Framework;

namespace ElevenLabs.WebGL.Tests
{
    public class BridgeValueDecoderTests
    {
        [Test]
        public void Decode_NullString_ReturnsDefaultForValueType()
        {
            Assert.AreEqual(0, BridgeValueDecoder.Decode<int>(null));
        }

        [Test]
        public void Decode_EmptyString_ReturnsDefaultForValueType()
        {
            Assert.AreEqual(0, BridgeValueDecoder.Decode<int>(string.Empty));
        }

        [Test]
        public void Decode_NullLiteral_ReturnsNullForReferenceType()
        {
            Assert.IsNull(BridgeValueDecoder.Decode<string>("null"));
        }

        [Test]
        public void Decode_PlainInt_ReturnsValue()
        {
            Assert.AreEqual(42, BridgeValueDecoder.Decode<int>("42"));
        }

        [Test]
        public void Decode_PlainString_ReturnsValue()
        {
            Assert.AreEqual("hello", BridgeValueDecoder.Decode<string>("\"hello\""));
        }

        [Test]
        public void Decode_PlainBool_ReturnsValue()
        {
            Assert.IsTrue(BridgeValueDecoder.Decode<bool>("true"));
        }

        [Test]
        public void Decode_RefMarker_ReturnsJsObjectWithCorrectHandle()
        {
            var result = BridgeValueDecoder.Decode<JsObject>("{\"$ref\":7}");
            Assert.IsNotNull(result);
            Assert.AreEqual(7, result.Handle);
        }

        [Test]
        public void Decode_FnMarker_ReturnsJsFunctionWithCorrectHandle()
        {
            var result = BridgeValueDecoder.Decode<JsFunction>("{\"$fn\":3}");
            Assert.IsNotNull(result);
            Assert.AreEqual(3, result.Handle);
        }

        [Test]
        public void GetShapeFor_JsObject_ReturnsObjectShape()
        {
            Assert.AreEqual(BridgeReturnShape.Object, BridgeValueDecoder.GetShapeFor<JsObject>());
        }

        [Test]
        public void GetShapeFor_JsFunction_ReturnsFunctionShape()
        {
            Assert.AreEqual(
                BridgeReturnShape.Function,
                BridgeValueDecoder.GetShapeFor<JsFunction>()
            );
        }

        [Test]
        public void GetShapeFor_Int_ReturnsValueShape()
        {
            Assert.AreEqual(BridgeReturnShape.Value, BridgeValueDecoder.GetShapeFor<int>());
        }

        [Test]
        public void GetShapeFor_String_ReturnsValueShape()
        {
            Assert.AreEqual(BridgeReturnShape.Value, BridgeValueDecoder.GetShapeFor<string>());
        }
    }
}
