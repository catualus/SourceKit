using System;
using System.IO;

namespace SourceKit
{
    /// <summary>
    /// Just enough of a .mdl header to stand in for a missing .phy.
    ///
    /// Most props have a .phy and the real collision hull comes from there. Some do not - a prop set to
    /// SOLID_BBOX collides as a box by definition, and third-party content is sometimes shipped without
    /// its .phy - and for those the model header's collision bounds are the only description of the
    /// prop's shape available.
    ///
    /// **A box is a poor substitute and is used narrowly on purpose.** Filling a prop's bounding box
    /// with solid is wrong in the direction that costs mesh: a fence, a tree or a handrail has a bounding
    /// box many times the volume of anything you could stand on, and treating that as ground invents
    /// walkable surface in mid-air and walls off routes that are actually open. So this is consulted
    /// only when the prop itself claims to be a box, or when it claims VPHYSICS and the .phy is genuinely
    /// absent - never in preference to a hull that exists.
    /// </summary>
    public sealed class StudioModel
    {
        /// <summary>'IDST', the studio model identifier.</summary>
        private const int StudioId = 0x54534449;

        /// <summary>Byte offset of hull_min within studiohdr_t, after id, version, checksum and name.</summary>
        private const int HullMinOffset = 104;

        public bool Valid { get; private set; }
        public int Version { get; private set; }

        /// <summary>The model's collision bounds, in model space.</summary>
        public BspFile.Vector3 HullMin { get; private set; }
        public BspFile.Vector3 HullMax { get; private set; }

        /// <summary>Whether the header's flags mark this as usable as a static prop.</summary>
        public bool StaticProp { get; private set; }

        /// <summary>The studiohdr flags word: STUDIOHDR_FLAGS_STATIC_PROP is 0x10, NO_FORCED_FADE 0x800.</summary>
        public int Flags { get; private set; }

        /// <summary>The model's $keyvalues text (prop_data and the like), or empty.</summary>
        public string KeyValues { get; private set; } = "";

        /// <summary>
        /// Whether VBSP will place this model as a prop_static: it must be compiled with $staticprop, and
        /// if it carries a prop_data block (a physics prop) that block must say allowstatic. Otherwise
        /// VBSP deletes the prop: "must be used on a dynamic entity (i.e. prop_physics). Deleted."
        /// </summary>
        public bool UsableAsStaticProp
        {
            get
            {
                if (!StaticProp)
                    return false;
                string text = KeyValues.ToLowerInvariant();
                int at = text.IndexOf("prop_data", StringComparison.Ordinal);
                if (at < 0)
                    return true;
                int open = text.IndexOf('{', at);
                int close = open < 0 ? -1 : text.IndexOf('}', open);
                if (close < 0)
                    return false;
                string block = text[open..close];
                int key = block.IndexOf("allowstatic", StringComparison.Ordinal);
                if (key < 0)
                    return false;
                string rest = block[(key + "allowstatic".Length)..].TrimStart(' ', '	', '"');
                return rest.Length > 0 && rest[0] != '0';
            }
        }

        /// <summary>The model's material names, without folder, in the order its texture table lists them.</summary>
        public System.Collections.Generic.IReadOnlyList<string> TextureNames { get; private set; } = [];

        /// <summary>The folders under materials/ the engine looks in for each texture name, in order.</summary>
        public System.Collections.Generic.IReadOnlyList<string> TextureFolders { get; private set; } = [];

        /// <summary>STUDIOHDR_FLAGS_STATIC_PROP.</summary>
        private const int FlagStaticProp = 0x10;

        public static StudioModel Parse(byte[] bytes)
        {
            var model = new StudioModel();

            if (bytes.Length < HullMinOffset + 24 + 8) return model;

            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms);

            if (r.ReadInt32() != StudioId) return model;

            model.Version = r.ReadInt32();

            // Version is checked loosely. Everything from Half-Life 2 to the present writes the header
            // prefix identically up to the bounding boxes, and rejecting an unfamiliar number would drop
            // collision for content that parses perfectly.
            if (model.Version is < 44 or > 60) return model;

            ms.Seek(HullMinOffset, SeekOrigin.Begin);

            model.HullMin = new BspFile.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            model.HullMax = new BspFile.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            // view_bbmin and view_bbmax sit between the hull and the flags.
            ms.Seek(HullMinOffset + 24 + 24, SeekOrigin.Begin);
            model.Flags = r.ReadInt32();
            model.StaticProp = (model.Flags & FlagStaticProp) != 0;

