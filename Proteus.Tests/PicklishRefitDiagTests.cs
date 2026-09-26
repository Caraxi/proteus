using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Reported: the packer's generated YAB L (and Rue L) sizes of "Picklish" (e6238 top, a strapless band over the bust)
/// clip very badly — the breasts come straight through the band.
/// </summary>
public class PicklishRefitDiagTests(ITestOutputHelper output)
{
    private const string Mod = @"E:\Penumbradt\Picklish - by Solona\top size";
    private const string TopRel = @"chara\equipment\e6238\model\c0201e6238_top.mdl";
    private const string YabRoot = @"E:\Penumbradt\hs-Yet Another Body+-4.2.0-tmi";
    private const string RueRoot = @"E:\Penumbradt\hs-Rue+-2.2.7-y0f";
    private const string NeolitheRoot = @"E:\Penumbradt\Neolithe [ALL IN ONE]";

    private static string Size(string folder) => Path.Combine(Mod, folder, TopRel);

    private static string BodyTop(string root, string folder)
        => Path.Combine(root, @"files\chest - smallclothes", folder, @"chara\equipment\e0000\model\c0201e0000_top.mdl");

    /// <summary>Every shipped size: what it detects as, and its cloth against its own skin and against the body it
    /// is for.</summary>
    [Fact]
    public void Every_shipped_size()
    {
        if (!Directory.Exists(Mod) || !Directory.Exists(YabRoot) || !Directory.Exists(NeolitheRoot)) return;
        var neo = BodySizeCatalog.Read(NeolitheRoot);
        var yab = BodySizeCatalog.Read(YabRoot);
        foreach (string folder in new[] { "neolithe xs", "neolithe s", "neolithe m", "neolithe l", "yab s", "yab m", "yab l",
                                          "rue m", "rue l" })
        {
            if (!File.Exists(Size(folder))) continue;
            var bytes = File.ReadAllBytes(Size(folder));
            var m = ModelPartReader.Read(bytes)!;
            var catalog = folder.StartsWith("neolithe") ? neo : yab;
            var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
            var rank = BodySizeMatch.Rank(m, catalog.For("_top", "0201"), catalog.PathOf, bones);
            output.WriteLine($"{folder}: {m.Positions.Length / 3} verts, detect {rank.Confidence}: " +
                             string.Join(" | ", rank.Scores.Take(3).Select(s => $"{s.Option.Label} {s.Rms * 1000:F2}mm {s.HitRate:P0}")));
            Clipping("  vs own skin", m, Skin(m));
        }

        var yabL = ModelPartReader.Read(File.ReadAllBytes(BodyTop(YabRoot, "Large")))!;
        var yabM = ModelPartReader.Read(File.ReadAllBytes(BodyTop(YabRoot, "Medium")))!;
        foreach (string folder in new[] { "yab l", "yab m" })
        {
            if (!File.Exists(Size(folder))) continue;
            var m = ModelPartReader.Read(File.ReadAllBytes(Size(folder)))!;
            Clipping($"{folder} vs YAB Large", m, yabL);
            Clipping($"{folder} vs YAB Medium", m, yabM);
            Parts(folder, m);
        }
        foreach (string folder in new[] { "neolithe l", "neolithe m" })
            Parts(folder, ModelPartReader.Read(File.ReadAllBytes(Size(folder)))!);
    }

