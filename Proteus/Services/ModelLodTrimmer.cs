using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Cuts a model down to its first level of detail, so the geometry an edit moved is what the game draws at every
/// distance. A model may carry up to three: LOD0 up close, coarser copies further out. The game's own gear carries all
/// three (switching at roughly 20-50 and 70-150 units), and so do mods built from it — 395 of 3,000 gear models in one
/// install. An edit that moves only LOD0 leaves the other two where they were, so a garment refitted to a new body
/// showed the old size from a distance.
/// <para/>
/// Everything a later level owns comes AFTER LOD0's in every table: its meshes, their vertex declarations, submeshes and
/// bone tables, and its vertex and index data at the end of the file. So the cut keeps a prefix of each table and
/// moves nothing LOD0 refers to — only the shape tables are rebuilt, because a shape lists its meshes per level. What
/// does not fit that layout is refused, and the model is left as it was.
/// <para/>
/// Shape keys stay: they are LOD0's too. That is why this is not the second-skin rebuild, which drops them.
/// </summary>
internal static class ModelLodTrimmer
{
    private const int FileHeaderSize = 0x44;
    private const int LodSize = 60, MeshSize = 36, SubmeshSize = 16;
    private const int ShapeSize = 16, ShapeMeshSize = 12, ShapeValueSize = 4;

    /// <summary>How many levels of detail the file header declares, or 0 for a file too short to have one.</summary>
    public static int LodCount(byte[] mdl) => mdl.Length > 64 ? mdl[64] : 0;

    /// <summary>
    /// The model with only LOD0, or null when it has one already or cannot be cut, with the reason in
    /// <paramref name="refusal"/>.
    /// </summary>
    public static byte[]? KeepLod0(byte[] mdl, out string refusal)
    {
        refusal = "";
        int lods = LodCount(mdl);
        if (lods <= 1)
        {
            refusal = "it has one level of detail already";
            return null;
        }

        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(mdl); }
        catch (Exception ex)
        {
            refusal = $"it could not be read ({ex.Message})";
            return null;
        }

