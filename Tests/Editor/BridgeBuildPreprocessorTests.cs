using ElevenLabs.WebGL.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;

namespace ElevenLabs.WebGL.Tests
{
    public class BridgeBuildPreprocessorTests
    {
        [Test]
        public void Validate_WebGLWithTableOff_ThrowsBuildFailedException()
        {
            bool original = PlayerSettings.WebGL.webAssemblyTable;
            PlayerSettings.WebGL.webAssemblyTable = false;
            try
            {
                Assert.Throws<BuildFailedException>(() =>
                    BridgeBuildPreprocessor.Validate(BuildTarget.WebGL)
                );
            }
            finally
            {
                PlayerSettings.WebGL.webAssemblyTable = original;
            }
        }

        [Test]
        public void Validate_WebGLWithTableOn_DoesNotThrow()
        {
            bool original = PlayerSettings.WebGL.webAssemblyTable;
            PlayerSettings.WebGL.webAssemblyTable = true;
            try
            {
                Assert.DoesNotThrow(() => BridgeBuildPreprocessor.Validate(BuildTarget.WebGL));
            }
            finally
            {
                PlayerSettings.WebGL.webAssemblyTable = original;
            }
        }

        [Test]
        public void Validate_NonWebGLPlatform_IsNoOp()
        {
            bool original = PlayerSettings.WebGL.webAssemblyTable;
            PlayerSettings.WebGL.webAssemblyTable = false;
            try
            {
                Assert.DoesNotThrow(() =>
                    BridgeBuildPreprocessor.Validate(BuildTarget.StandaloneWindows64)
                );
            }
            finally
            {
                PlayerSettings.WebGL.webAssemblyTable = original;
            }
        }
    }
}
