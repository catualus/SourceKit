using System;
using System.IO;
using System.Linq;
using System.Text;
using SourceKit.Geometry;
using SourceKit.Vmf;
using Xunit;

namespace SourceKit.Tests
{
    public class VmfTests
    {
        /// <summary>A 64-unit cube from the origin, sides written the way Hammer writes them.</summary>
        internal const string Cube = """
            versioninfo
            {
            	"editorversion" "400"
            	"mapversion" "3"
            }
            world
            {
            	"id" "1"
            	"classname" "worldspawn"
            	"skyname" "sky_day01_01"
            	solid
            	{
            		"id" "2"
            		side
            		{
            			"id" "10"
            			"plane" "(0 64 64) (64 64 64) (64 0 64)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "11"
            			"plane" "(0 0 0) (64 0 0) (64 64 0)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "12"
            			"plane" "(0 64 64) (0 0 64) (0 0 0)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "13"
            			"plane" "(64 64 0) (64 0 0) (64 0 64)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "14"
            			"plane" "(64 64 64) (0 64 64) (0 64 0)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "15"
            			"plane" "(64 0 0) (0 0 0) (0 0 64)"
            			"material" "TOOLS/TOOLSNODRAW"
            		}
            	}
            }
            entity
            {
            	"id" "20"
            	"classname" "info_player_start"
            	"origin" "32 32 100"
            }

            """;

        /// <summary>The cube with CRLF endings, whichever endings this source file was checked out with.</summary>
        internal static string CubeText => Cube.Replace("\r\n", "\n").Replace("\n", "\r\n");

        internal static VmfDocument CubeDocument() => VmfDocument.Parse(CubeText);

        [Fact]
        public void AnUneditedDocumentWritesBackByteForByte()
        {
            string text = CubeText;
            Assert.Equal(text, VmfDocument.Parse(text).ToString());
        }

        [Fact]
        public void AppendRepeatsAKeyAndInsertBeforePlacesABlock()
        {
            var doc = VmfDocument.Parse(CubeText);
            var editor = new VmfNode("editor");
            doc.World.Add(editor);
            editor.Append("visgroupid", "3");
            editor.Append("visgroupid", "7");
            Assert.Equal(["3", "7"], editor.KeyValues.Where(kv => kv.Key == "visgroupid").Select(kv => kv.Value));

            doc.Root.InsertBefore(doc.World, new VmfNode("visgroups"));
            var names = doc.Root.Nodes.Select(n => n.Name).ToList();
            Assert.Equal(names.IndexOf("world") - 1, names.IndexOf("visgroups"));

            var reread = VmfDocument.Parse(doc.ToString());
            Assert.Equal(2, reread.World.Child("editor")!.KeyValues.Count(kv => kv.Key == "visgroupid"));
        }

        [Fact]
        public void OddFormattingSurvivesTheRoundTrip()
        {
            // Spaces instead of tabs, a brace on the block's own line, a blank line, LF endings, no
            // final newline: none of it is Hammer's, all of it is in maps people have.
            string text = "world\n{\n  \"id\"   \"1\"\n\n  solid {\n    \"id\" \"2\"\n  }\n}";
            var document = VmfDocument.Parse(text);

            Assert.Equal(text, document.ToString());
            Assert.Equal("2", document.World.Child("solid")!.Get("id"));
        }

        [Fact]
        public void AByteOrderMarkAndNonAsciiTextAreKept()
        {
            byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("world\r\n{\r\n\t\"comment\" \"caf\u00e9\"\r\n}\r\n")];
            var document = VmfDocument.Parse(bytes);

            using var stream = new MemoryStream();
            document.Write(stream);
            Assert.Equal(bytes, stream.ToArray());
        }

        [Fact]
        public void EditingAValueRewritesOnlyThatLine()
        {
            var document = CubeDocument();
            string before = document.ToString();

            document.World.Set("skyname", "sky_night01");

            var changed = before.Split("\r\n").Zip(document.ToString().Split("\r\n"))
                .Where(pair => pair.First != pair.Second).ToList();
            Assert.Single(changed);
            Assert.Equal("\t\"skyname\" \"sky_night01\"", changed[0].Second);
        }

