using System;
using System.IO;

namespace SourceKit
{
    /// <summary>
    /// The header every BSP reader starts from: the ident, the version, the 64-entry lump table and the
    /// map revision, read and checked in one place.
    ///
    /// Each reader used to parse the table itself and none checked the version, so a map from a branch
    /// with another layout was read as if it were ours and failed somewhere far from the cause, or did
    /// not fail at all. Left 4 Dead 2 is the case that matters: it is version 21 like CS:GO, but writes
    /// each lump entry as version, offset, length rather than offset, length, version. Read the usual way,
    /// every lump starts inside the header and is the wrong length, and the readers clamp that into
    /// plausible-looking garbage. Such a map is refused here, by name, instead.
    /// </summary>
    internal sealed class BspHeader
    {
        /// <summary>'VBSP', little-endian.</summary>
        public const int Ident = 0x50534256;

        public const int LumpCount = 64;

        /// <summary>The versions read: 19 and 20 (Garry's Mod, the Orange Box games) and 21 (CS:GO, Portal 2).</summary>
        public const int MinVersion = 19, MaxVersion = 21;

        /// <summary>Ident, version, the lump table, map revision: where the first lump may start.</summary>
        public const int Size = 4 + 4 + LumpCount * 16 + 4;

        /// <summary>One entry of the lump table, as the file has it.</summary>
        public readonly record struct Lump(int Offset, int Length, int Version, int FourCC);

        public int Version { get; }

        public int MapRevision { get; }

        private readonly Lump[] lumps;

        private BspHeader(int version, Lump[] lumps, int mapRevision)
        {
            Version = version;
            this.lumps = lumps;
            MapRevision = mapRevision;
        }

        public Lump this[int index] => lumps[index];

        /// <summary>
        /// Reads the header from the start of <paramref name="r"/>'s stream, leaving the stream just past
        /// it. <paramref name="source"/> names the file in errors.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The stream is not a Source BSP, is too short to hold a header, or is a version or lump layout
        /// that is not read here.
        /// </exception>
        public static BspHeader Read(BinaryReader r, string source)
        {
            var stream = r.BaseStream;
            stream.Seek(0, SeekOrigin.Begin);
            if (stream.Length < Size)
                throw new InvalidDataException($"{source} is not a Source BSP: it is too short to hold a BSP header.");

            int ident = r.ReadInt32();
            if (ident != Ident)
                throw new InvalidDataException($"{source} is not a Source BSP: ident 0x{ident:X8}.");

            int version = r.ReadInt32();
            if (version is < MinVersion or > MaxVersion)
                throw new InvalidDataException(
                    $"{source} is an unsupported BSP version/layout: version {version}, and only {MinVersion} to {MaxVersion} are read.");

            var lumps = new Lump[LumpCount];
            for (int i = 0; i < LumpCount; i++)
                lumps[i] = new Lump(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            int mapRevision = r.ReadInt32();

            if (IsVersionFirst(lumps, stream.Length))
                throw new InvalidDataException(
                    $"{source} is an unsupported BSP version/layout: its lump table is in Left 4 Dead 2's " +
                    "version-first order (version, offset, length), which is not read.");

            return new BspHeader(version, lumps, mapRevision);
        }

        /// <summary>
        /// The lump's bytes, decompressed if it is LZMA-tagged; empty for a lump the table gives no length.
        /// </summary>
        public byte[] ReadLump(BinaryReader r, int index)
        {
            var lump = lumps[index];
            return lump.Length <= 0 ? [] : LzmaLump.Read(r, lump.Offset, lump.Length);
        }

        /// <summary>
        /// Whether the table is in the version-first order: read the usual way, some lump with data
        /// starts inside the header, which no compiler writes; read version-first, every lump with data
        /// starts after the header and ends inside the file, with a small version number.
        ///
        /// Both conditions, so a map in the usual order with one damaged entry is not mistaken for one,
        /// and keeps being read as it always was.
        /// </summary>
        private static bool IsVersionFirst(Lump[] lumps, long fileLength)
        {
            bool startsInHeader = false;
            foreach (var lump in lumps)
                if (lump.Length > 0 && lump.Offset < Size)
                    startsInHeader = true;
            if (!startsInHeader)
                return false;

            // The same sixteen bytes, taken as version, offset, length, fourCC.
            bool any = false;
            foreach (var lump in lumps)
            {
                int version = lump.Offset, offset = lump.Length, length = lump.Version;
                if (length == 0)
                    continue;
                if (length < 0 || version is < 0 or > 255 || offset < Size || (long)offset + length > fileLength)
                    return false;
                any = true;
            }
            return any;
        }
    }
}
