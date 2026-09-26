using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Proteus.Interop;
using Proteus.Services;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A refit posed exactly as the character stood in game (<c>/proteus posedump</c>) and drawn from behind: the posed
/// clip a rest-pose measure cannot see. Skin showing where the cloth covers is counted, per candidate model.
/// </summary>
public class PosedRenderDiagTests(ITestOutputHelper output)
{
    /// <summary>Where <c>/proteus posedump</c> writes when given no folder; PROTEUS_POSE_DIR points elsewhere.</summary>
    internal static string PoseDir => Environment.GetEnvironmentVariable("PROTEUS_POSE_DIR")
        ?? Path.Combine(Path.GetTempPath(), "proteus-pose");

    private const string TopGamePath = "chara/equipment/e0367/model/c0201e0367_top.mdl";

    /// <summary>How far behind the skin cloth may lie and still be cloth the skin has come through (10 mm).</summary>
    private const float ThroughDepth = 0.01f;

    internal sealed record Posed(Vector3[] At, int[] Tris, bool[] TriSkin, int[] TriMaterial, string[] Materials);

    /// <summary>The pose, the deformer and the models the dump holds; null when there is no dump.</summary>
    internal static (LivePose Pose, PbdFile? Pbd, List<(string File, string GamePath)> Models)? LoadDump(string dir)
    {
        var json = Path.Combine(dir, "pose.json");
        if (!File.Exists(json)) return null;
        var snap = JsonSerializer.Deserialize<LivePoseDump.Snapshot>(File.ReadAllText(json))!;
        var pbdPath = Path.Combine(dir, "human.pbd");
        var pbd = File.Exists(pbdPath) ? new PbdFile(File.ReadAllBytes(pbdPath)) : null;
        var models = snap.Models.Select(m => m.Split('\t')).Select(p => (Path.Combine(dir, p[0]), p[1])).ToList();
        return (LivePoseDump.Load(snap), pbd, models);
    }

    /// <summary>Pose a model's LOD 0 into the character's own model space (the root's facing and place taken off,
    /// so "behind" is always -Z).</summary>
    internal static Posed? Pose(byte[] bytes, string gamePath, LivePose pose, PbdFile? pbd)
    {
        var mesh = ModelSkinReader.Read(bytes, null, gamePath);
        if (mesh == null) return null;
        var world = new Vector3[mesh.VertexCount];
        new LiveMeshPoser().Pose(mesh, pose, pbd, world);
        Matrix4x4.Invert(pose.Root, out var toModel);
        for (int v = 0; v < world.Length; v++) world[v] = Vector3.Transform(world[v], toModel);
        var triSkin = new bool[mesh.TriangleCount];
        for (int t = 0; t < mesh.TriangleCount; t++)
            triSkin[t] = SecondSkinWriter.IsBodySkinMaterial(mesh.MaterialNames[mesh.TriangleMaterials[t]]);
        return new Posed(world, mesh.Triangles, triSkin, mesh.TriangleMaterials.Select(m => (int)m).ToArray(), mesh.MaterialNames);
    }

