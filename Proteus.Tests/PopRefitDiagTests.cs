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
/// Reported: "pop" (e0367 top) refitted from YAB+ Yiggle - Medium onto Neolithe DEFAULT ALMOND · NSFW Almond XS clips
/// badly, and the metal ring under the breasts came out squished.
/// </summary>
public class PopRefitDiagTests(ITestOutputHelper output)
{
    private const string Mod = @"E:\Penumbradt\pop_v1.2";
    private const string TopRel = @"chara\equipment\e0367\model\c0201e0367_top.mdl";
    private const string Shipped = Mod + @"\p1\pop\top & accessories\" + TopRel;
    private const string Saved = Mod + @"\Body Retarget\Neolithe [ALL IN ONE] - DEFAULT ALMOND - NSFW Almond XS\" + TopRel;
    private const string YabRoot = @"E:\Penumbradt\hs-Yet Another Body+-4.2.0-tmi";
    private const string NeolitheRoot = @"E:\Penumbradt\Neolithe [ALL IN ONE]";

    [Fact]
    public void The_parts_as_shipped_and_as_saved()
    {
        if (!File.Exists(Shipped) || !File.Exists(Saved)) return;
        foreach (var (label, path) in new[] { ("shipped", Shipped), ("saved", Saved) })
        {
            var m = ModelPartReader.Read(File.ReadAllBytes(path))!;
            output.WriteLine($"{label}: {m.Positions.Length / 3} verts");
            foreach (var part in m.Parts)
            {
                var verts = part.Triangles.Distinct().ToList();
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (int v in verts) { lo = Vector3.Min(lo, At(m, v)); hi = Vector3.Max(hi, At(m, v)); }
                output.WriteLine($"  {part.Label,-8} isl {part.Island,3} [{part.Material}] {verts.Count,6} v {part.Triangles.Length / 3,6} t " +
                                 $"x {lo.X:F3}..{hi.X:F3} y {lo.Y:F3}..{hi.Y:F3} z {lo.Z:F3}..{hi.Z:F3}");
            }
        }
    }

    /// <summary>The refit as the panel runs it, geometry only (vertex for vertex with the shipped file): how far each
    /// metal piece is bent out of its own shape, and where the cloth ends up against the skin that is drawn.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ring_shape_and_clipping(bool keepMetal)
    {
        if (!File.Exists(Shipped) || !Directory.Exists(YabRoot) || !Directory.Exists(NeolitheRoot)) return;
        var bytes = File.ReadAllBytes(Shipped);
        var garment = ModelPartReader.Read(bytes)!;
        var pairs = Pairs();

        var rank = BodySizeMatch.Rank(garment, BodySizeCatalog.Read(YabRoot).For("_top"), BodySizeCatalog.Read(YabRoot).PathOf,
                                      new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal));
        output.WriteLine($"detect: {rank.Confidence} — " +
                         string.Join(" | ", rank.Scores.Take(5).Select(s => $"{s.Option.Label} {s.Rms * 1000:F2}mm {s.HitRate:P0}")));

        // The two metal rows ticked "Keep shape", as the panel turns them into pieces.
        var metal = keepMetal
            ? Proteus.Gui.BodyRetargetPanel.ShapePieces(garment, new HashSet<string> { "3.1", "3.3" })
            : null;
        if (metal != null) output.WriteLine($"keep shape: {metal.Count} pieces");
        var solved = BodyRetarget.Solve(garment, pairs, "_top", replaceSkin: true, keepShape: metal);
        output.WriteLine($"solve: moved up to {solved.WorstMove * 1000:F1} mm, snapped {solved.Snapped}, pushed {solved.Pushed} " +
                         $"(worst {solved.WorstPush * 1000:F2}), missed {solved.Missed}, folded {solved.Folded}");
        var moved = Moved(garment, solved.Edit);

        output.WriteLine("");
        output.WriteLine("metal pieces (material top_b), bent out of shape: best uniform scale, then pairwise-distance error");
        foreach (var part in garment.Parts.Where(p => p.Material.Contains("top_b") && (p.Island >= 0 || p.Label == "3.2")))
        {
            if (part.Island >= 0 && part.Triangles.Distinct().Count() < 200) continue;   // chain links: report in bulk below
            Shape(part.Label, part.Triangles.Distinct().ToArray(), garment, moved);
        }
        var links = garment.Parts.Where(p => p.Material.Contains("top_b") && p.Island >= 0
                                          && p.Triangles.Distinct().Count() < 200).ToList();
        float linkWorst = 0f, linkScale0 = 9f, linkScale1 = 0f;
        foreach (var l in links)
        {
            var (s, rms, worst) = Distortion(l.Triangles.Distinct().ToArray(), garment, moved);
            linkWorst = MathF.Max(linkWorst, worst);
            linkScale0 = MathF.Min(linkScale0, s);
            linkScale1 = MathF.Max(linkScale1, s);
        }
        output.WriteLine($"  {links.Count} chain links: scale {linkScale0:F3}..{linkScale1:F3}, worst pairwise error {linkWorst * 1000:F2} mm");

