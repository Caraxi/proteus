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
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) { output.WriteLine($"no game data at {sqpack}"); yield break; }
        var data = new Lumina.GameData(sqpack);
        foreach (var path in VanillaGear)
            if (data.GetFile(path) is { } file)
                yield return (path, file.Data);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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
    [Fact]
    public void An_in_place_refit_of_the_game_s_gear_comes_out_with_one_level()
    {
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) return;
        var data = new Lumina.GameData(sqpack);
        var garmentBytes = data.GetFile("chara/equipment/e0010/model/c0101e0010_top.mdl")!.Data;
        var bodyBytes = data.GetFile("chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl")!.Data;
        var body = ModelPartReader.Read(bodyBytes)!;
        var uv = BodyRetargetDiagTests.Uv(bodyBytes);
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

    /// <summary>
    /// Every installed mod's gear that carries more than one level: how many cut, and why any did not. Each cut must
    /// read back with the same LOD0 geometry.
    /// <para/>
    /// Opt-in with <c>PROTEUS_LOD_SWEEP=1</c>: it reads every model under the mods folder (about 12,000, 15-45 s),
    /// which every full run would otherwise pay for. Measured 2026-09-27: 1,457 cut, 0 refused.
    /// </summary>
    [Fact]
    public void Installed_mods_multi_LOD_gear()
    {
        if (Environment.GetEnvironmentVariable("PROTEUS_LOD_SWEEP") != "1")
        {
            output.WriteLine("set PROTEUS_LOD_SWEEP=1 to sweep the installed mods");
            return;
        }
        var mods = Environment.GetEnvironmentVariable("PROTEUS_MODS") ?? @"E:\Penumbradt";
        if (!Directory.Exists(mods)) return;
        int cut = 0, refused = 0;
        var why = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(mods, "*.mdl", SearchOption.AllDirectories))
        {
            byte[] before;
            try { before = File.ReadAllBytes(file); } catch (IOException) { continue; }
            if (ModelLodTrimmer.LodCount(before) <= 1) continue;
            if (ModelPartReader.Read(before) is not { } a) continue;   // not a model anything here could refit

            var after = ModelLodTrimmer.KeepLod0(before, out string refusal);
            if (after == null)
            {
                refused++;
                why[refusal] = why.GetValueOrDefault(refusal) + 1;
                if (why[refusal] <= 2) output.WriteLine($"refused ({refusal}): {file}");
                continue;
            }
            cut++;
            var b = ModelPartReader.Read(after)!;
            Assert.True(a.Positions.AsSpan().SequenceEqual(b.Positions), file);
        }
        output.WriteLine($"cut {cut:N0}, refused {refused:N0}");
        foreach (var (reason, count) in why.OrderByDescending(kv => kv.Value)) output.WriteLine($"  {count,5:N0}  {reason}");
    }
}