        try { return Cut(mdl, src, lods, out refusal); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or IOException)
        {
            refusal = $"its tables are not where the format says they are ({ex.Message})";
            return null;
        }
    }

    private static byte[]? Cut(byte[] s, SecondSkinWriter.Source src, int lods, out string refusal)
    {
        refusal = "";
        int mh = src.Mh;
        bool v6 = U32(s, 0) >= SecondSkinWriter.MdlVersionV6;
        ushort meshCount = U16(s, mh + 4), attrCount = U16(s, mh + 6), submeshCount = U16(s, mh + 8);
        ushort matCount = U16(s, mh + 10), boneCount = U16(s, mh + 12), tableCount = U16(s, mh + 14);
        ushort shapeCount = U16(s, mh + 16), shapeMeshCount = U16(s, mh + 18), shapeValueCount = U16(s, mh + 20);

        // ── what this cut understands ────────────────────────────────────────
        if ((s[mh + 27] & 0x10) != 0) return Refuse("it carries extra levels of detail", out refusal);
        if (s[mh + 26] != 0 || U16(s, mh + 38) != 0) return Refuse("it has terrain shadow meshes", out refusal);
        if (U16(s, 12) != meshCount) return Refuse("its vertex declarations do not match its meshes", out refusal);

        int lod0 = src.LodStart;
        int n = U16(s, lod0 + 2);
        if (U16(s, lod0) != 0 || n == 0 || n > meshCount)
            return Refuse("its first level of detail is not its first meshes", out refusal);
        for (int l = 0; l < 3; l++)
        {
            int at = lod0 + l * LodSize;
            // Water, shadow, terrain shadow and fog meshes: none on gear, and each would need its own range kept.
            if (U16(s, at + 14) != 0 || U16(s, at + 18) != 0 || U16(s, at + 22) != 0 || U16(s, at + 26) != 0)
                return Refuse("it has special-purpose meshes", out refusal);
            if (U32(s, at + 28) != 0) return Refuse("it has edge geometry", out refusal);
            if (l > 0 && l < lods)
            {
                int start = U16(s, at), count = U16(s, at + 2);
                if (count > 0 && (start < n || start + count > meshCount))
                    return Refuse("its levels of detail share meshes", out refusal);
            }
        }

        // LOD0's vertex and index data, copied as one run; no other level may sit inside it.
        long vb = U32(s, 16), ib = U32(s, 28), vSize = U32(s, 40), iSize = U32(s, 52);
        long dataEnd = ib + iSize;
        if (vb != src.Vb || ib < vb + vSize || dataEnd > s.Length)
            return Refuse("its vertex data is not laid out in order", out refusal);
        for (int l = 1; l < 3; l++)
        {
            long ov = U32(s, 16 + l * 4), oi = U32(s, 28 + l * 4);
            if (U32(s, 40 + l * 4) > 0 && ov >= vb && ov < dataEnd) return Refuse("its levels of detail share data", out refusal);
            if (U32(s, 52 + l * 4) > 0 && oi >= vb && oi < dataEnd) return Refuse("its levels of detail share data", out refusal);
        }

        // What LOD0's meshes reach: a prefix of the submeshes and of the bone tables.
        int keptSubmeshes = 0, keptTables = 0;
        for (int m = 0; m < n; m++)
        {
            int mo = src.MeshStart + m * MeshSize;
            keptSubmeshes = Math.Max(keptSubmeshes, U16(s, mo + 10) + U16(s, mo + 12));
            int table = U16(s, mo + 14);
            if (table < tableCount) keptTables = Math.Max(keptTables, table + 1);
        }
        if (keptSubmeshes > submeshCount) return Refuse("its meshes name submeshes it does not have", out refusal);

        // Where the tail blocks sit, walked the way the parser walks them, and checked against where it landed.
        int boneTablesAt = src.MatOffStart + matCount * 4 + boneCount * 4;
        int shapesEnd = src.ShapeBlock + shapeCount * ShapeSize + shapeMeshCount * ShapeMeshSize +
                        shapeValueCount * ShapeValueSize;
        int padAt = shapesEnd + 4 + (int)U32(s, shapesEnd);
        if (v6) padAt += s[mh + 43] * 32 + U16(s, mh + 48) * 16;
        if (padAt + 1 + s[padAt] != src.ModelBBoxAt || src.ModelBBoxAt > vb)
            return Refuse("its tables are not where the format says they are", out refusal);

        // ── the cut ──────────────────────────────────────────────────────────
        using var ms = new MemoryStream(s.Length);
        ms.Write(s, 0, FileHeaderSize);                                                // patched below
        ms.Write(s, FileHeaderSize, n * SecondSkinWriter.DeclSize);                   // one declaration per mesh
        ms.Write(s, src.DeclEnd, mh - src.DeclEnd);                                   // string count, size, strings
        long mhOut = ms.Position;
        ms.Write(s, mh, src.LodStart - mh);                                           // model header, element ids
        long lodOut = ms.Position;
        ms.Write(s, src.LodStart, 3 * LodSize);                                       // patched below
        ms.Write(s, src.MeshStart, n * MeshSize);
        ms.Write(s, src.AttrStart, attrCount * 4);                                    // no terrain shadow meshes
        ms.Write(s, src.SubmeshStart, keptSubmeshes * SubmeshSize);
        ms.Write(s, src.MatOffStart, (matCount + boneCount) * 4);                     // material, bone names

        int tableShorts = 0;
        if (v6)
        {
            var tables = src.BoneTables.Take(keptTables).ToList();
            SecondSkinWriter.WriteBoneTablesV6(ms, tables);
            tableShorts = tables.Sum(t => (t.Length + 1) & ~1);
        }
        else
        {
            ms.Write(s, boneTablesAt, keptTables * SecondSkinWriter.V5BoneTableBytes);
        }

        // Shapes: each keeps its LOD0 meshes, renumbered; the other levels' ranges are left empty past them.
        int shapeMeshAt = src.ShapeBlock + shapeCount * ShapeSize;
        int shapeValueAt = shapeMeshAt + shapeMeshCount * ShapeMeshSize;
        var shapeRecords = new byte[shapeCount * ShapeSize];
        using var shapeMeshes = new MemoryStream();
        using var shapeValues = new MemoryStream();
        int keptShapeMeshes = 0, keptShapeValues = 0;
        for (int si = 0; si < shapeCount; si++)
        {
            int shp = src.ShapeBlock + si * ShapeSize;
            int start = U16(s, shp + 4), count = U16(s, shp + 10);
            if (start + count > shapeMeshCount) return Refuse("a shape names meshes it does not have", out refusal);

            Array.Copy(s, shp, shapeRecords, si * ShapeSize, 4);                      // name offset
            W16(shapeRecords, si * ShapeSize + 4, (ushort)keptShapeMeshes);
            W16(shapeRecords, si * ShapeSize + 10, (ushort)count);
            for (int k = 0; k < count; k++)
            {
                int sm = shapeMeshAt + (start + k) * ShapeMeshSize;
                uint values = U32(s, sm + 4), first = U32(s, sm + 8);
                if (first + values > shapeValueCount) return Refuse("a shape names values it does not have", out refusal);

                var record = new byte[ShapeMeshSize];
                Array.Copy(s, sm, record, 0, 8);                                        // mesh index offset, count
                W32(record, 8, (uint)keptShapeValues);
                shapeMeshes.Write(record);
                shapeValues.Write(s, shapeValueAt + (int)first * ShapeValueSize, (int)values * ShapeValueSize);
                keptShapeValues += (int)values;
            }
            keptShapeMeshes += count;
        }
        if (keptShapeMeshes > ushort.MaxValue || keptShapeValues > ushort.MaxValue)
            return Refuse("its shapes are too large", out refusal);
        for (int si = 0; si < shapeCount; si++)
        {
            W16(shapeRecords, si * ShapeSize + 6, (ushort)keptShapeMeshes);
            W16(shapeRecords, si * ShapeSize + 8, (ushort)keptShapeMeshes);
            W16(shapeRecords, si * ShapeSize + 12, 0);
            W16(shapeRecords, si * ShapeSize + 14, 0);
        }
        ms.Write(shapeRecords);
        shapeMeshes.Position = 0; shapeMeshes.CopyTo(ms);
        shapeValues.Position = 0; shapeValues.CopyTo(ms);

        // The submesh bone map (whole: LOD0's submeshes index into it) and a face's two tables, verbatim.
        ms.Write(s, shapesEnd, padAt - shapesEnd);

        // Padding chosen so the data moves by a multiple of 16, keeping every alignment the author's file had.
        long bboxes = vb - src.ModelBBoxAt;
        long afterPadByte = ms.Position + 1;
        int pad = (int)(((vb - (afterPadByte + bboxes)) % 16 + 16) % 16);
        ms.WriteByte((byte)pad);
        for (int i = 0; i < pad; i++) ms.WriteByte(0);
        ms.Write(s, src.ModelBBoxAt, (int)bboxes);                                    // bounding boxes

        long vbOut = ms.Position;
        ms.Write(s, (int)vb, (int)(dataEnd - vb));                                    // LOD0's vertices, indices
        var o = ms.ToArray();
        long ibOut = ib + (vbOut - vb);

        // ── file header ──────────────────────────────────────────────────────
        uint stack = (uint)(n * SecondSkinWriter.DeclSize);
        W32(o, 4, stack);
        W32(o, 8, (uint)(vbOut - FileHeaderSize - stack));                            // runtime size
        W16(o, 12, (ushort)n);
        for (int l = 0; l < 3; l++)
        {
            W32(o, 16 + l * 4, l == 0 ? (uint)vbOut : 0);
            W32(o, 28 + l * 4, l == 0 ? (uint)ibOut : 0);
            if (l > 0) { W32(o, 40 + l * 4, 0); W32(o, 52 + l * 4, 0); }
        }
        o[64] = 1;

        // ── model header ─────────────────────────────────────────────────────
        int mo2 = (int)mhOut;
        W16(o, mo2 + 4, (ushort)n);
        W16(o, mo2 + 8, (ushort)keptSubmeshes);
        W16(o, mo2 + 14, (ushort)keptTables);
        W16(o, mo2 + 18, (ushort)keptShapeMeshes);
        W16(o, mo2 + 20, (ushort)keptShapeValues);
        o[mo2 + 22] = 1;
        if (v6) W16(o, mo2 + 44, (ushort)tableShorts);                               // BoneTableArrayCountTotal

        // ── levels of detail ─────────────────────────────────────────────────
        // LOD0 is now the last level, and the last level's distance is 0: drawn however far away (as the game's own
        // LOD2 is). The empty levels start past LOD0's meshes, as a one-level model's do.
        int lo = (int)lodOut;
        BitConverter.GetBytes(0f).CopyTo(o, lo + 4);                                  // model LOD range
        BitConverter.GetBytes(0f).CopyTo(o, lo + 8);                                  // texture LOD range
        W16(o, lo + 12, (ushort)n);                                                   // water, shadow, fog: empty
        W16(o, lo + 16, (ushort)n);
        W16(o, lo + 24, (ushort)n);
        W32(o, lo + 52, (uint)vbOut);
        W32(o, lo + 56, (uint)ibOut);
        for (int l = 1; l < 3; l++)
        {
            int at = lo + l * LodSize;
            Array.Clear(o, at, LodSize);
            W16(o, at, (ushort)n);
            W16(o, at + 12, (ushort)n);
            W16(o, at + 16, (ushort)n);
            W16(o, at + 24, (ushort)n);
        }

        // Read back the way everything downstream will read it.
        try
        {
            var back = SecondSkinWriter.Parse(o);
            if (back.MeshCount != n || back.Vb != vbOut || ModelPartReader.Read(o) == null)
                return Refuse("the cut model did not read back", out refusal);
        }
        catch (Exception ex)
        {
            return Refuse($"the cut model did not read back ({ex.Message})", out refusal);
        }
        return o;
    }

    private static byte[]? Refuse(string why, out string refusal)
    {
        refusal = why;
        return null;
    }

    private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);

    private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);

    private static void W16(byte[] b, int o, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);

    private static void W32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
}