        output.WriteLine("");
        var target = pairs[0].Target;
        Clipping("shipped vs its own skin", garment, Skin(garment));
        Clipping("shipped vs YAB Yiggle M", garment, pairs[0].Correspondence.Source);
        Clipping("refit vs Almond XS", moved, target);
        if (File.Exists(Saved))
        {
            var saved = ModelPartReader.Read(File.ReadAllBytes(Saved))!;
            Clipping("saved file vs its own skin", saved, Skin(saved));
            Clipping("saved file vs Almond XS", saved, target);
        }
    }

    /// <summary>The waist belt (3.2), vertex by vertex: how far off the author's own skin it sat, how far off YAB's,
    /// and how far off Neolithe's after the transfer alone and after the push-out.</summary>
    [Fact]
    public void The_belt_against_each_skin()
    {
        if (!File.Exists(Shipped) || !Directory.Exists(YabRoot) || !Directory.Exists(NeolitheRoot)) return;
        var bytes = File.ReadAllBytes(Shipped);
        var garment = ModelPartReader.Read(bytes)!;
        var pairs = Pairs();
        var noPush = Moved(garment, BodyRetarget.Solve(garment, pairs, "_top", pushOut: false, replaceSkin: true).Edit);
        var pushed = Moved(garment, BodyRetarget.Solve(garment, pairs, "_top", replaceSkin: true).Edit);

        var own = new BodySurface(Skin(garment), 0.01f);
        var yab = new BodySurface(pairs[0].Correspondence.Source, 0.01f);
        var neo = new BodySurface(pairs[0].Target, 0.01f);
        float Signed(BodySurface s, Vector3 p, float reach) => s.Nearest(p, reach, out var h) ? Vector3.Dot(p - h.Point, h.Normal) : float.NaN;

        foreach (string label in new[] { "3.2", "3.1", "2.1" })
        {
            var verts = garment.Parts.First(p => p.Island < 0 && p.Label == label).Triangles.Distinct().ToArray();
            int[] buckets = new int[6];
            int ownNone = 0, authoredInside = 0, insideAfter = 0, insideAfterAuthoredOut = 0, pushedMoved = 0;
            var worst = new List<(float After, float Own, float Yab, float Transfer, Vector3 At)>();
            foreach (int v in verts)
            {
                float so = Signed(own, At(garment, v), 0.15f);
                float sy = Signed(yab, At(garment, v), 0.03f);
                float st = Signed(neo, At(noPush, v), 0.03f);
                float sp = Signed(neo, At(pushed, v), 0.03f);
                if (float.IsNaN(so)) ownNone++;
                else if (so < 0f) authoredInside++;
                if (Vector3.Distance(At(noPush, v), At(pushed, v)) > 1e-5f) pushedMoved++;
                if (sp < -0.0005f)
                {
                    insideAfter++;
                    if (!(so < 0f)) insideAfterAuthoredOut++;
                    worst.Add((sp, so, sy, st, At(pushed, v)));
                }
            }
            output.WriteLine($"{label}: {verts.Length} verts; no own skin within 150 mm {ownNone}; authored inside own skin {authoredInside}; " +
                             $"moved by the push {pushedMoved}; inside Neolithe after {insideAfter} (authored outside: {insideAfterAuthoredOut})");
            foreach (var w in worst.OrderBy(w => w.After).Take(12))
                output.WriteLine($"   after {w.After * 1000,6:F2}  own {w.Own * 1000,6:F2}  yab {w.Yab * 1000,6:F2}  transfer-only {w.Transfer * 1000,6:F2}   at {w.At:F3}");
        }
    }

    /// <summary>The garment's own skin against every YAB chest option: signed distance per skin vertex, by region.</summary>
    [Fact]
    public void Which_yab_the_skin_is()
    {
        if (!File.Exists(Shipped) || !Directory.Exists(YabRoot)) return;
        var garment = ModelPartReader.Read(File.ReadAllBytes(Shipped))!;
        var skinVerts = garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct().ToArray();
        var yab = BodySizeCatalog.Read(YabRoot);
        foreach (var option in yab.For("_top"))
        {
            var body = new BodySurface(ModelPartReader.Read(File.ReadAllBytes(yab.PathOf(option)))!, 0.01f);
            var all = new List<float>();
            var bust = new List<float>();
            var waist = new List<float>();
            int exact = 0;
            foreach (int v in skinVerts)
            {
                var p = At(garment, v);
                if (!body.Nearest(p, 0.05f, out var h)) continue;
                float s = Vector3.Dot(p - h.Point, h.Normal);
                if (h.Distance < 1e-4f) exact++;
                all.Add(s);
                if (p.Y is > 1.15f and < 1.30f && p.Z > 0.03f) bust.Add(s);
                if (p.Y is > 1.03f and < 1.10f) waist.Add(s);
            }
            string Stat(List<float> l) => l.Count == 0 ? "-" : $"mean {l.Average() * 1000,6:F2} rms {MathF.Sqrt(l.Average(x => x * x)) * 1000,5:F2}";
            output.WriteLine($"{option.Label,-40} exact {exact,5}/{skinVerts.Length}  all {Stat(all)}  bust {Stat(bust)}  waist {Stat(waist)}");
        }
    }

    private static List<BodyRetarget.SlotPair> Pairs()
    {
        var yab = BodySizeCatalog.Read(YabRoot);
        var neo = BodySizeCatalog.Read(NeolitheRoot);
        string sourcePath = yab.PathOf(yab.For("_top").First(o => o.Name == "Yiggle - Medium"));
        string targetPath = neo.PathOf(neo.For("_top").First(o => o.Label == "DEFAULT ALMOND · NSFW Almond XS"));
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var targetBytes = File.ReadAllBytes(targetPath);
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), @"E:\repos\Proteus\Proteus");
        Assert.True(BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sourceBytes), target,
                                                BodyRetargetDiagTests.Uv(targetBytes), "_top", out var built,
                                                out string refusal, remap), refusal);
        return [new BodyRetarget.SlotPair("_top", built!, target, targetBytes, sourceBytes)];
    }

    private void Shape(string label, int[] verts, ModelParts before, ModelParts after)
    {
        var (s, rms, worst) = Distortion(verts, before, after);
        Vector3 Extent(ModelParts m)
        {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (int v in verts) { lo = Vector3.Min(lo, At(m, v)); hi = Vector3.Max(hi, At(m, v)); }
            return hi - lo;
        }
        var e0 = Extent(before) * 1000f;
        var e1 = Extent(after) * 1000f;
        output.WriteLine($"  {label,-8} {verts.Length,5} v: scale {s:F3}, pairwise error rms {rms * 1000:F2} mm worst {worst * 1000:F2} mm; " +
                         $"extent {e0.X:F1}x{e0.Y:F1}x{e0.Z:F1} -> {e1.X:F1}x{e1.Y:F1}x{e1.Z:F1} mm");
    }

    /// <summary>Every pair of a (sampled) piece's points: the uniform scale that best explains how their distances
    /// changed, and what is left over — zero for a piece that only moved, turned and grew.</summary>
    private static (float Scale, float Rms, float Worst) Distortion(int[] verts, ModelParts before, ModelParts after)
    {
        int step = Math.Max(1, verts.Length / 300);
        var sample = verts.Where((_, i) => i % step == 0).ToArray();
        double num = 0, den = 0;
        var pairs = new List<(float D0, float D1)>();
        for (int i = 0; i < sample.Length; i++)
            for (int j = i + 1; j < sample.Length; j++)
            {
                float d0 = Vector3.Distance(At(before, sample[i]), At(before, sample[j]));
                float d1 = Vector3.Distance(At(after, sample[i]), At(after, sample[j]));
                pairs.Add((d0, d1));
                num += d0 * d1;
                den += d0 * d0;
            }
        float s = den > 0 ? (float)(num / den) : 1f;
        double sq = 0;
        float worst = 0f;
        foreach (var (d0, d1) in pairs)
        {
            float e = MathF.Abs(d1 - s * d0);
            sq += e * e;
            worst = MathF.Max(worst, e);
        }
        return (s, pairs.Count > 0 ? (float)Math.Sqrt(sq / pairs.Count) : 0f, worst);
    }

    /// <summary>Cloth vertices behind the skin, signed along the nearest skin point's normal, within 30 mm. Per whole
    /// cloth submesh, with how many sit under the breasts (y 1.15..1.30) so the clip can be placed.</summary>
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
