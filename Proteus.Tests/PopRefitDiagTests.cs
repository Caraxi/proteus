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

    private const string Saved2 = Mod + @"\Body Retarget\Neolithe [ALL IN ONE] - DEFAULT ALMOND - NSFW Almond XS 2\" + TopRel;

    /// <summary>
    /// Reported on the second save (source YAB Medium): the arm bands (2.2's four islands, loose round the sleeve) come
    /// out wobbly, and the back still clips a little. The saved file's bands against the shipped ones, then the refit
    /// with and without the bands kept whole; and every cloth vertex inside the skin on the back, by place.
    /// </summary>
    [Fact]
    public void Arm_bands_and_the_back()
    {
        if (!File.Exists(Shipped) || !File.Exists(Saved2)) return;
        var bytes = File.ReadAllBytes(Shipped);
        var garment = ModelPartReader.Read(bytes)!;
        var saved = ModelPartReader.Read(File.ReadAllBytes(Saved2))!;

        // The writer keeps cloth in order, so each band's k-th vertex in the saved file is its k-th as shipped.
        for (int k = 1; k <= 4; k++)
        {
            var was = garment.Parts.First(p => p.Label == $"2.2.{k}").Triangles.Distinct().OrderBy(v => v).ToArray();
            var now = saved.Parts.FirstOrDefault(p => p.Material.Contains("top_a") && p.Island == k - 1
                                                   && p.Label.EndsWith($".2.{k}"))?.Triangles.Distinct().OrderBy(v => v).ToArray();
            if (now == null || now.Length != was.Length) { output.WriteLine($"band {k}: not matched"); continue; }
            var (s, rms, worst) = Distortion(was, now, garment, saved);
            output.WriteLine($"saved band 2.2.{k}: scale {s:F3}, bent rms {rms * 1000:F2} mm, worst {worst * 1000:F2} mm");
        }

        var pairs = Pairs("Medium");
        var marked = new HashSet<string> { "3.1", "3.3" };
        foreach (bool bands in new[] { false, true })
        {
            if (bands) marked.Add("2.2");
            var solved = BodyRetarget.Solve(garment, pairs, "_top", replaceSkin: true,
                                            keepShape: Proteus.Gui.BodyRetargetPanel.ShapePieces(garment, marked));
            var moved = Moved(garment, solved.Edit);
            output.WriteLine($"keep shape {string.Join(",", marked)}:");
            for (int k = 1; k <= 4; k++)
                Shape($"2.2.{k}", garment.Parts.First(p => p.Label == $"2.2.{k}").Triangles.Distinct().ToArray(), garment, moved);
            Clipping("  refit vs Almond XS", moved, pairs[0].Target);
        }

        // The back of the saved file: cloth inside its own skin, placed.
        var skin = new BodySurface(Skin(saved), 0.01f);
        var back = new List<(float S, Vector3 At, string Part)>();
        foreach (var part in saved.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles.Distinct())
            {
                var p = At(saved, v);
                if (p.Z > 0f || !skin.Nearest(p, 0.03f, out var h)) continue;
                float s = Vector3.Dot(p - h.Point, h.Normal);
                if (s < -0.0005f) back.Add((s, p, part.Label));
            }
        output.WriteLine($"saved file, back: {back.Count} cloth verts inside the skin");
        foreach (var g in back.GroupBy(b => (b.Part, Y: MathF.Round(b.At.Y * 50f) / 50f)).OrderBy(g => g.Key.Y))
            output.WriteLine($"  {g.Key.Part} y~{g.Key.Y:F2}: {g.Count()} verts, deepest {-g.Min(b => b.S) * 1000:F1} mm, " +
                             $"x {g.Min(b => b.At.X):F3}..{g.Max(b => b.At.X):F3}");
    }

    /// <summary>Skin vertices in FRONT of the cloth over them (nearest cloth face within 10 mm, signed along its face
    /// normal) on the back, z &lt; 0: skin through the middle of cloth faces, which a vertex test cannot see. The
    /// shipped top against its own skin, then the saved refit.</summary>
    [Fact]
    public void Skin_through_the_back()
    {
        if (!File.Exists(Shipped) || !File.Exists(Saved2)) return;
        foreach (var (label, path) in new[] { ("shipped", Shipped), ("saved", Saved2) })
        {
            var m = ModelPartReader.Read(File.ReadAllBytes(path))!;
            var cloth = new List<(Vector3 A, Vector3 B, Vector3 C, string Part)>();
            foreach (var part in m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                    cloth.Add((At(m, part.Triangles[t]), At(m, part.Triangles[t + 1]), At(m, part.Triangles[t + 2]), part.Label));
            var through = new List<(float S, Vector3 At, string Part)>();
            int under = 0;
            foreach (int v in m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct())
            {
                var s = At(m, v);
                if (s.Z > 0f) continue;
                float best = 0.01f, signed = 0f;
                string part = "";
                foreach (var (a, b, c, pl) in cloth)
                {
                    if (MathF.Abs(a.Y - s.Y) > 0.03f || MathF.Abs(a.X - s.X) > 0.03f) continue;
                    var q = BrushTransfer.ClosestOnTriangle(s, a, b, c, out _, out _, out _);
                    float d = Vector3.Distance(s, q);
                    if (d >= best) continue;
                    var n = Vector3.Cross(b - a, c - a);
                    if (n.LengthSquared() < 1e-20f) continue;
                    best = d;
                    signed = Vector3.Dot(s - q, Vector3.Normalize(n));
                    part = pl;
                }
                if (best >= 0.01f) continue;
                under++;
                if (signed > 0.0002f) through.Add((signed, s, part));
            }
            output.WriteLine($"{label}: back skin under cloth {under}, in front of it {through.Count}");
            foreach (var g in through.GroupBy(b => (b.Part, Y: MathF.Round(b.At.Y * 50f) / 50f)).OrderBy(g => g.Key.Y))
                output.WriteLine($"  {g.Key.Part} y~{g.Key.Y:F2}: {g.Count()} skin verts, worst {g.Max(b => b.S) * 1000:F1} mm, " +
                                 $"x {g.Min(b => b.At.X):F3}..{g.Max(b => b.At.X):F3} z {g.Min(b => b.At.Z):F3}..{g.Max(b => b.At.Z):F3}");
        }
    }

    /// <summary>Back cloth within 4 mm of the skin: how differently it is weighted from the skin under it (half the L1
    /// distance of the two bone weightings: 0 same, 1 nothing shared), shipped and saved, by height. Cloth and skin on
    /// different bones part in a pose even when the rest pose is clear.</summary>
    [Fact]
    public void Back_weights_against_the_skin()
    {
        if (!File.Exists(Shipped) || !File.Exists(Saved2)) return;
        var shippedBytes = File.ReadAllBytes(Shipped);
        var garment = ModelPartReader.Read(shippedBytes)!;
        var planned = BodyRetarget.Plan(garment, shippedBytes, Pairs("Medium"), "_top", replaceSkin: true, acrossBodies: true,
                                        keepShape: Proteus.Gui.BodyRetargetPanel.ShapePieces(garment, new HashSet<string> { "2.2", "3.1", "3.3" }));
        foreach (var (label, bytes) in new[] { ("shipped", shippedBytes), ("saved", File.ReadAllBytes(Saved2)), ("refit now", planned.Model) })
        {
            var m = ModelPartReader.Read(bytes)!;
            var skin = ModelSkinReader.Read(bytes, null, null)!;
            var surface = new BodySurface(m, 0.01f);
            const int K = XivLiveMesh.SkinnedMesh.MaxInfluences;
            Dictionary<string, float> W(int v)
            {
                var d = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int k = 0; k < K; k++)
                {
                    float w = skin.BoneWeights[v * K + k];
                    if (w <= 0f) continue;
                    string b = skin.BoneNames[skin.BoneIndices[v * K + k]];
                    d[b] = d.GetValueOrDefault(b) + w;
                }
                return d;
            }
            float Diff(Dictionary<string, float> a, Dictionary<string, float> b)
                => 0.5f * a.Keys.Union(b.Keys).Sum(k => MathF.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k)));

            var rows = new List<(float Y, float Diff, float Off, string Part, string Cloth, string Skin, Vector3 At, float Skin2)>();
            foreach (var part in m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                foreach (int v in part.Triangles.Distinct())
                {
                    var p = At(m, v);
                    if (p.Z > -0.02f || !surface.Nearest(p, 0.004f, out var h)) continue;
                    var ws = new Dictionary<string, float>(StringComparer.Ordinal);
                    foreach (var (c, f) in new[] { (h.A, h.U), (h.B, h.V), (h.C, h.W) })
                        foreach (var (b, w) in W(c)) ws[b] = ws.GetValueOrDefault(b) + w * f;
                    var wc = W(v);
                    string Top(Dictionary<string, float> d) => string.Join(" ", d.OrderByDescending(x => x.Value).Take(3).Select(x => $"{x.Key}:{x.Value:F2}"));
                    rows.Add((p.Y, Diff(wc, ws), Vector3.Dot(p - h.Point, h.Normal), part.Label, Top(wc), Top(ws), p, h.Distance));
                }
            output.WriteLine($"{label}: {rows.Count} back cloth verts within 4 mm of skin");
            foreach (var g in rows.GroupBy(r => MathF.Round(r.Y * 25f) / 25f).OrderBy(g => g.Key))
            {
                var worst = g.OrderByDescending(r => r.Diff).First();
                output.WriteLine($"  y~{g.Key:F2}: {g.Count(),4} verts, weights differ mean {g.Average(r => r.Diff):P0}, " +
                                 $">30% {g.Count(r => r.Diff > 0.3f)}; worst {worst.Diff:P0} ({worst.Part}) cloth [{worst.Cloth}] skin [{worst.Skin}]");
            }
            foreach (var r in rows.Where(r => r.Diff > 0.1f).OrderByDescending(r => r.Diff).Take(8))
                output.WriteLine($"    {r.Diff:P0} at {r.At:F3} ({r.Part}), {r.Skin2 * 1000:F1} mm off skin, {r.Off * 1000:F1} signed");
            output.WriteLine($"    over 10%: {rows.Count(r => r.Diff > 0.1f)}");
        }
    }

    /// <summary>The hard-piece rule on "pop": every arm band, chain link, underwire and the waist-front piece, and
    /// nothing of its cloth.</summary>
    [Fact]
    public void The_hard_pieces_are_the_metal_and_the_bands()
    {
        if (!File.Exists(Shipped)) return;
        var m = ModelPartReader.Read(File.ReadAllBytes(Shipped))!;
        var hard = BodyRetarget.HardPieces(m);
        var islands = m.Parts.Where(p => p.Island >= 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)).Select(p => p.Label).ToList();
        output.WriteLine($"{hard.Count} of {islands.Count} islands hard; not: {string.Join(", ", islands.Except(hard))}");
        Assert.Equal(islands.OrderBy(l => l), hard.OrderBy(l => l));
        Assert.Contains("2.2.1", hard);
        Assert.Contains("3.1.1", hard);
    }

    /// <summary>The same rule over other garments on this machine, to see what else it would take for hard.</summary>
    [Fact]
    public void Hard_pieces_elsewhere()
    {
        foreach (var dir in new[] { @"E:\Penumbradt\This Old Thing - by Solona", @"E:\Penumbradt\Seaside", @"E:\Penumbradt\Ruffles - by Solona",
                                    @"E:\Penumbradt\Date Night Skirt Yab Compat - by Solona", @"E:\Penumbradt\Sweep the Leg - by Solona",
                                    @"E:\Penumbradt\[ninka] - anemone (neolithe)", @"E:\Penumbradt\CO Rayne for YabRueWC" })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.mdl", SearchOption.AllDirectories)
                                          .Where(p => !p.Contains(@"\Proteus\") && !p.Contains("Body Retarget")).Take(12))
            {
                var m = ModelPartReader.Read(File.ReadAllBytes(path));
                if (m == null) continue;
                var hard = BodyRetarget.HardPieces(m);
                int islands = m.Parts.Count(p => p.Island >= 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material));
                if (hard.Count == 0) continue;
                output.WriteLine($"{Path.GetRelativePath(@"E:\Penumbradt", path)}: {hard.Count}/{islands} hard: " +
                                 string.Join(", ", hard.Take(12).Select(l =>
                                 {
                                     var p = m.Parts.First(q => q.Label == l);
                                     var vs = p.Triangles.Distinct().Select(v => At(m, v)).ToList();
                                     var e = (vs.Aggregate(Vector3.Max) - vs.Aggregate(Vector3.Min)) * 1000f;
                                     return $"{l}[{p.Material.Split('/').Last()} {e.X:F0}x{e.Y:F0}x{e.Z:F0}]";
                                 })));
            }
        }
    }

    /// <summary>Every island of the shipped top: closed (no open edge once welded by position) or not, how many open
    /// edges, its size, and how far it sits off the skin — what a rule for hard pieces could go on.</summary>
    [Theory]
    [InlineData(Shipped)]
    [InlineData(@"E:\Penumbradt\Ruffles - by Solona\bottom size\medium\chara\equipment\e6010\model\c0201e6010_dwn.mdl")]
    [InlineData(@"E:\Penumbradt\[ninka] - anemone (neolithe)\size - hoodie\neolithe - m\chara\equipment\e6209\model\c0201e6209_top.mdl")]
    [InlineData(@"E:\Penumbradt\Date Night Skirt Yab Compat - by Solona\items\chara\equipment\e0233\model\c0201e0233_dwn.mdl")]
    public void What_each_island_is(string path)
    {
        if (!File.Exists(path)) return;
        var fileBytes = File.ReadAllBytes(path);
        var m = ModelPartReader.Read(fileBytes)!;
        var skin = new BodySurface(m, 0.01f);
        var rig = ModelSkinReader.Read(fileBytes, null, null)!;
        const int K = XivLiveMesh.SkinnedMesh.MaxInfluences;
        // How far a piece's vertices' weights stray from the piece's own average: 0 = all rigged alike.
        string Spread(int[] vs)
        {
            var mean = new Dictionary<string, float>(StringComparer.Ordinal);
            var each = vs.Select(v =>
            {
                var d = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int k = 0; k < K; k++)
                {
                    float w = rig.BoneWeights[v * K + k];
                    if (w <= 0f) continue;
                    string b = rig.BoneNames[rig.BoneIndices[v * K + k]];
                    d[b] = d.GetValueOrDefault(b) + w;
                    mean[b] = mean.GetValueOrDefault(b) + w / vs.Length;
                }
                return d;
            }).ToList();
            var diffs = each.Select(d => 0.5f * mean.Keys.Union(d.Keys).Sum(b => MathF.Abs(d.GetValueOrDefault(b) - mean.GetValueOrDefault(b))))
                            .OrderBy(x => x).ToList();
            return $"rig spread mean {diffs.Average():P0} max {diffs[^1]:P0}, bones {string.Join(" ", mean.OrderByDescending(x => x.Value).Take(3).Select(x => $"{x.Key}:{x.Value:F2}"))}";
        }
        var rows = new List<string>();
        foreach (var part in m.Parts.Where(p => p.Island >= 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
        {
            var verts = part.Triangles.Distinct().ToArray();
            var at = verts.Select(v => BodyRetarget.ToVec(At(m, v))).ToArray();
            var local = verts.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
            var node = MeshMath.WeldByPosition(at, out _);
            var edges = new Dictionary<(int, int), int>();
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = node[local[part.Triangles[t + k]]], b = node[local[part.Triangles[t + (k + 1) % 3]]];
                    if (a == b) continue;
                    var key = a < b ? (a, b) : (b, a);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            int open = edges.Values.Count(c => c == 1);
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            float near = float.MaxValue, far = 0f;
            var ds = new List<float>();
            foreach (int v in verts)
            {
                var p = At(m, v);
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                float d = skin.Nearest(p, 0.2f, out var h) ? h.Distance : 0.2f;
                near = MathF.Min(near, d); far = MathF.Max(far, d);
                ds.Add(d);
            }
            ds.Sort();
            var e = (hi - lo) * 1000f;
            rows.Add($"{part.Label,-9} {part.Material.Split('_').Last(),-7} {verts.Length,5} v  open edges {open,4}/{edges.Count,5}  " +
                     $"size {e.X:F0}x{e.Y:F0}x{e.Z:F0} mm  off skin {near * 1000:F1}..{far * 1000:F1} median {ds[ds.Count / 2] * 1000:F1} mm" +
                     $"  hard {BodyRetarget.HardPieces(m).Contains(part.Label)}  {Spread(verts)}");
        }
        // The chain links are many alike: every row but a few of them.
        foreach (var r in rows.Where((r, i) => !r.StartsWith("3.1.") || i % 20 == 0 || r.StartsWith("3.1.1 ") || r.StartsWith("3.1.2 ")))
            output.WriteLine(r);
    }

    private static (float Scale, float Rms, float Worst) Distortion(int[] was, int[] now, ModelParts before, ModelParts after)
        => Distortion(Enumerable.Range(0, was.Length).ToArray(), i => At(before, was[i]), i => At(after, now[i]));

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

    internal const string ShippedPath = Shipped;

    internal static List<BodyRetarget.SlotPair> PairsFor(string from) => Pairs(from);

    private static List<BodyRetarget.SlotPair> Pairs(string from = "Yiggle - Medium")
    {
        var yab = BodySizeCatalog.Read(YabRoot);
        var neo = BodySizeCatalog.Read(NeolitheRoot);
        string sourcePath = yab.PathOf(yab.For("_top").First(o => o.Name == from));
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
        => Distortion(verts, v => At(before, v), v => At(after, v));

    private static (float Scale, float Rms, float Worst) Distortion(int[] verts, Func<int, Vector3> before, Func<int, Vector3> after)
    {
        int step = Math.Max(1, verts.Length / 300);
        var sample = verts.Where((_, i) => i % step == 0).ToArray();
        double num = 0, den = 0;
        var pairs = new List<(float D0, float D1)>();
        for (int i = 0; i < sample.Length; i++)
            for (int j = i + 1; j < sample.Length; j++)
            {
                float d0 = Vector3.Distance(before(sample[i]), before(sample[j]));
                float d1 = Vector3.Distance(after(sample[i]), after(sample[j]));
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
