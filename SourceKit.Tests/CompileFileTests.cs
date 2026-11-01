using System.IO;
using SourceKit.Compile;
using SourceKit.Geometry;
using Xunit;

namespace SourceKit.Tests
{
    public class CompileFileTests
    {
        private const string Portals = "PRT1\n3\n2\n4 0 1 (0 0 0 ) (0 64 0 ) (0 64 128 ) (0 0 128 ) \n4 1 2 (64 0 0 ) (64 64 0 ) (64 64 128 ) (64 0 128 ) \n";

        [Fact]
        public void APortalFileReadsClustersAndPortals()
        {
            var file = PortalFile.Read(new StringReader(Portals));

            Assert.Equal(3, file.ClusterCount);
            Assert.Equal(2, file.Portals.Count);
            Assert.Equal(1, file.Portals[1].ClusterA);
            Assert.Equal(2, file.Portals[1].ClusterB);
            Assert.Equal(64 * 128, file.Portals[0].Winding.Area, 6);
        }

        [Theory]
        [InlineData("PRT2\n1\n0\n")]
        [InlineData("PRT1\n1\n1\n")]
        [InlineData("PRT1\n2\n1\n4 0 5 (0 0 0) (0 1 0) (0 1 1) (0 0 1)\n")]
        [InlineData("PRT1\n2\n1\n4 0 1 (0 0 0) (0 1 0)\n")]
        [InlineData("PRT1\nmany\n1\n")]
        public void AMalformedPortalFileIsRefused(string text)
        {
            Assert.Throws<InvalidDataException>(() => PortalFile.Read(new StringReader(text)));
        }

        [Fact]
        public void APointfileGivesTheLeakPathAndHowCloseItPasses()
        {
            var file = PointFile.Read(new StringReader("0 0 0\n100 0 0\n100 100 0\n"));

            Assert.Equal(3, file.Points.Count);
            Assert.Equal(10, file.DistanceTo(new Vec3(50, 10, 0)), 6);
            Assert.Equal(0, file.DistanceTo(new Vec3(100, 50, 0)), 6);
        }

        [Fact]
        public void AMalformedPointfileIsRefused()
        {
            Assert.Throws<InvalidDataException>(() => PointFile.Read(new StringReader("0 0 0\nnot a point\n")));
        }

        [Theory]
        [InlineData(@"C:\Users\Catualus\Documents\Meowy Roleplay\DarkRP\maps\rp_downtown_meowy.prt")]
        [InlineData(@"C:\Users\Catualus\Documents\Prominence\rp_downtown_prominence_d.prt")]
        public void RealPortalFilesRead(string path)
        {
            if (!File.Exists(path))
                return;

            var file = PortalFile.Load(path);

            Assert.True(file.ClusterCount > 1000);
            Assert.True(file.Portals.Count > file.ClusterCount);
        }
    }
}
