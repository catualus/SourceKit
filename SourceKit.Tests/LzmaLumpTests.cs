using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SourceKit.Tests
{
    /// <summary>
    /// What <see cref="LzmaLump"/> does with lumps that are not simply well-formed. Decoding a good one is
    /// covered beside the visibility reader; these are the edges every lump reader leans on.
    /// </summary>
    public class LzmaLumpTests
    {
        private static readonly byte[] Data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 253)).ToArray();

        private static BinaryReader Reader(byte[] bytes) => new(new MemoryStream(bytes));

        [Fact]
        public void ALumpWithoutTheTagIsReturnedAsItIs()
        {
            byte[] file = [.. new byte[3], .. Data];
            Assert.Equal(Data, LzmaLump.Read(Reader(file), 3, Data.Length));
        }

        [Fact]
        public void AnUntaggedLumpRunningOffTheEndIsCutAtTheEnd()
        {
            byte[] file = [.. new byte[3], .. Data];
            Assert.Equal(Data, LzmaLump.Read(Reader(file), 3, int.MaxValue));
            Assert.Empty(LzmaLump.Read(Reader(file), file.Length + 10, 16));
            Assert.Empty(LzmaLump.Read(Reader(file), -1, 16));
        }

        /// <summary>
        /// A stream that cannot expand to the size its header declares is corruption that would otherwise
        /// read as fewer records - a world with holes in it - so it throws, and throws what every other
        /// malformed lump throws rather than the decoder's own exception type.
        /// </summary>
        [Fact]
        public void AnExpansionShorterThanDeclaredThrows()
        {
            var lump = BspVisibilityTests.Compress(Data);
            BitConverter.GetBytes(Data.Length + 100).CopyTo(lump, 4);

            Assert.Throws<InvalidDataException>(() => LzmaLump.Read(Reader(lump), 0, lump.Length));
        }

        /// <summary>A stream cut short, so the decoder runs out of input partway.</summary>
        [Fact]
        public void ATruncatedStreamThrows()
        {
            var lump = BspVisibilityTests.Compress(Data);
            int cut = (lump.Length - 17) / 2;
            BitConverter.GetBytes(cut).CopyTo(lump, 8);

            Assert.Throws<InvalidDataException>(() => LzmaLump.Read(Reader(lump), 0, 17 + cut));
        }

        /// <summary>
        /// A tag whose sizes cannot be true - a compressed stream longer than the file, a decompressed
        /// size past what any lump holds - is not trusted to size a buffer: the raw bytes come back for
        /// the caller's own checks to refuse.
        /// </summary>
        [Theory]
        [InlineData(8, 1_000_000)]          // compressed size past the end of the file
        [InlineData(4, 600 * 1024 * 1024)]  // decompressed size past the ceiling
        [InlineData(4, -5)]                 // a negative size
        public void ATagWhoseSizesDoNotFitIsReturnedRaw(int field, int value)
        {
            var lump = BspVisibilityTests.Compress(Data);
            BitConverter.GetBytes(value).CopyTo(lump, field);

            Assert.Equal(lump, LzmaLump.Read(Reader(lump), 0, lump.Length));
        }

        [Fact]
        public void ALumpTooShortForTheHeaderIsReturnedRaw()
        {
            byte[] file = [.. "LZMA"u8, 1, 2, 3];
            Assert.Equal(file, LzmaLump.Read(Reader(file), 0, file.Length));
        }
    }
}
