using System;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Vmf;
using Xunit;

namespace SourceKit.Tests
{
    /// <summary>
    /// Numbering new blocks without walking the map per block, and copying a document without writing
    /// it out and parsing it again.
    /// </summary>
    public class VmfCopyAndIdTests
    {
        private static VmfDocument Cube() => VmfTests.CubeDocument();

        /// <summary>A brush as a caller places one: a solid and six sides, numbered from <paramref name="first"/>.</summary>
        private static VmfNode Brush(long first)
        {
            var solid = new VmfNode("solid");
            solid.Set("id", first.ToString());
            for (int i = 1; i <= 6; i++)
            {
                var side = new VmfNode("side");
                solid.Add(side);
                side.Set("id", (first + i).ToString());
            }
            return solid;
        }

        [Fact]
        public void NextIdsHandsOutContiguousRangesAboveEveryId()
        {
            var map = Cube();
            Assert.Equal(20, map.MaxId());

            Assert.Equal(21, map.NextIds(8));
            Assert.Equal(29, map.NextIds(8));
            Assert.Equal(37, map.NextIds(1));

            // Reserving is not placing: MaxId still reports what is in the map.
            Assert.Equal(20, map.MaxId());
        }

        [Fact]
        public void BrushesPlacedFromReservedRangesNeverCollide()
        {
            var map = Cube();
            for (int i = 0; i < 50; i++)
                map.World.Add(Brush(map.NextIds(7)));

            var ids = map.Root.Descendants().Select(n => n.Id).OfType<long>().ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal(20 + 50 * 7, map.MaxId());
        }

        /// <summary>A block the caller numbered itself, added after the first call, moves the next range above it.</summary>
        [Fact]
        public void ABlockAddedWithItsOwnIdsIsNoticed()
        {
            var map = Cube();
            map.NextIds(8);

            map.World.Add(Brush(500));
            Assert.Equal(506, map.MaxId());
            Assert.Equal(507, map.NextIds(8));

            // Inserted rather than added, and numbered deep inside.
            var entity = new VmfNode("entity");
            var inner = new VmfNode("solid");
            entity.Add(inner);
            inner.Set("id", "900");
            map.Root.InsertBefore(map.World, entity);
            Assert.Equal(901, map.NextIds(2));
        }

        [Fact]
        public void AnIdSetOnABlockInTheMapIsNoticed()
        {
            var map = Cube();
            map.NextIds(1);

            map.World.Child("solid")!.Set("id", "2000");
            Assert.Equal(2001, map.NextIds(1));

            var side = map.World.Child("solid")!.Children("side").First();
            side.GetPair("id")!.Value = "3000";
            Assert.Equal(3001, map.NextIds(1));

            var entity = new VmfNode("entity");
            map.Root.Add(entity);
            entity.Set("ID", "4000");
            Assert.Equal(4001, map.NextIds(1));
        }

        /// <summary>An id given up is not handed out again: a removed block may still be named elsewhere.</summary>
        [Fact]
        public void RemovingABlockDoesNotGiveItsIdsBack()
        {
            var map = Cube();
            long first = map.NextIds(7);
            var brush = Brush(first);
            map.World.Add(brush);
            map.World.Remove(brush);

            Assert.Equal(first + 7, map.NextIds(1));
        }