        [Fact]
        public void AddedKeysAndBlocksFollowTheFilesIndentation()
        {
            var document = CubeDocument();
            var entity = new VmfNode("entity");
            document.Root.Add(entity);
            entity.Set("id", "21");
            entity.Set("classname", "func_detail");

            document.World.Set("detailvbsp", "detail.vbsp");

            string text = document.ToString();
            Assert.Contains("\t\"skyname\" \"sky_day01_01\"\r\n\t\"detailvbsp\" \"detail.vbsp\"\r\n\tsolid", text);
            Assert.EndsWith("entity\r\n{\r\n\t\"id\" \"21\"\r\n\t\"classname\" \"func_detail\"\r\n}\r\n", text);

            // And what was written reads back as what was meant.
            var reread = VmfDocument.Parse(text);
            Assert.Equal("func_detail", reread.Entities.Last().Classname);
        }

        [Fact]
        public void MaxIdCoversEveryBlock()
        {
            Assert.Equal(20, CubeDocument().MaxId());
        }

        [Theory]
        [InlineData("world\r\n{\r\n")]
        [InlineData("world\r\n}\r\n")]
        [InlineData("}\r\n")]
        [InlineData("world\r\n\"id\" \"1\"\r\n")]
        [InlineData("world\r\n{\r\n\t\"id\r\n}\r\n")]
        public void MalformedTextIsRefused(string text)
        {
            Assert.Throws<InvalidDataException>(() => VmfDocument.Parse(text));
        }

        [Fact]
        public void ValuesThatWouldCorruptTheFileAreRefused()
        {
            var document = CubeDocument();
            Assert.Throws<ArgumentException>(() => document.World.Set("skyname", "a\"b"));
            Assert.Throws<ArgumentException>(() => document.World.Set("skyname", "a\nb"));
        }

        [Fact]
        public void ACubeBecomesSixSquareFaces()
        {
            var brush = VmfBrush.Build(CubeDocument().World.Child("solid")!)!;

            Assert.Equal(6, brush.Faces.Count);
            Assert.True(brush.IsAxialBox);
            Assert.Equal(new Vec3(0, 0, 0), brush.Mins);
            Assert.Equal(new Vec3(64, 64, 64), brush.Maxs);
            Assert.Equal(64 * 64 * 64, brush.Volume, 3);
            Assert.All(brush.Faces, f => Assert.Equal(64 * 64, f.Winding!.Area, 3));

            // Each face points out of the cube, and its polygon faces the same way as its plane.
            var top = brush.Faces.Single(f => f.Side.Id == 10);
            Assert.Equal(new Vec3(0, 0, 1), top.Plane.Normal);
            var w = top.Winding!.Points;
            Assert.True((w[1] - w[0]).Cross(w[2] - w[0]).Dot(top.Plane.Normal) > 0);
            Assert.All(w, p => Assert.Equal(64, p.Z, 6));
        }

        [Fact]
        public void ACutCornerGivesAWedgeWithTheRightVolume()
        {
            // The cube with a diagonal side cutting off half of it: a prism with half the volume.
            var document = CubeDocument();
            var solid = document.World.Child("solid")!;
            var cut = new VmfNode("side");
            solid.Add(cut);
            cut.Set("id", "16");
            // The plane x + y = 64, facing away from the origin.
            cut.Set("plane", "(64 0 0) (0 64 0) (0 64 64)");
            cut.Set("material", "DEV/DEV_MEASUREWALL01A");

            var brush = VmfBrush.Build(solid)!;

            Assert.Equal(64 * 64 * 64 / 2.0, brush.Volume, 3);
            Assert.False(brush.IsAxialBox);
            // The two sides the cut removed entirely leave no polygon.
            Assert.Equal(2, brush.Faces.Count(f => f.Winding == null));
        }

        [Fact]
        public void ABrushThatDoesNotCloseIsRejected()
        {
            var solid = CubeDocument().World.Child("solid")!;
            solid.Remove(solid.Children("side").First());

            Assert.Null(VmfBrush.Build(solid));
        }