    /// <summary>The refit re-run the way the packer runs it — each Neolithe size onto YAB Large — before and after
    /// the push-out, against YAB Large.</summary>
    [Theory]
    [InlineData("neolithe l", "Large", YabRoot)]
    [InlineData("neolithe m", "Medium", YabRoot)]
    [InlineData("neolithe l", "Large", RueRoot)]
    public void Refit_onto_yab(string from, string yabChest, string root)
    {
        if (!File.Exists(Size(from)) || !Directory.Exists(root) || !Directory.Exists(NeolitheRoot)) return;
        var bytes = File.ReadAllBytes(Size(from));
        var garment = ModelPartReader.Read(bytes)!;
        var neo = BodySizeCatalog.Read(NeolitheRoot);
        var yab = BodySizeCatalog.Read(root);
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), @"E:\repos\Proteus\Proteus");

        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var src = BodySizeMatch.Rank(garment, neo.For("_top", "0201"), neo.PathOf, bones).Best!.Value.Option;
        string target = BodyTop(root, yabChest);
        output.WriteLine($"{from}: source {src.FullLabel} -> {Path.GetFileName(root)} {yabChest}");
        Assert.Null(BodyRetarget.BuildPair("_top", neo.PathOf(src), target, "_top", false, Mask(neo, "_top"), Mask(yab, "_top"),
                                           remap, out var pair));
        var pairs = new List<BodyRetarget.SlotPair> { pair };

        var planned = BodyRetarget.Plan(garment, bytes, pairs, "_top", replaceSkin: true, acrossBodies: true, cutHidden: true);
        var r = planned.Report;
        output.WriteLine($"snapped {r.Snapped:N0}, transferred {r.Transferred:N0}, missed {r.Missed:N0}, pushed {r.Pushed:N0} " +
                         $"(worst {r.WorstPush * 1000f:F2} mm), worst move {r.WorstMove * 1000f:F2} mm, folded {r.Folded}");
        var refit = ModelPartReader.Read(planned.Model)!;
        var body = pair.Target;
        Clipping("source vs its own skin", garment, Skin(garment));
        Clipping("source vs source body", garment, pair.Correspondence.Source);
        Clipping("refit vs its own skin", refit, Skin(refit));
        Clipping("refit vs YAB body", refit, body);

        Clipping("solve, no push, no lay", Moved(garment, BodyRetarget.Solve(garment, pairs, "_top", pushOut: false).Edit), body);
        Clipping("solve, no push, no follow", Moved(garment, BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoFollow: true),
                     () => BodyRetarget.Solve(garment, pairs, "_top", pushOut: false, replaceSkin: true)).Edit), body);
        var noPush = Moved(garment, BodyRetarget.Solve(garment, pairs, "_top", pushOut: false, replaceSkin: true).Edit);
        var pushed = Moved(garment, BodyRetarget.Solve(garment, pairs, "_top", replaceSkin: true).Edit);
        Clipping("solve, no push, vs YAB", noPush, body);
        Clipping("solve, pushed, vs YAB", pushed, body);

        // The worst of them: the Solve's delta against the plain eased field at the nearest source point.
        {
            var srcModel = pair.Correspondence.Source;
            var field = BodyRetarget.SourceBody.Whole(srcModel, pair.Correspondence.Field);
            var eased = BodyRetarget.SourceBody.Eased(srcModel, field);
            var ss = new BodySurface(srcModel, 0.01f);
            var ds = new BodySurface(body, 0.01f);
            var worst = new List<(float S, int V, string Part)>();
            foreach (var part in garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                foreach (int v in part.Triangles.Distinct())
                    if (ds.Nearest(At(noPush, v), 0.03f, out var h))
                        worst.Add((Vector3.Dot(At(noPush, v) - h.Point, h.Normal), v, part.Label));
            foreach (var (s, v, label) in worst.OrderBy(w => w.S).Take(12))
            {
                var p = At(garment, v);
                ss.Nearest(p, 0.25f, out var h);
                var f = (eased[h.A] ?? default) * h.U + (eased[h.B] ?? default) * h.V + (eased[h.C] ?? default) * h.W;
                ds.Nearest(p + f, 0.05f, out var hf);
                output.WriteLine($"  v{v} {label} at {p:F3}: solve {s * 1000:F1} mm, delta {(At(noPush, v) - p) * 1000:F1}; " +
                                 $"field {f * 1000:F1} -> {Vector3.Dot(p + f - hf.Point, hf.Normal) * 1000:F1} mm; src off {h.Distance * 1000:F1}");
            }
        }

        // Where the transfer leaves each bust cloth vertex, against where it started on the source.
        var own = new BodySurface(Skin(garment), 0.01f);
        var srcBody = new BodySurface(pair.Correspondence.Source, 0.01f);
        var dst = new BodySurface(body, 0.01f);
        float S(BodySurface s, Vector3 p) => s.Nearest(p, 0.05f, out var h) ? Vector3.Dot(p - h.Point, h.Normal) : float.NaN;
        var rows = new List<(float Y, float Own, float Src, float NoPush, float Pushed, float Moved)>();
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles.Distinct())
            {
                var p = At(garment, v);
                if (p.Z < 0.02f || p.Y is < 1.10f or > 1.35f) continue;
                rows.Add((At(pushed, v).Y, S(own, p), S(srcBody, p), S(dst, At(noPush, v)), S(dst, At(pushed, v)),
                          Vector3.Distance(p, At(noPush, v))));
            }
        output.WriteLine($"front bust cloth: {rows.Count} verts (signed mm, + outside)");
        foreach (var g in rows.GroupBy(x => MathF.Round(x.Y * 50f) / 50f).OrderBy(g => g.Key))
        {
            string M(Func<(float Y, float Own, float Src, float NoPush, float Pushed, float Moved), float> f)
            {
                var l = g.Select(f).Where(x => !float.IsNaN(x)).ToList();
                return l.Count == 0 ? "   -   " : $"{l.Average() * 1000,6:F1}/{l.Min() * 1000,6:F1}";
            }
            output.WriteLine($"  y~{g.Key:F2} {g.Count(),5}: own {M(x => x.Own)}  src {M(x => x.Src)}  noPush {M(x => x.NoPush)}  " +
                             $"pushed {M(x => x.Pushed)}  moved {g.Average(x => x.Moved) * 1000:F1} mm");
        }
    }

    /// <summary>Whether the body field itself is right under the bust: each front bust cloth vertex's nearest SOURCE
    /// body point, carried by the field (exact and eased), against the target surface. A landing inside the target is
    /// the correspondence's fault; a landing on it with the cloth inside is the transfer's.</summary>
    [Fact]
    public void The_field_under_the_bust()
    {
        if (!File.Exists(Size("neolithe l")) || !Directory.Exists(YabRoot) || !Directory.Exists(NeolitheRoot)) return;
        var bytes = File.ReadAllBytes(Size("neolithe l"));
        var garment = ModelPartReader.Read(bytes)!;
        var neo = BodySizeCatalog.Read(NeolitheRoot);
        var yab = BodySizeCatalog.Read(YabRoot);
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), @"E:\repos\Proteus\Proteus");
        var src = neo.For("_top", "0201").First(o => o.Label == "DEFAULT ALMOND · SFW Almond L");
        Assert.Null(BodyRetarget.BuildPair("_top", neo.PathOf(src), BodyTop(YabRoot, "Large"), "_top", false, Mask(neo, "_top"),
                                           Mask(yab, "_top"), remap, out var pair));
        var srcModel = pair.Correspondence.Source;
        var field = BodyRetarget.SourceBody.Whole(srcModel, pair.Correspondence.Field);
        var eased = BodyRetarget.SourceBody.Eased(srcModel, field);
        var srcSurface = new BodySurface(srcModel, 0.01f);
        var dst = new BodySurface(pair.Target, 0.01f);
        float S(BodySurface s, Vector3 p) => s.Nearest(p, 0.08f, out var h) ? Vector3.Dot(p - h.Point, h.Normal) : float.NaN;
        Vector3 F(IReadOnlyList<Vector3?> f, BodySurface.Hit h)
            => (f[h.A] ?? Vector3.Zero) * h.U + (f[h.B] ?? Vector3.Zero) * h.V + (f[h.C] ?? Vector3.Zero) * h.W;

        // The body alone: every source skin vertex on the front of the bust, carried by the field, against YAB.
        var bodyRows = new List<(float Y, float Exact, float Eased)>();
        for (int v = 0; v < srcModel.Positions.Length / 3; v++)
        {
            if (field[v] is not { } d || eased[v] is not { } e) continue;
            var p = At(srcModel, v);
            if (p.Z < 0.02f || p.Y is < 1.10f or > 1.35f) continue;
            bodyRows.Add((p.Y, S(dst, p + d), S(dst, p + e)));
        }
        output.WriteLine($"source body front bust verts carried by the field, vs YAB L (mm): {bodyRows.Count}");
        foreach (var g in bodyRows.GroupBy(x => MathF.Round(x.Y * 50f) / 50f).OrderBy(g => g.Key))
            output.WriteLine($"  y~{g.Key:F2} {g.Count(),5}: exact {g.Average(x => x.Exact) * 1000,6:F1} (min {g.Min(x => x.Exact) * 1000,6:F1})  " +
                             $"eased {g.Average(x => x.Eased) * 1000,6:F1} (min {g.Min(x => x.Eased) * 1000,6:F1})");

        // The cloth: nearest source point, its offset, the landing.
        var rows = new List<(float Y, float Off, float Land, float Carried, Vector3 At, Vector3 Normal, Vector3 D)>();
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles.Distinct())
            {
                var p = At(garment, v);
                if (p.Z < 0.02f || p.Y is < 1.10f or > 1.35f) continue;
                if (!srcSurface.Nearest(p, 0.25f, out var h)) continue;
                var d = F(eased, h);
                rows.Add((p.Y, h.Distance, S(dst, h.Point + d), S(dst, p + d), p, h.Normal, d));
            }
        output.WriteLine($"front bust cloth: nearest source point off, its landing vs YAB, the cloth carried vs YAB (mm)");
        foreach (var g in rows.GroupBy(x => MathF.Round(x.Y * 50f) / 50f).OrderBy(g => g.Key))
            output.WriteLine($"  y~{g.Key:F2} {g.Count(),5}: off {g.Average(x => x.Off) * 1000,5:F1}  land {g.Average(x => x.Land) * 1000,6:F1} " +
                             $"(min {g.Min(x => x.Land) * 1000,6:F1})  carried {g.Average(x => x.Carried) * 1000,6:F1} (min {g.Min(x => x.Carried) * 1000,6:F1})");
        foreach (var r in rows.OrderBy(r => r.Carried).Take(10))
            output.WriteLine($"    at {r.At:F3} off {r.Off * 1000:F1} src normal {r.Normal:F2} delta {r.D * 1000:F1} land {r.Land * 1000:F1} carried {r.Carried * 1000:F1}");
    }

    /// <summary>The refit with and without the fold give-up: how many triangles fold, where, and what the clipping is.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Folds_and_the_give_up(bool noGiveUp)
    {
        if (!File.Exists(Size("neolithe l")) || !Directory.Exists(YabRoot) || !Directory.Exists(NeolitheRoot)) return;
        var garment = ModelPartReader.Read(File.ReadAllBytes(Size("neolithe l")))!;
        var neo = BodySizeCatalog.Read(NeolitheRoot);
        var yab = BodySizeCatalog.Read(YabRoot);
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), @"E:\repos\Proteus\Proteus");
        var src = neo.For("_top", "0201").First(o => o.Label == "DEFAULT ALMOND · SFW Almond L");
        Assert.Null(BodyRetarget.BuildPair("_top", neo.PathOf(src), BodyTop(YabRoot, "Large"), "_top", false, Mask(neo, "_top"),
                                           Mask(yab, "_top"), remap, out var pair));
        var solved = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoGiveUp: noGiveUp),
                                             () => BodyRetarget.Solve(garment, [pair], "_top", replaceSkin: true));
        output.WriteLine($"no give-up {noGiveUp}: folded {solved.Folded}, pushed {solved.Pushed}");
        var moved = Moved(garment, solved.Edit);
        Clipping("vs YAB L", moved, pair.Target);

        foreach (var part in garment.Parts.Where(p => p.Island < 0))
        {
            var at = new List<Vector3>();
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var n0 = Vector3.Cross(At(garment, b) - At(garment, a), At(garment, c) - At(garment, a));
                var n1 = Vector3.Cross(At(moved, b) - At(moved, a), At(moved, c) - At(moved, a));
                if (n0.Length() > 1e-12f && Vector3.Dot(n0, n1) < 0f) at.Add((At(garment, a) + At(garment, b) + At(garment, c)) / 3f);
            }
            if (at.Count == 0) continue;
            output.WriteLine($"  {part.Label} [{part.Material.Split('/').Last()}]: {at.Count} folded");
            foreach (var p in at.Take(12)) output.WriteLine($"    at {p:F3}");
        }
    }

    private void Parts(string label, ModelParts m)
    {
        output.WriteLine($"{label} parts:");
        foreach (var part in m.Parts)
        {
            var verts = part.Triangles.Distinct().ToList();
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (int v in verts) { lo = Vector3.Min(lo, At(m, v)); hi = Vector3.Max(hi, At(m, v)); }
            output.WriteLine($"  {part.Label,-8} isl {part.Island,3} [{part.Material}] {verts.Count,6} v " +
                             $"x {lo.X:F3}..{hi.X:F3} y {lo.Y:F3}..{hi.Y:F3} z {lo.Z:F3}..{hi.Z:F3}");
        }
    }

    private static ushort? Mask(BodySizeCatalog c, string slot)
        => BodyRetarget.ImcSlotName(slot) is { } equip ? ImcEntrySource.MaskFor(c.ModRoot, 0, equip, null) : null;

    private void Clipping(string label, ModelParts cloth, ModelParts body)
    {
        var surface = new BodySurface(body, 0.01f);
        var perPart = new List<string>();
        int total = 0, deep = 0;
        foreach (var part in cloth.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            int inside = 0, bust = 0;
            float partWorst = 0f;
            foreach (int v in part.Triangles.Distinct())
            {
                var p = At(cloth, v);
                if (!surface.Nearest(p, 0.03f, out var hit)) continue;
                float signed = Vector3.Dot(p - hit.Point, hit.Normal);
                if (signed > -0.0005f) continue;
                inside++;
                if (p.Y is > 1.15f and < 1.30f) bust++;
                if (signed < -0.002f) deep++;
                partWorst = MathF.Max(partWorst, -signed);
            }
            total += inside;
            if (inside > 0) perPart.Add($"{part.Label}[{part.Material.Split('_').Last()}] {inside} ({bust} at bust, to {partWorst * 1000:F1})");
        }
        output.WriteLine($"{label,-28} inside >0.5mm {total,5}, >2mm {deep,5}   {string.Join("; ", perPart)}");
    }

    private static ModelParts Skin(ModelParts m) => new()
    {
        Positions = m.Positions, Normals = m.Normals, MeshSpans = m.MeshSpans,
        Parts = m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList(),
        AttributeNames = m.AttributeNames, Min = m.Min, Max = m.Max, ShatteredSubmeshes = m.ShatteredSubmeshes,
    };

    private static ModelParts Moved(ModelParts m, RetargetEdit edit)
    {
        var pos = (float[])m.Positions.Clone();
        for (int v = 0; v < pos.Length / 3; v++)
        {
            var d = edit.DeltaAt(v);
            pos[v * 3] += d.X; pos[v * 3 + 1] += d.Y; pos[v * 3 + 2] += d.Z;
        }
        return new ModelParts
        {
            Positions = pos, Normals = m.Normals, MeshSpans = m.MeshSpans, Parts = m.Parts,
            AttributeNames = m.AttributeNames, Min = m.Min, Max = m.Max, ShatteredSubmeshes = m.ShatteredSubmeshes,
        };
    }

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
}
