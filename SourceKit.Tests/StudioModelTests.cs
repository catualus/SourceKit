using System;
using System.Text;
using Xunit;

namespace SourceKit.Tests
{
    /// <summary>
    /// Whether VBSP keeps a model as a prop_static: a decision the tools make before compiling, which has
    /// to agree with VBSP's or a prop the tool counted on is deleted from the map.
    /// </summary>
    public class StudioModelTests
    {
        private const int FlagStaticProp = 0x10;

        /// <summary>
        /// A studiohdr_t prefix: the IDST ident and version, a 16-unit collision box, the flags word, and
        /// <paramref name="keyValues"/> after the header where keyvalueindex and keyvaluesize point.
        /// </summary>
        private static byte[] Model(int flags, string? keyValues = null, int version = 48)
        {
            var text = keyValues == null ? [] : Encoding.ASCII.GetBytes(keyValues);
            var bytes = new byte[400 + text.Length];
            "IDST"u8.CopyTo(bytes);
            BitConverter.GetBytes(version).CopyTo(bytes, 4);
            for (int i = 0; i < 3; i++)
            {
                BitConverter.GetBytes(-8f).CopyTo(bytes, 104 + i * 4);    // hull_min
                BitConverter.GetBytes(8f).CopyTo(bytes, 116 + i * 4);     // hull_max
            }
            BitConverter.GetBytes(flags).CopyTo(bytes, 152);
            if (keyValues != null)
            {
                BitConverter.GetBytes(400).CopyTo(bytes, 312);
                BitConverter.GetBytes(text.Length).CopyTo(bytes, 316);
                text.CopyTo(bytes, 400);
            }
            return bytes;
        }

        private static bool Usable(int flags, string? keyValues = null) => StudioModel.Parse(Model(flags, keyValues)).UsableAsStaticProp;

        [Fact]
        public void AModelCompiledWithoutStaticPropIsNotUsable()
        {
            var model = StudioModel.Parse(Model(0));
            Assert.True(model.Valid);
            Assert.False(model.StaticProp);
            Assert.False(model.UsableAsStaticProp);
        }

        [Fact]
        public void AStaticPropWithNoPropDataIsUsable()
        {
            Assert.True(Usable(FlagStaticProp));
            Assert.True(Usable(FlagStaticProp, "\"qc_path\" { \"value\" \"x.qc\" }"));
        }

        /// <summary>A physics prop: VBSP deletes it from the map unless its prop_data allows static use.</summary>
        [Theory]
        [InlineData("\"prop_data\" { \"base\" \"Wooden.Medium\" }", false)]
        [InlineData("\"prop_data\" { \"base\" \"Wooden.Medium\" \"allowstatic\" \"1\" }", true)]
        [InlineData("\"prop_data\" { \"allowstatic\" \"0\" }", false)]
        [InlineData("\"Prop_Data\"\n{\n\t\"AllowStatic\"\t\"1\"\n}", true)]
        [InlineData("\"prop_data\" { \"allowstatic\" \"\" }", false)]
        public void APropDataBlockMustAllowStaticUse(string keyValues, bool usable)
        {
            Assert.Equal(usable, Usable(FlagStaticProp, keyValues));
        }

        /// <summary>Only the prop_data block counts: allowstatic in another block allows nothing.</summary>
        [Fact]
        public void AllowStaticOutsideThePropDataBlockDoesNotCount()
        {
            Assert.False(Usable(FlagStaticProp, "\"prop_data\" { \"base\" \"Metal.Small\" } \"other\" { \"allowstatic\" \"1\" }"));
        }

        [Fact]
        public void AnUnclosedPropDataBlockIsNotUsable()
        {
            Assert.False(Usable(FlagStaticProp, "\"prop_data\" { \"allowstatic\" \"1\""));
        }

        [Fact]
        public void SomethingThatIsNotAModelIsNotUsable()
        {
            var bytes = Model(FlagStaticProp);
            "IDSQ"u8.CopyTo(bytes);
            var model = StudioModel.Parse(bytes);
            Assert.False(model.Valid);
            Assert.False(model.UsableAsStaticProp);
        }
    }
}