    /// <summary>
    /// Draw posed models from behind (looking along +Z), orthographic, into a PNG; returns how many pixels show skin
    /// inside the silhouette the cloth covers â€” skin through the cloth.
    /// </summary>
    internal static int Render(IEnumerable<Posed> models, string png, Vector2 lo, Vector2 hi, int width, bool fromBehind = true)
    {
        int height = (int)(width * (hi.Y - lo.Y) / (hi.X - lo.X));
        var depth = new float[width * height];
        var colour = new int[width * height];
        var clothDepth = new float[width * height];   // the nearest cloth here, whatever is drawn over it
        var isSkin = new bool[width * height];
        Array.Fill(depth, float.MaxValue);
        Array.Fill(clothDepth, float.MaxValue);
        Array.Fill(colour, unchecked((int)0xFF202028));
        var light = Vector3.Normalize(new Vector3(0.3f, 0.5f, fromBehind ? -1f : 1f));

        foreach (var m in models)
        {
            for (int t = 0; t < m.Tris.Length / 3; t++)
            {
                var a = m.At[m.Tris[t * 3]];
                var b = m.At[m.Tris[t * 3 + 1]];
                var c = m.At[m.Tris[t * 3 + 2]];
                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-20f) continue;
                n = Vector3.Normalize(n);
                bool skin = m.TriSkin[t];
                string mat = m.Materials[m.TriMaterial[t]];
                float shade = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(n, light));
                (int r, int g, int bl) = skin ? (230, 150, 110)
                                     : mat.Contains("_b.") || mat.EndsWith("_b.mtrl") ? (190, 190, 205)
                                     : (110, 170, 230);
                int col = unchecked((int)0xFF000000) | ((int)(r * shade) << 16) | ((int)(g * shade) << 8) | (int)(bl * shade);

                Vector2 P(Vector3 p) => new((fromBehind ? -(p.X - hi.X) : p.X - lo.X) / (hi.X - lo.X) * width,
                                            (hi.Y - p.Y) / (hi.Y - lo.Y) * height);
                float D(Vector3 p) => fromBehind ? p.Z : -p.Z;   // smaller is nearer the viewer
                var pa = P(a); var pb = P(b); var pc = P(c);
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
                int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
                int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
                float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pc.X - pa.X) * (pb.Y - pa.Y);
                if (MathF.Abs(area) < 1e-9f) continue;
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = ((pb.X - p.X) * (pc.Y - p.Y) - (pc.X - p.X) * (pb.Y - p.Y)) / area;
                    float w1 = ((pc.X - p.X) * (pa.Y - p.Y) - (pa.X - p.X) * (pc.Y - p.Y)) / area;
                    float w2 = 1f - w0 - w1;
                    if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                    float z = w0 * D(a) + w1 * D(b) + w2 * D(c);
                    int i = y * width + x;
                    if (!skin && z < clothDepth[i]) clothDepth[i] = z;
                    if (z >= depth[i]) continue;
                    depth[i] = z;
                    colour[i] = col;
                    isSkin[i] = skin;
                }
            }
        }

        int through = 0;
        for (int i = 0; i < colour.Length; i++)
        {
            // Skin drawn over cloth that lies just behind it: the cloth was meant to be on top. Cloth far behind is
            // the other side of the body.
            if (!isSkin[i] || clothDepth[i] - depth[i] > ThroughDepth) continue;
            if (i / width < height * 0.55f) through++;   // the upper back only: below it the sleeves cross the hips' edge
            colour[i] = unchecked((int)0xFFFF00FF);   // magenta: skin where cloth should be on top
        }

        var rgba = new byte[width * height * 4];
        for (int i = 0; i < colour.Length; i++)
        {
            rgba[i * 4] = (byte)(colour[i] >> 16);
            rgba[i * 4 + 1] = (byte)(colour[i] >> 8);
            rgba[i * 4 + 2] = (byte)colour[i];
            rgba[i * 4 + 3] = 255;
        }
        using var stream = File.Create(png);
        new StbImageWriteSharp.ImageWriter().WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
        return through;
    }

    /// <summary>
    /// The refit posed: every cloth vertex on the upper back that ends up inside the posed skin, with how far off the
    /// skin it was at rest and how differently it is rigged from the skin under it â€” geometry or weights.
    /// </summary>
    [Fact]
    public void Which_cloth_goes_inside_when_posed()
    {
        if (LoadDump(PoseDir) is not { } dump || !File.Exists(PopRefitDiagTests.ShippedPath)) return;
        var (pose, pbd, _) = dump;
        var shipped = File.ReadAllBytes(PopRefitDiagTests.ShippedPath);
        var garment = ModelPartReader.Read(shipped)!;
        var hard = Proteus.Gui.BodyRetargetPanel.ShapePieces(garment, BodyRetarget.HardPieces(garment).ToHashSet());
        var bytes = BodyRetarget.Plan(garment, shipped, PopRefitDiagTests.PairsFor("Medium"), "_top", replaceSkin: true,
                                      acrossBodies: true, keepShape: hard).Model;

        var rest = ModelPartReader.Read(bytes)!;
        var rig = ModelSkinReader.Read(bytes, null, TopGamePath)!;
        Assert.Equal(rest.Positions.Length, rig.VertexCount * 3);
        var world = new Vector3[rig.VertexCount];
        new LiveMeshPoser().Pose(rig, pose, pbd, world);
        Matrix4x4.Invert(pose.Root, out var toModel);
        var posedPos = new float[rest.Positions.Length];
        for (int v = 0; v < world.Length; v++)
        {
            var p = Vector3.Transform(world[v], toModel);
            posedPos[v * 3] = p.X; posedPos[v * 3 + 1] = p.Y; posedPos[v * 3 + 2] = p.Z;
        }
        var posed = new ModelParts
        {
            Positions = posedPos, Normals = rest.Normals, MeshSpans = rest.MeshSpans, Parts = rest.Parts,
            AttributeNames = rest.AttributeNames, Min = rest.Min, Max = rest.Max, ShatteredSubmeshes = rest.ShatteredSubmeshes,
        };
        // Normals are the rest ones; posed skin surfaces need their own â€” BodySurface derives them from the faces.
        var restSkin = new BodySurface(rest, 0.01f);
        var posedSkin = new BodySurface(posed, 0.01f);

        const int K = XivLiveMesh.SkinnedMesh.MaxInfluences;
        Dictionary<string, float> W(int v)
        {
            var d = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < K; k++)
            {
                float w = rig.BoneWeights[v * K + k];
                if (w > 0f) d[rig.BoneNames[rig.BoneIndices[v * K + k]]] = d.GetValueOrDefault(rig.BoneNames[rig.BoneIndices[v * K + k]]) + w;
            }
            return d;
        }
        Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

        var rows = new List<(float Posed, float AtRest, float Diff, Vector3 At, string Cloth, string Skin, string Part)>();
        foreach (var part in rest.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles.Distinct())
            {
                var q = At(posed, v);
                if (!posedSkin.Nearest(q, 0.02f, out var h)) continue;
                float s = Vector3.Dot(q - h.Point, h.Normal);
                if (s > -0.0005f) continue;
                var r = At(rest, v);
                if (r.Z > -0.01f || r.Y < 1.15f || MathF.Abs(r.X) > 0.12f) continue;   // the upper back, not the sleeves
                float s0 = restSkin.Nearest(r, 0.02f, out var h0) ? Vector3.Dot(r - h0.Point, h0.Normal) : float.NaN;
                var ws = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var (c, f) in new[] { (h.A, h.U), (h.B, h.V), (h.C, h.W) })
                    foreach (var (b, w) in W(c)) ws[b] = ws.GetValueOrDefault(b) + w * f;
                var wc = W(v);
                float diff = 0.5f * wc.Keys.Union(ws.Keys).Sum(b => MathF.Abs(wc.GetValueOrDefault(b) - ws.GetValueOrDefault(b)));
                string Top(Dictionary<string, float> d) => string.Join(" ", d.OrderByDescending(x => x.Value).Take(3).Select(x => $"{x.Key}:{x.Value:F2}"));
                rows.Add((s, s0, diff, r, Top(wc), Top(ws), part.Label));
            }
        output.WriteLine($"{rows.Count} upper-back cloth verts inside the posed skin");
        output.WriteLine($"  at rest: inside {rows.Count(r => r.AtRest < 0f)}, 0-1 mm {rows.Count(r => r.AtRest is >= 0f and < 0.001f)}, " +
                         $"1-3 mm {rows.Count(r => r.AtRest is >= 0.001f and < 0.003f)}, >3 mm {rows.Count(r => r.AtRest >= 0.003f)}");
        output.WriteLine($"  rigged apart from the skin: <5% {rows.Count(r => r.Diff < 0.05f)}, 5-15% {rows.Count(r => r.Diff is >= 0.05f and < 0.15f)}, " +
                         $">15% {rows.Count(r => r.Diff >= 0.15f)}");
        foreach (var r in rows.OrderBy(r => r.Posed).Take(15))
            output.WriteLine($"  posed {r.Posed * 1000,6:F2} rest {r.AtRest * 1000,6:F2} mm, apart {r.Diff:P0} at {r.At:F3} ({r.Part}) cloth [{r.Cloth}] skin [{r.Skin}]");
    }

    /// <summary>The refit as the panel would save it now (hard pieces kept), written next to the pose dump for
    /// <c>/proteus tempmodel</c> to put on the character.</summary>
    [Fact]
    public void Write_the_candidate()
    {
        if (!File.Exists(PopRefitDiagTests.ShippedPath) || !Directory.Exists(PoseDir)) return;
        var shipped = File.ReadAllBytes(PopRefitDiagTests.ShippedPath);
        var garment = ModelPartReader.Read(shipped)!;
        var hard = Proteus.Gui.BodyRetargetPanel.ShapePieces(garment, BodyRetarget.HardPieces(garment).ToHashSet());
        var planned = BodyRetarget.Plan(garment, shipped, PopRefitDiagTests.PairsFor("Medium"), "_top", replaceSkin: true,
                                        acrossBodies: true, keepShape: hard);
        var path = Path.Combine(PoseDir, "candidate.mdl");
        File.WriteAllBytes(path, planned.Model);
        output.WriteLine($"{path}: {planned.Model.Length:N0} bytes; moved up to {planned.Report.WorstMove * 1000:F1} mm, pushed {planned.Report.Pushed}");
    }

    [Fact]
    public void The_drawn_top_from_behind()
    {
        if (LoadDump(PoseDir) is not { } dump) { output.WriteLine("no pose dump"); return; }
        var (pose, pbd, models) = dump;
        output.WriteLine($"pose: {pose.BoneCount} bones, race c{pose.GenderRace:D4}; models: {string.Join(", ", models.Select(m => m.GamePath))}");

        var others = models.Where(m => m.GamePath != TopGamePath && File.Exists(m.File)
                                     && (m.GamePath.Contains("_dwn") || m.GamePath.Contains("_glv")))
                           .Select(m => Pose(File.ReadAllBytes(m.File), m.GamePath, pose, pbd)).OfType<Posed>().ToList();
        // A dump taken wearing something else has no "pop" to compare: nothing to say, rather than a failure.
        if (models.FirstOrDefault(m => m.GamePath == TopGamePath && File.Exists(m.File)).File is not { } drawn)
        {
            output.WriteLine($"the dump was not taken wearing {TopGamePath}");
            return;
        }

        var candidates = new List<(string Name, byte[] Bytes)> { ("drawn in game", File.ReadAllBytes(drawn)) };
        if (File.Exists(PopRefitDiagTests.ShippedPath))
        {
            var shipped = File.ReadAllBytes(PopRefitDiagTests.ShippedPath);
            var garment = ModelPartReader.Read(shipped)!;
            var hard = Proteus.Gui.BodyRetargetPanel.ShapePieces(garment, BodyRetarget.HardPieces(garment).ToHashSet());
            var pairs = PopRefitDiagTests.PairsFor("Medium");
            // Scoped to this call (see BodyRetarget.WithTuning): other tests refitting at the same time are untouched.
            byte[] Refit(BodyRetarget.Tuning tuning, bool keep)
                => BodyRetarget.WithTuning(tuning, () => BodyRetarget.Plan(garment, shipped, pairs, "_top", replaceSkin: true,
                                                                           acrossBodies: true, keepShape: keep ? hard : null).Model);
            candidates.Add(("like 1074", Refit(new BodyRetarget.Tuning(NoOwnSkinWeights: true), keep: false)));
            foreach (var (reach, tolerance) in new[] { (0f, 0.2f), (0.01f, 0.2f), (0.02f, 0.2f), (0.04f, 0.2f), (0.02f, 0.4f) })
                candidates.Add(($"copy {reach * 1000:F0}mm tol {tolerance:F1}",
                                Refit(new BodyRetarget.Tuning(CopyReach: reach, CopyTolerance: tolerance), keep: true)));
        }
        foreach (var (name, bytes) in candidates)
        {
            var top = Pose(bytes, TopGamePath, pose, pbd)!;
            var centre = top.At.Aggregate(Vector3.Zero, (s, p) => s + p) / top.At.Length;
            foreach (int degrees in new[] { 0, 40, -40 })
            {
                var turn = Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateRotationY(degrees * MathF.PI / 180f);
                Posed Turned(Posed m) => m with { At = m.At.Select(p => Vector3.Transform(p, turn)).ToArray() };
                string png = Path.Combine(PoseDir, $"back {degrees:+0;-0;0} - {name}.png");
                int through = Render([Turned(top), .. others.Select(Turned)], png, new Vector2(-0.3f, -0.3f),
                                     new Vector2(0.3f, 0.3f), 700);
                output.WriteLine($"{name} {degrees,3}Â°: skin through the cloth {through,6} px -> {Path.GetFileName(png)}");
            }
        }
    }
}