        [Fact]
        public void TheDiffNamesOnlyWhatChanged()
        {
            var before = CubeDocument();
            var after = CubeDocument();
            after.World.Child("solid")!.Children("side").First().Set("material", "TOOLS/TOOLSNODRAW");
            var entity = new VmfNode("entity");
            after.Root.Add(entity);
            entity.Set("id", "30");
            entity.Set("classname", "func_areaportal");

            var changes = VmfDiff.Compare(before, after);

            Assert.Equal(2, changes.Count);
            Assert.Contains(changes, c => c.Kind == VmfChangeKind.ValueChanged
                && c.Path == "world#1/solid#2/side#10/material" && c.After == "TOOLS/TOOLSNODRAW");
            Assert.Contains(changes, c => c.Kind == VmfChangeKind.Added && c.Path == "entity#30");
        }

        /// <summary>
        /// Real maps repeat ids. Removing one block must not change how the repeats after it are named,
        /// or an untouched brush shows up as removed and added again - an undeclared change, which stops
        /// a Viswright compile.
        /// </summary>
        [Fact]
        public void RemovingABlockDoesNotRenameRepeatedIdsAfterIt()
        {
            static string Map(bool withFirst) =>
                "world\n{\n\t\"id\" \"1\"\n\t\"classname\" \"worldspawn\"\n" +
                (withFirst ? "\tsolid\n\t{\n\t\t\"id\" \"2\"\n\t}\n" : "") +
                "\tsolid\n\t{\n\t\t\"id\" \"5\"\n\t\t\"note\" \"first\"\n\t}\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"5\"\n\t\t\"note\" \"second\"\n\t}\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"5\"\n\t\t\"note\" \"third\"\n\t}\n" +
                "}\n";

            var changes = VmfDiff.Compare(VmfDocument.Parse(Map(withFirst: true)), VmfDocument.Parse(Map(withFirst: false)));

            var change = Assert.Single(changes);
            Assert.Equal(VmfChangeKind.Removed, change.Kind);
            Assert.Equal("world#1/solid#2", change.Path);
        }

        /// <summary>Repeats are numbered per id, in file order, so a pass can name the one it changed.</summary>
        [Fact]
        public void RepeatedIdsAreNumberedInFileOrder()
        {
            const string map = "world\n{\n\t\"id\" \"1\"\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"5\"\n\t}\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"6\"\n\t}\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"5\"\n\t}\n" +
                "\tsolid\n\t{\n\t\t\"id\" \"5\"\n\t\t\"note\" \"x\"\n\t}\n" +
                "}\n";

            var before = VmfDocument.Parse(map);
            var after = VmfDocument.Parse(map);
            after.World.Children("solid").Last().Set("note", "y");

            var change = Assert.Single(VmfDiff.Compare(before, after));
            Assert.Equal("world#1/solid#5~2/note", change.Path);
        }

        [Fact]
        public void IdenticalMapsHaveNoDiff()
        {
            Assert.Empty(VmfDiff.Compare(CubeDocument(), CubeDocument()));
        }

        /// <summary>
        /// The real maps this library is used on, when they are on this machine. They are tens of MB of
        /// whatever their editors wrote, which is the round trip that matters.
        /// </summary>
        [Theory]
        [InlineData(@"C:\Users\Catualus\Documents\Meowy Roleplay\DarkRP\maps\rp_downtown_meowy.vmf")]
        [InlineData(@"C:\Users\Catualus\Documents\Prominence\rp_downtown_prominence_d.vmf")]
        public void RealMapsRoundTripAndBuild(string path)
        {
            if (!File.Exists(path))
                return;

            byte[] bytes = File.ReadAllBytes(path);
            var document = VmfDocument.Parse(bytes);
            using var stream = new MemoryStream();
            document.Write(stream);
            Assert.True(bytes.AsSpan().SequenceEqual(stream.ToArray()), "the map did not write back byte for byte");

            int built = 0, failed = 0;
            foreach (var solid in document.World.Children("solid"))
            {
                if (VmfBrush.Build(solid) is not null) built++;
                else failed++;
            }
            Assert.True(built > 1000);
            // Hammer refuses to save an invalid solid, so nearly every brush must build.
            Assert.True(failed <= built / 1000, $"{failed} of {built + failed} world brushes did not build");
        }
    }
}
