using System.Text.RegularExpressions;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    public class FBIdGeneratorTests
    {
        [Fact]
        public void SameInputProducesSameOutput()
        {
            var a = FBIdGenerator.GenerateFBId("Pusher_Test_v1");
            var b = FBIdGenerator.GenerateFBId("Pusher_Test_v1");
            Assert.Equal(a, b);
        }

        [Fact]
        public void DifferentInputsProduceDifferentOutputs()
        {
            var a = FBIdGenerator.GenerateFBId("Pusher_Test_v1");
            var b = FBIdGenerator.GenerateFBId("Pusher_Test_v2");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void OutputIsExactly16HexUppercaseChars()
        {
            var id = FBIdGenerator.GenerateFBId("seed");
            Assert.Equal(16, id.Length);
            Assert.Matches(new Regex("^[0-9A-F]{16}$"), id);
        }
    }
}