        [Fact]
        public void NextIdsNeedsAPositiveCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Cube().NextIds(0));
        }

        [Fact]
        public void ACloneWritesOutByteForByte()
        {
            byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes(VmfTests.CubeText.Replace("sky_day01_01", "caf\u00e9"))];
            var map = VmfDocument.Parse(bytes);

            // Edited and added lines, which a clone must write the same way: indented like the file.
            map.World.Set("skyname", "sky_night01");
            map.World.Set("detailvbsp", "detail.vbsp");
            map.World.Add(Brush(100));

            var copy = map.Clone();

            Assert.Equal(map.ToString(), copy.ToString());
            using var a = new MemoryStream();
            using var b = new MemoryStream();
            map.Write(a);
            copy.Write(b);
            Assert.Equal(a.ToArray(), b.ToArray());
            Assert.Equal(map.NewLine, copy.NewLine);
            Assert.True(copy.HasByteOrderMark);
        }

        [Fact]
        public void EditsToACloneLeaveTheOriginalAlone()
        {
            var map = Cube();
            string before = map.ToString();
            var copy = map.Clone();

            copy.World.Set("skyname", "sky_night01");
            copy.World.Child("solid")!.Children("side").First().Set("material", "TOOLS/TOOLSNODRAW");
            copy.World.Remove(copy.World.Child("solid")!);
            copy.Root.Add(new VmfNode("cameras"));
            copy.Entities.First().Set("targetname", "spawn");

            Assert.Equal(before, map.ToString());
            Assert.Equal(VmfTests.CubeDocument().ToString(), map.ToString());
            Assert.NotNull(map.World.Child("solid"));

            // And the other way.
            map.World.Set("skyname", "sky_dusk");
            Assert.Equal("sky_night01", copy.World.Get("skyname"));
        }

        [Fact]
        public void ACloneIsATreeOfItsOwn()
        {
            var map = Cube();
            var copy = map.Clone();

            var original = map.World.Child("solid")!;
            var cloned = copy.World.Child("solid")!;
            Assert.NotSame(original, cloned);
            Assert.Same(copy.World, cloned.Parent);
            Assert.Same(copy.Root, copy.World.Parent);
            Assert.All(cloned.Items, item => Assert.Same(cloned, item.Parent));

            // The unchanged lines themselves are shared, not copied.
            Assert.Same(original.GetPair("id")!.Raw, cloned.GetPair("id")!.Raw);
        }

        /// <summary>A clone keeps what was edited marked as edited, where a re-parse would forget it.</summary>
        [Fact]
        public void ACloneKeepsWhatWasEditedAndAdded()
        {
            var map = Cube();
            map.World.Set("skyname", "sky_night01");
            map.World.Add(new VmfNode("group"));

            var copy = map.Clone();

            Assert.True(copy.World.GetPair("skyname")!.IsModified);
            Assert.False(copy.World.GetPair("classname")!.IsModified);
            Assert.True(copy.World.Child("group")!.IsAdded);
            Assert.False(copy.World.Child("solid")!.IsAdded);
        }

        [Fact]
        public void ACloneKeepsTheIdsAlreadyReserved()
        {
            var map = Cube();
            Assert.Equal(21, map.NextIds(10));

            var copy = map.Clone();
            Assert.Equal(31, copy.NextIds(1));
            Assert.Equal(31, map.NextIds(1));

            // A clone of a map that never reserved anything walks its own blocks.
            var fresh = Cube();
            var freshCopy = fresh.Clone();
            freshCopy.World.Add(Brush(70));
            Assert.Equal(77, freshCopy.NextIds(1));
            Assert.Equal(21, fresh.NextIds(1));
        }

        [Theory]
        [InlineData(@"C:\Users\Catualus\Documents\Meowy Roleplay\DarkRP\maps\rp_downtown_meowy.vmf")]
        [InlineData(@"C:\Users\Catualus\Documents\Meowy Roleplay\DarkRP\maps\rp_downtown_tits_bzgg.vmf")]
        public void ARealMapClonesByteForByteAndNumbersAboveItsIds(string path)
        {
            if (!File.Exists(path))
                return;

            var map = VmfDocument.Load(path);
            var copy = map.Clone();
            Assert.True(map.ToString() == copy.ToString(), "the clone did not write out as the map does");

            long max = map.MaxId();
            Assert.Equal(max + 1, copy.NextIds(8));
            copy.World.Add(Brush(copy.NextIds(7)));
            Assert.Equal(max + 15, copy.MaxId());
            Assert.Equal(max, map.MaxId());
        }
    }
}
