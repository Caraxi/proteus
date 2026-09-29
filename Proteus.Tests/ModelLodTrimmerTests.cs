using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Cutting a model to its first level of detail, on the game's own gear — which carries three — read out of sqpack.
/// Skipped where the game is not installed. LOD0 must come through untouched: the same geometry, the same vertex and
/// index bytes, the same shape keys; only the other levels go.
/// </summary>
public class ModelLodTrimmerTests(ITestOutputHelper output)
{
    private static readonly string[] VanillaGear =
    [
        "chara/equipment/e6200/model/c0201e6200_top.mdl",   // three shapes
        "chara/equipment/e0010/model/c0101e0010_top.mdl",
        "chara/equipment/e6010/model/c0201e6010_dwn.mdl",
        "chara/equipment/e0200/model/c0101e0200_glv.mdl",
        "chara/equipment/e0300/model/c0201e0300_met.mdl",
    ];

    private IEnumerable<(string Path, byte[] Bytes)> Vanilla()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        foreach (var path in VanillaGear)
            yield return (path, data.GetFile(path)?.Data ?? throw new FileNotFoundException(path));
    }

    [LocalDataFact(LocalData.GameData)]
    public void The_game_s_gear_keeps_LOD0_exactly_and_loses_the_rest()
    {
        foreach (var (path, before) in Vanilla())
        {
            Assert.Equal(3, ModelLodTrimmer.LodCount(before));
            var after = ModelLodTrimmer.KeepLod0(before, out string refusal);
            Assert.True(after != null, $"{path}: {refusal}");
            output.WriteLine($"{path}: {before.Length:N0} -> {after!.Length:N0} bytes");

            var was = SecondSkinWriter.Parse(before);
            var now = SecondSkinWriter.Parse(after);
            Assert.Equal(1, ModelLodTrimmer.LodCount(after));
            Assert.Equal(1, after[now.Mh + 22]);
            Assert.Equal(was.Lod0MeshCount, now.MeshCount);
            Assert.Equal(was.Lod0MeshCount, now.Lod0MeshCount);

            // LOD0's data, byte for byte, now at the end of the file.
            uint vSize = BitConverter.ToUInt32(before, 40), iSize = BitConverter.ToUInt32(before, 52);
            Assert.Equal(before.AsSpan(was.Vb, (int)vSize).ToArray(), after.AsSpan(now.Vb, (int)vSize).ToArray());
            Assert.Equal(before.AsSpan(was.Ib, (int)iSize).ToArray(), after.AsSpan(now.Ib, (int)iSize).ToArray());
            Assert.Equal(after.Length, now.Ib + (int)iSize);

            // The same meshes, materials, bones and attributes.
            for (int m = 0; m < now.MeshCount; m++)
                Assert.Equal(before.AsSpan(was.MeshStart + m * 36, 36).ToArray(),
                             after.AsSpan(now.MeshStart + m * 36, 36).ToArray());
            Assert.Equal(was.MatNames, now.MatNames);
            Assert.Equal(was.BoneNames, now.BoneNames);
            Assert.Equal(was.AttrNames, now.AttrNames);
            Assert.Equal(was.SubmeshBoneMap, now.SubmeshBoneMap);
            Assert.Equal(was.ModelBBoxes, now.ModelBBoxes);
            Assert.Equal(was.BoneBBoxes, now.BoneBBoxes);

            // Every shape key, with LOD0's edits intact.
            Assert.Equal(was.Shapes.Keys.OrderBy(k => k), now.Shapes.Keys.OrderBy(k => k));
            foreach (var (name, entries) in was.Shapes)
            {
                var kept = now.Shapes[name];
                Assert.Equal(entries.Count, kept.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    Assert.Equal(entries[i].MeshIndexOffset, kept[i].MeshIndexOffset);
                    Assert.Equal(entries[i].Values, kept[i].Values);
                }
            }

            // And the geometry everything downstream reads.
            var a = ModelPartReader.Read(before)!;
            var b = ModelPartReader.Read(after)!;
            Assert.Equal(a.Positions, b.Positions);
            Assert.Equal(a.Parts.Select(p => (p.Mesh, p.Material, p.Triangles.Length)),
                         b.Parts.Select(p => (p.Mesh, p.Material, p.Triangles.Length)));
        }
    }

    [LocalDataFact(LocalData.GameData)]
    public void LOD0_is_drawn_at_any_distance()
    {
        foreach (var (_, before) in Vanilla())
        {
            var after = ModelLodTrimmer.KeepLod0(before, out _)!;
            int lod = SecondSkinWriter.Parse(after).LodStart;
            Assert.Equal(0f, BitConverter.ToSingle(after, lod + 4));
            for (int l = 1; l < 3; l++)
                Assert.Equal(0, BitConverter.ToUInt16(after, lod + l * 60 + 2));   // no meshes
        }
    }

    [LocalDataFact(LocalData.GameData)]
    public void A_model_with_one_level_is_left_alone()
    {
        foreach (var (_, before) in Vanilla())
        {
            var once = ModelLodTrimmer.KeepLod0(before, out _)!;
            Assert.Null(ModelLodTrimmer.KeepLod0(once, out string refusal));
            Assert.Contains("one level", refusal);
        }
    }

    /// <summary>
    /// A refit in place — same rig, the garment's own skin kept — used to write the author's other levels back
    /// untouched, fitted to the old body. It now writes LOD0 alone, and does not warn.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    public void An_in_place_refit_of_the_game_s_gear_comes_out_with_one_level()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var garmentBytes = data.GetFile("chara/equipment/e0010/model/c0101e0010_top.mdl")!.Data;
        var bodyBytes = data.GetFile("chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl")!.Data;
        var body = ModelPartReader.Read(bodyBytes)!;
        var uv = TestMeshes.Uv(bodyBytes);
        Assert.True(BodyCorrespondence.TryBuild(body, uv, body, uv, "_top", out var built, out string refusal,
                                                male: true), refusal);

        var planned = BodyRetarget.Plan(ModelPartReader.Read(garmentBytes)!, garmentBytes,
                                        [new BodyRetarget.SlotPair("_top", built!, body)], "_top");
        Assert.Null(planned.Report.Swap);                        // in place: nothing rebuilt
        Assert.Equal(1, ModelLodTrimmer.LodCount(planned.Model));
        Assert.False(planned.Report.HasOtherLods);
    }

    [Fact]
    public void Garbage_is_refused_rather_than_thrown()
    {
        var junk = new byte[256];
        junk[64] = 3;
        Assert.Null(ModelLodTrimmer.KeepLod0(junk, out string refusal));
        Assert.NotEmpty(refusal);
    }
}