            ReadTextures(bytes, model);
            ReadKeyValues(bytes, model);

            // A model with no collision bounds at all - both corners zero - describes nothing, and
            // returning it as a valid degenerate box would place a point-sized solid at the origin.
            model.Valid = model.HullMax.X > model.HullMin.X
                       && model.HullMax.Y > model.HullMin.Y
                       && model.HullMax.Z > model.HullMin.Z;

            return model;
        }

        /// <summary>
        /// The collision bounds as twelve triangles, so a box-collided prop can join the same triangle
        /// soup as everything else rather than needing its own trace path.
        /// </summary>
        /// <summary>
        /// The texture table (numtextures and textureindex at 204, each entry 64 bytes starting with
        /// the offset of its name from the entry) and the folder table (numcdtextures and
        /// cdtextureindex at 212, offsets of strings from the start of the file). A table that does
        /// not fit the file is ignored rather than read past its end.
        /// </summary>
        private static void ReadTextures(byte[] bytes, StudioModel model)
        {
            if (bytes.Length < 220)
                return;
            int count = BitConverter.ToInt32(bytes, 204), index = BitConverter.ToInt32(bytes, 208);
            int folders = BitConverter.ToInt32(bytes, 212), folderIndex = BitConverter.ToInt32(bytes, 216);
            if (count < 0 || count > 1024 || index < 0 || index + count * 64L > bytes.Length
                || folders < 0 || folders > 1024 || folderIndex < 0 || folderIndex + folders * 4L > bytes.Length)
                return;

            var names = new System.Collections.Generic.List<string>(count);
            for (int i = 0; i < count; i++)
            {
                int entry = index + i * 64;
                string? name = ReadString(bytes, (long)entry + BitConverter.ToInt32(bytes, entry));
                if (name != null) names.Add(name);
            }
            var paths = new System.Collections.Generic.List<string>(folders);
            for (int i = 0; i < folders; i++)
            {
                string? folder = ReadString(bytes, BitConverter.ToInt32(bytes, folderIndex + i * 4));
                if (folder != null) paths.Add(folder);
            }
            model.TextureNames = names;
            model.TextureFolders = paths;
        }

        /// <summary>keyvalueindex and keyvaluesize at 312 and 316: the model's $keyvalues, as text.</summary>
        private static void ReadKeyValues(byte[] bytes, StudioModel model)
        {
            if (bytes.Length < 320)
                return;
            int index = BitConverter.ToInt32(bytes, 312), size = BitConverter.ToInt32(bytes, 316);
            if (index <= 0 || size <= 0 || index + (long)size > bytes.Length || size > 1 << 20)
                return;
            model.KeyValues = System.Text.Encoding.ASCII.GetString(bytes, index, size).TrimEnd(' ');
        }

        private static string? ReadString(byte[] bytes, long offset)
        {
            if (offset < 0 || offset >= bytes.Length)
                return null;
            int end = Array.IndexOf(bytes, (byte)0, (int)offset);
            if (end < 0 || end - offset > 260)
                return null;
            return System.Text.Encoding.ASCII.GetString(bytes, (int)offset, end - (int)offset);
        }

        public BspFile.Vector3[] AsTriangles()
        {
            if (!Valid) return [];

            float x0 = HullMin.X, y0 = HullMin.Y, z0 = HullMin.Z;
            float x1 = HullMax.X, y1 = HullMax.Y, z1 = HullMax.Z;

            var c = new BspFile.Vector3[8];
            for (int i = 0; i < 8; i++)
                c[i] = new BspFile.Vector3((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);

            // Corner indices for the twelve triangles, two per face, wound outwards.
            ReadOnlySpan<byte> faces =
            [
                0, 2, 3,  0, 3, 1,      // z low
                4, 5, 7,  4, 7, 6,      // z high
                0, 1, 5,  0, 5, 4,      // y low
                2, 6, 7,  2, 7, 3,      // y high
                0, 4, 6,  0, 6, 2,      // x low
                1, 3, 7,  1, 7, 5,      // x high
            ];

            var tris = new BspFile.Vector3[faces.Length];
            for (int i = 0; i < faces.Length; i++) tris[i] = c[faces[i]];

            return tris;
        }
    }
}
