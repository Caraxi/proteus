using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Reported: a body swap from a gen3 mod onto Rue+ (bibo) does not work. Both transfer maps ship
/// (<c>gen3_to_bibo</c> and <c>bibo_to_gen3</c>), so the pair is supposed to be buildable; these find out where it
/// actually stops.
/// </summary>
public class Gen3ToRueDiagTests(ITestOutputHelper output)
{
    private const string Mods = @"E:\Penumbradt";
    /// <summary>The PLUGIN directory. UVRemapService appends "uvmaps" to it itself — passing the maps folder sends
    /// it looking in uvmaps\uvmaps, which reports "map not found" and looks exactly like an unsupported pair.</summary>
    private const string PluginDir = @"E:\repos\Proteus\Proteus";

    private static readonly string Maps = Path.Combine(PluginDir, "uvmaps");

    /// <summary>A remapper that prints what it logs, so a missing map or a dead codec cannot look like a refusal.</summary>
    private UVRemapService Remap()
    {
        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        log.WhenForAnyArgs(l => l.Warning(default(string)!)).Do(c => output.WriteLine("   log warn: " + c.Arg<string>()));
        log.WhenForAnyArgs(l => l.Error(default(string)!)).Do(c => output.WriteLine("   log error: " + c.Arg<string>()));
        log.WhenForAnyArgs(l => l.Information(default(string)!)).Do(c => output.WriteLine("   log info: " + c.Arg<string>()));
        output.WriteLine($"   maps dir: {Maps} (exists: {Directory.Exists(Maps)}, " +
                         $"{(Directory.Exists(Maps) ? Directory.GetFiles(Maps, "*.tif").Length : 0)} tif)");
        return new UVRemapService(log, PluginDir);
    }

    /// <summary>Every installed body mod, the slots it sizes, and the texture layout its models are drawn with.</summary>
    [Fact]
    public void Which_bodies_are_installed_and_what_layout_each_is()
    {
        if (!Directory.Exists(Mods)) { output.WriteLine("no mods root"); return; }

        output.WriteLine($"{"layout",-8}{"sizes",7}  mod");
        foreach (string dir in Directory.GetDirectories(Mods).OrderBy(d => d))
        {
            BodySizeCatalog catalog;
            try { catalog = BodySizeCatalog.Read(dir); } catch { continue; }
            if (!catalog.IsBody) continue;

            // The layout is read off a model, so take the first size that has a readable file.
            string layout = "?";
            foreach (var option in catalog.Options)
            {
                string path = catalog.PathOf(option);
                if (!File.Exists(path)) continue;
                if (ModelPartReader.Read(File.ReadAllBytes(path)) is not { } parts) continue;
                layout = BodyCorrespondence.LayoutOf(parts) ?? "none";
                break;
            }
            output.WriteLine($"{layout,-8}{catalog.Options.Count,7}  {Path.GetFileName(dir)}");
        }
    }

    /// <summary>
    /// Build the correspondence the reported swap needs: a gen3 body to a bibo one. Prints the refusal when it will
    /// not build, and how much of the body it matched when it will — a correspondence that builds but matches almost
    /// nothing is the other way this "does not work".
    /// </summary>
    [Fact]
    public void Can_a_gen3_body_be_paired_with_a_bibo_one()
    {
        if (Pick("gen3") is not { } gen3 || Pick("bibo") is not { } bibo)
        {
            output.WriteLine("need one gen3 and one bibo body installed");
            return;
        }
        output.WriteLine($"gen3: {gen3.Mod}  /  {gen3.Option.FullLabel}");
        output.WriteLine($"bibo: {bibo.Mod}  /  {bibo.Option.FullLabel}");

        foreach (var (a, b, way) in new[] { (gen3, bibo, "gen3 -> bibo"), (bibo, gen3, "bibo -> gen3") })
        {
            byte[] sb = File.ReadAllBytes(a.Path), tb = File.ReadAllBytes(b.Path);
            var source = ModelPartReader.Read(sb)!;
            var target = ModelPartReader.Read(tb)!;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool ok = BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sb),
                                                  target, BodyRetargetDiagTests.Uv(tb),
                                                  "_top", out var built, out string refusal, Remap());
            clock.Stop();

            output.WriteLine("");
            output.WriteLine($"{way} in {clock.ElapsedMilliseconds} ms: {(ok ? built!.Describe() : "REFUSED — " + refusal)}");
        }
    }

    /// <summary>Straight at the converter: does a gen3-to-bibo map load at all?</summary>
    [Fact]
    public void Does_the_gen3_to_bibo_map_load()
    {
        var remap = Remap();
        foreach (var (from, to) in new[] { ("gen3", "bibo"), ("bibo", "gen3") })
        {
            var convert = remap.UvConverter(from, to, unmirror: true);
            output.WriteLine($"{from} -> {to}: {(convert == null ? "NO CONVERTER" : "built")}");
            if (convert == null) continue;

            // And does it answer for a uv in the middle of the sheet?
            var answered = convert(0.5f, 0.5f, 1);
            output.WriteLine($"     (0.5, 0.5) maps to {(answered is { } a ? $"({a.Item1:F3}, {a.Item2:F3})" : "nothing")}");
        }
    }

    /// <summary>
    /// What a gen3 garment's materials and uvs look like AFTER a refit onto a bibo body. The geometry pairs at 99.7%,
    /// so a swap that "does not work" has to be failing further down: the skin mesh drawn with a material of the wrong
    /// layout would texture it with the other body's sheet, which reads as scrambled skin rather than as a bad fit.
    /// </summary>
    [Fact]
    public void What_a_gen3_to_bibo_refit_does_to_materials_and_uvs()
    {
        if (Pick("gen3") is not { } gen3 || PickMod("hs-Rue+", "bibo") is not { } rue)
        {
            output.WriteLine("need a gen3 body and Rue+ installed");
            return;
        }
        output.WriteLine($"garment stands in as: {gen3.Mod} / {gen3.Option.FullLabel}");
        output.WriteLine($"refit onto:           {rue.Mod} / {rue.Option.FullLabel}");

        byte[] garmentBytes = File.ReadAllBytes(gen3.Path);
        byte[] sourceBytes = File.ReadAllBytes(gen3.Path);      // made for its own body, which is the honest pairing
        byte[] targetBytes = File.ReadAllBytes(rue.Path);
        var garment = ModelPartReader.Read(garmentBytes)!;
        var sourceParts = ModelPartReader.Read(sourceBytes)!;
        var targetParts = ModelPartReader.Read(targetBytes)!;

        if (!BodyCorrespondence.TryBuild(sourceParts, BodyRetargetDiagTests.Uv(sourceBytes),
                                         targetParts, BodyRetargetDiagTests.Uv(targetBytes),
                                         "_top", out var built, out string refusal, Remap()))
        {
            output.WriteLine($"REFUSED: {refusal}");
            return;
        }
        output.WriteLine($"correspondence: {built!.Describe()}");

        var pairs = new List<BodyRetarget.SlotPair>
        {
            new("_top", built, targetParts, targetBytes, sourceBytes),
        };

        Show("the garment as authored", garmentBytes);
        Show("the target body", targetBytes);
        foreach (bool swap in new[] { false, true })
        {
            var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top",
                                            replaceSkin: swap, acrossBodies: true);
            Show($"refit, new body's skin = {swap}", planned.Model);
        }
    }

    /// <summary>Each whole submesh: its material, the layout that material implies, and the uv range it covers.</summary>
    private void Show(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } m) { output.WriteLine($"{label}: unreadable"); return; }
        float[] uv = BodyRetargetDiagTests.Uv(mdl);

        output.WriteLine("");
        output.WriteLine($"--- {label}");
        foreach (var span in m.MeshSpans)
        {
            var part = m.Parts.FirstOrDefault(p => p.Mesh == span.Mesh && p.Island < 0);
            if (part == null) continue;
            string layout = SecondSkinWriter.SkinMaterialBodyType(part.Material) ?? "-";
            float lo = 9f, hi = -9f;
            for (int i = 0; i < span.Count; i++)
            {
                int at = (span.BaseVertex + i) * 2;
                if (at + 1 >= uv.Length) break;
                lo = MathF.Min(lo, uv[at]);
                hi = MathF.Max(hi, uv[at]);
            }
            output.WriteLine($"    mesh {span.Mesh}  {span.Count,6:N0} verts  layout {layout,-5}  " +
                             $"u {lo,6:F3}..{hi,6:F3}  {part.Material}");
        }
    }

    /// <summary>A named mod's first readable chest size, checked to be the layout expected.</summary>
    private (string Mod, BodyOption Option, string Path)? PickMod(string startsWith, string layout)
    {
        if (!Directory.Exists(Mods)) return null;
        foreach (string dir in Directory.GetDirectories(Mods).OrderBy(d => d))
        {
            if (!Path.GetFileName(dir).StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)) continue;
            var catalog = BodySizeCatalog.Read(dir);
            foreach (var option in catalog.For("_top", "0201"))
            {
                string path = catalog.PathOf(option);
                if (!File.Exists(path)) continue;
                if (ModelPartReader.Read(File.ReadAllBytes(path)) is not { } parts) continue;
                if (!string.Equals(BodyCorrespondence.LayoutOf(parts), layout, StringComparison.OrdinalIgnoreCase))
                    continue;
                return (Path.GetFileName(dir), option, path);
            }
        }
        return null;
    }

    /// <summary>
    /// A PRE-DAWNTRAIL (v5) garment through the refit. The re-emit forces the header to v6 because it only writes v6
    /// bone tables, so everything else about the model has to survive that upgrade: the material list, which mesh is
    /// drawn with which material, the texture coordinates, and the vertex count.
    /// </summary>
    [Theory]
    [InlineData(@"E:\Penumbradt\Ginger - by Solona\Riviera Dress\Everything\chara\equipment\e9069\model\c0201e9069_top.mdl")]
    [InlineData(@"E:\Penumbradt\Christina - by Solona\Size\Medium\chara\equipment\e0118\model\c0201e0118_top.mdl")]
    public void A_pre_dawntrail_garment_through_the_refit(string garmentFile)
    {
        if (!File.Exists(garmentFile)) { output.WriteLine("not installed"); return; }
        if (Pick("gen3") is not { } gen3 || PickMod("hs-Rue+", "bibo") is not { } rue) return;

        byte[] garmentBytes = File.ReadAllBytes(garmentFile);
        output.WriteLine($"{Path.GetFileName(garmentFile)}: version 0x{BitConverter.ToUInt32(garmentBytes, 0):x8}");

        byte[] sourceBytes = File.ReadAllBytes(gen3.Path), targetBytes = File.ReadAllBytes(rue.Path);
        var sourceParts = ModelPartReader.Read(sourceBytes)!;
        var targetParts = ModelPartReader.Read(targetBytes)!;
        if (!BodyCorrespondence.TryBuild(sourceParts, BodyRetargetDiagTests.Uv(sourceBytes),
                                         targetParts, BodyRetargetDiagTests.Uv(targetBytes),
                                         "_top", out var built, out string refusal, Remap()))
        {
            output.WriteLine($"REFUSED: {refusal}");
            return;
        }

        var garment = ModelPartReader.Read(garmentBytes)!;
        var pairs = new List<BodyRetarget.SlotPair> { new("_top", built!, targetParts, targetBytes, sourceBytes) };
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: false, acrossBodies: true);

        output.WriteLine($"   out version 0x{BitConverter.ToUInt32(planned.Model, 0):x8}");
        Show("before", garmentBytes);
        Show("after", planned.Model);

        // The uvs must be untouched: the refit moves positions only, and a shifted uv is a scrambled texture.
        float[] a = BodyRetargetDiagTests.Uv(garmentBytes), b = BodyRetargetDiagTests.Uv(planned.Model);
        if (a.Length != b.Length)
        {
            output.WriteLine($"   UV COUNT CHANGED: {a.Length / 2:N0} -> {b.Length / 2:N0}");
            return;
        }
        int moved = 0;
        float worst = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float d = MathF.Abs(a[i] - b[i]);
            if (d > 1e-6f) moved++;
            worst = MathF.Max(worst, d);
        }
        output.WriteLine($"   uvs: {moved:N0} of {a.Length:N0} changed, worst {worst:F6}");
    }

    /// <summary>
    /// Reported: Eve (gen3) legs refused onto LaRue (bibo) at 73%. The chest pairs at 99.7%; this measures the LEGS,
    /// and where on the leg the misses are — the converter finding no answer, or the atlas finding no landing.
    /// </summary>
    [Theory]
    [InlineData("[Cry] AB Body", "hs-Rue+")]
    [InlineData("The hrBody 3", "hs-Rue+")]
    [InlineData("hs-Rue+", "[Cry] AB Body")]
    public void Can_gen3_legs_be_paired_with_bibo_legs(string fromMod, string toMod)
    {
        if (PickSlot(fromMod, "_dwn") is not { } a || PickSlot(toMod, "_dwn") is not { } b)
        {
            output.WriteLine("mods not installed");
            return;
        }
        output.WriteLine($"from: {a.Mod} / {a.Option.FullLabel}");
        output.WriteLine($"to:   {b.Mod} / {b.Option.FullLabel}");

        byte[] sb = File.ReadAllBytes(a.Path), tb = File.ReadAllBytes(b.Path);
        var source = ModelPartReader.Read(sb)!;
        var target = ModelPartReader.Read(tb)!;
        var remap = Remap();
        string from = BodyCorrespondence.LayoutOf(source)!, to = BodyCorrespondence.LayoutOf(target)!;
        output.WriteLine($"layouts: {from} -> {to}");
        foreach (var part in source.Parts.Where(p => p.Island < 0).Select(p => p.Material).Distinct())
            output.WriteLine($"   source material {part} -> {SecondSkinWriter.SkinMaterialBodyType(part) ?? "not skin"}");

        bool ok = BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sb), target, BodyRetargetDiagTests.Uv(tb),
                                              "Legs", out var built, out string refusal, remap);
        output.WriteLine(ok ? built!.Describe() : "REFUSED — " + refusal);


        // Where the converter gives up, by height and by uv.
        var convert = remap.UvConverter(from, to, unmirror: true, fold: true)!;
        var uv = BodyCorrespondence.SameTile(BodyRetargetDiagTests.Uv(sb));
        var skin = source.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                         .SelectMany(p => p.Triangles).Distinct().ToList();
        var noConvert = skin.Where(v => convert(uv[v * 2], uv[v * 2 + 1], source.Positions[v * 3] >= 0 ? 1 : -1) == null).ToList();
        output.WriteLine($"converter has no answer for {noConvert.Count:N0} of {skin.Count:N0} skin vertices");
        float minY = skin.Min(v => source.Positions[v * 3 + 1]), maxY = skin.Max(v => source.Positions[v * 3 + 1]);
        output.WriteLine($"height {minY:F3}..{maxY:F3}; misses by tenth of height (bottom first):");
        var bands = new int[10]; var all = new int[10];
        int Band(int v) => Math.Clamp((int)((source.Positions[v * 3 + 1] - minY) / (maxY - minY) * 10), 0, 9);
        foreach (int v in skin) all[Band(v)]++;
        foreach (int v in noConvert) bands[Band(v)]++;
        for (int i = 0; i < 10; i++) output.WriteLine($"   {i}: {bands[i],5} / {all[i],5}");
        var uvGrid = new int[4, 4];
        foreach (int v in noConvert) uvGrid[Math.Min(3, (int)(uv[v * 2] * 4)), Math.Min(3, (int)(uv[v * 2 + 1] * 4))]++;
        output.WriteLine("misses by uv quarter (rows v, cols u):");
        for (int y = 0; y < 4; y++)
            output.WriteLine("   " + string.Join(" ", Enumerable.Range(0, 4).Select(x => $"{uvGrid[x, y],5}")));
    }

    /// <summary>A picture of where the gen3-to-bibo map answers (grey), with a gen3 body's legs (green: answered, red:
    /// not) and chest (blue) drawn over it.</summary>
    [Fact]
    public void Picture_the_gen3_to_bibo_map_under_gen3_legs()
    {
        if (PickSlot("[Cry] AB Body", "_dwn") is not { } legs || PickSlot("[Cry] AB Body", "_top") is not { } top) return;
        var convert = Remap().UvConverter("gen3", "bibo", unmirror: true, fold: true)!;
        const int N = 512;
        var px = new byte[N * N * 3];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            byte g = convert((x + 0.5f) / N, (y + 0.5f) / N, 1) != null ? (byte)110 : (byte)20;
            px[(y * N + x) * 3] = px[(y * N + x) * 3 + 1] = px[(y * N + x) * 3 + 2] = g;
        }
        void Plot(string path, Func<bool, (byte, byte, byte)> colour)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var parts = ModelPartReader.Read(bytes)!;
            var uv = BodyCorrespondence.SameTile(BodyRetargetDiagTests.Uv(bytes));
            foreach (int v in parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                   .SelectMany(p => p.Triangles).Distinct())
            {
                bool ok = convert(uv[v * 2], uv[v * 2 + 1], parts.Positions[v * 3] >= 0 ? 1 : -1) != null;
                var (r, gg, b) = colour(ok);
                int x = Math.Clamp((int)(uv[v * 2] * N), 0, N - 1), y = Math.Clamp((int)(uv[v * 2 + 1] * N), 0, N - 1);
                (px[(y * N + x) * 3], px[(y * N + x) * 3 + 1], px[(y * N + x) * 3 + 2]) = (r, gg, b);
            }
        }
        Plot(top.Path, _ => (60, 120, 255));
        Plot(legs.Path, ok => ok ? ((byte)60, (byte)220, (byte)60) : ((byte)255, (byte)50, (byte)50));
        string file = Path.Combine(Path.GetTempPath(), "gen3_to_bibo_legs.png");
        using var stream = File.Create(file);
        new StbImageWriteSharp.ImageWriter().WritePng(px, N, N, StbImageWriteSharp.ColorComponents.RedGreenBlue, stream);
        output.WriteLine(file);
    }

    /// <summary>Eve (a gen3 body), as the reporter sent it.</summary>
    private const string EvePmp = @"C:\Users\solon\OneDrive\Desktop\eve.pmp";

    /// <summary>One file out of the Eve pack, or null when the pack is not here.</summary>
    private static byte[]? EveFile(string entry)
    {
        if (!File.Exists(EvePmp)) return null;
        using var zip = System.IO.Compression.ZipFile.OpenRead(EvePmp);
        if (zip.GetEntry(entry) is not { } e) return null;
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>
    /// The reported pair: Eve (gen3, from eve.pmp) legs onto LaRue (bibo, inside LavaBod+). What the refusal counts,
    /// and whether the points the converter cannot place are joined to the rest of the leg by position — if they are,
    /// the hole fill can carry them.
    /// </summary>
    [Theory]
    [InlineData("Legs - Default.mdl")]
    [InlineData("Legs - Smallclothes.mdl")]
    [InlineData("Legs - Animated Genitalia.mdl")]
    public void Eve_legs_onto_LaRue(string file)
    {
        string lava = Path.Combine(Mods, "LavaBod+ 2.2");
        if (EveFile("Models - Legs/" + file) is not { } sb || !Directory.Exists(lava)) { output.WriteLine("not here"); return; }
        var catalog = BodySizeCatalog.Read(lava);
        var larue = catalog.For("_dwn", "0201").FirstOrDefault(o => o.FullLabel.Contains("LaRue", StringComparison.OrdinalIgnoreCase));
        if (larue == null)
        {
            output.WriteLine("no LaRue legs option; legs options are:");
            foreach (var o in catalog.For("_dwn", "0201")) output.WriteLine("   " + o.FullLabel);
            return;
        }
        output.WriteLine($"target: {larue.FullLabel}  ({catalog.PathOf(larue)})");

        byte[] tb = File.ReadAllBytes(catalog.PathOf(larue));
        var source = ModelPartReader.Read(sb)!;
        var target = ModelPartReader.Read(tb)!;
        var remap = Remap();
        string from = BodyCorrespondence.LayoutOf(source)!, to = BodyCorrespondence.LayoutOf(target)!;
        output.WriteLine($"layouts: {from} -> {to}");
        foreach (var part in source.Parts.Where(p => p.Island < 0).Select(p => p.Material).Distinct())
            output.WriteLine($"   source material {part} -> {SecondSkinWriter.SkinMaterialBodyType(part) ?? "not skin"}");

        bool ok = BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sb), target, BodyRetargetDiagTests.Uv(tb),
                                              "Legs", out var built, out string refusal, remap);
        output.WriteLine(ok ? built!.Describe() : "REFUSED — " + refusal);

        // The same off-sheet test the refit makes: the strict converter, judged per island.
        var convert = remap.UvConverter(from, to, unmirror: true, fold: true, reach: BodyCorrespondence.OnSheetReach)!;
        var uv = BodyCorrespondence.SameTile(BodyRetargetDiagTests.Uv(sb));
        var skin = source.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                         .SelectMany(p => p.Triangles).Distinct().ToList();
        var off = UvAtlasCorrespondence.OffSheet(source, uv, skin, convert);
        output.WriteLine($"off the sheet: {off.Count:N0} of {skin.Count:N0} skin vertices");
        if (off.Count == 0) return;

        System.Numerics.Vector3 P(int v) => new(source.Positions[v * 3], source.Positions[v * 3 + 1], source.Positions[v * 3 + 2]);
        var lo = off.Select(P).Aggregate(System.Numerics.Vector3.Min);
        var hi = off.Select(P).Aggregate(System.Numerics.Vector3.Max);
        output.WriteLine($"   they span x {lo.X:F3}..{hi.X:F3}  y {lo.Y:F3}..{hi.Y:F3}  z {lo.Z:F3}..{hi.Z:F3}");

        // Joined by position: an off-sheet vertex at the same place (0.01 mm) as a placed one.
        var placed = skin.Where(v => !off.Contains(v))
                         .Select(v => MeshMath.PositionKey(new SecondSkinWriter.Vec3(P(v).X, P(v).Y, P(v).Z), 100_000)).ToHashSet();
        int joined = off.Count(v => placed.Contains(MeshMath.PositionKey(new SecondSkinWriter.Vec3(P(v).X, P(v).Y, P(v).Z), 100_000)));
        output.WriteLine($"   {joined:N0} of them sit exactly on a placed vertex (a seam)");
        // And by triangle: an off-sheet vertex sharing a triangle with a placed one.
        int byTriangle = 0;
        var touching = new HashSet<int>();
        foreach (var part in source.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                int n = (off.Contains(a) ? 1 : 0) + (off.Contains(b) ? 1 : 0) + (off.Contains(c) ? 1 : 0);
                if (n is > 0 and < 3) { touching.Add(a); touching.Add(b); touching.Add(c); }
            }
        byTriangle = touching.Count(off.Contains);
        output.WriteLine($"   {byTriangle:N0} of them share a triangle with a placed one");

        if (!ok) return;
        // The island's displacement against the placed skin around it (within 3 cm of the island's box).
        var f = built!.Field;
        int unplaced = skin.Count(v => f[v] == null);
        output.WriteLine($"skin vertices with no displacement: {unplaced:N0}");
        var islandD = off.Where(v => f[v] != null).Select(v => f[v]!.Value).ToList();
        var ringD = skin.Where(v => !off.Contains(v) && f[v] != null)
                        .Where(v => P(v).X > lo.X - 0.03f && P(v).X < hi.X + 0.03f && P(v).Y > lo.Y - 0.03f &&
                                    P(v).Y < hi.Y + 0.03f && P(v).Z > lo.Z - 0.03f && P(v).Z < hi.Z + 0.03f)
                        .Select(v => f[v]!.Value).ToList();
        System.Numerics.Vector3 Mean(List<System.Numerics.Vector3> l) => l.Aggregate(System.Numerics.Vector3.Zero, (s, d) => s + d) / l.Count;
        output.WriteLine($"   island: {islandD.Count} moved, mean {Mean(islandD) * 1000} mm, " +
                         $"largest {islandD.Max(d => d.Length()) * 1000:F1} mm");
        output.WriteLine($"   skin around it: {ringD.Count} vertices, mean {Mean(ringD) * 1000} mm, " +
                         $"largest {ringD.Max(d => d.Length()) * 1000:F1} mm");

        // A picture: map coverage grey; source skin green (placed, < 50 mm), red (> 50 mm), yellow (not placed), and
        // each off-sheet vertex magenta. The Eve body's top drawn in blue to show what the sheet holds.
        {
            const int N = 1024;
            var px = new byte[N * N * 3];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                byte g = convert((x + 0.5f) / N, (y + 0.5f) / N, 1) != null ? (byte)90 : (byte)20;
                px[(y * N + x) * 3] = px[(y * N + x) * 3 + 1] = px[(y * N + x) * 3 + 2] = g;
            }
            void Dot(float u, float w, (byte, byte, byte) c)
            {
                int x = Math.Clamp((int)(u * N), 0, N - 1), y = Math.Clamp((int)(w * N), 0, N - 1);
                (px[(y * N + x) * 3], px[(y * N + x) * 3 + 1], px[(y * N + x) * 3 + 2]) = c;
            }
            if (EveFile("Models - Body/Body - Default.mdl") is { } tbytes)
            {
                var tparts = ModelPartReader.Read(tbytes)!;
                var tuv = BodyCorrespondence.SameTile(BodyRetargetDiagTests.Uv(tbytes));
                foreach (int v in tparts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                              .SelectMany(p => p.Triangles).Distinct())
                    Dot(tuv[v * 2], tuv[v * 2 + 1], (60, 110, 255));
            }
            foreach (int v in skin)
            {
                var c = off.Contains(v) ? ((byte)255, (byte)0, (byte)255)
                      : f[v] == null ? ((byte)255, (byte)230, (byte)0)
                      : f[v]!.Value.Length() > 0.05f ? ((byte)255, (byte)40, (byte)40)
                      : ((byte)60, (byte)220, (byte)60);
                Dot(uv[v * 2], uv[v * 2 + 1], c);
            }
            string png = Path.Combine(Path.GetTempPath(), "eve_" + Path.GetFileNameWithoutExtension(file).Replace(' ', '_') + ".png");
            using var s = File.Create(png);
            new StbImageWriteSharp.ImageWriter().WritePng(px, N, N, StbImageWriteSharp.ColorComponents.RedGreenBlue, s);
            output.WriteLine(png);
        }

        // Every big move and every unplaced vertex, by where it sits and its uv.
        foreach (var (label, pick) in new (string, Func<int, bool>)[]
                 {
                     ("moved over 50 mm", v => f[v] is { } d && d.Length() > 0.05f),
                     ("not placed", v => f[v] == null),
                 })
        {
            var hits = skin.Where(pick).ToList();
            output.WriteLine($"{label}: {hits.Count}");
            foreach (var g in hits.GroupBy(v => (X: MathF.Round(P(v).X * 20) / 20, Y: MathF.Round(P(v).Y * 20) / 20))
                                  .OrderByDescending(g => g.Count()).Take(8))
            {
                int v0 = g.First();
                output.WriteLine($"   {g.Count(),5} near x {g.Key.X:F2} y {g.Key.Y:F2}  e.g. uv ({uv[v0 * 2]:F3}, {uv[v0 * 2 + 1]:F3})" +
                                 (f[v0] is { } d0 ? $" moved {d0 * 1000} mm" : "") + (off.Contains(v0) ? " [converter: none]" : ""));
            }
        }
    }

    /// <summary>A named mod's first readable model for a slot.</summary>
    private (string Mod, BodyOption Option, string Path)? PickSlot(string startsWith, string slot)
    {
        if (!Directory.Exists(Mods)) return null;
        foreach (string dir in Directory.GetDirectories(Mods).OrderBy(d => d))
        {
            if (!Path.GetFileName(dir).StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)) continue;
            var catalog = BodySizeCatalog.Read(dir);
            foreach (var option in catalog.For(slot, "0201"))
            {
                string path = catalog.PathOf(option);
                if (!File.Exists(path)) continue;
                if (ModelPartReader.Read(File.ReadAllBytes(path)) is null) continue;
                return (Path.GetFileName(dir), option, path);
            }
        }
        return null;
    }

    /// <summary>The first readable chest size of the first installed body whose models are drawn in that layout.</summary>
    private (string Mod, BodyOption Option, string Path)? Pick(string layout)
    {
        // No mods on this machine (CI has no E: drive): nothing to pick, and the callers skip.
        if (!Directory.Exists(Mods)) return null;
        foreach (string dir in Directory.GetDirectories(Mods).OrderBy(d => d))
        {
            BodySizeCatalog catalog;
            try { catalog = BodySizeCatalog.Read(dir); } catch { continue; }
            if (!catalog.IsBody) continue;

            foreach (var option in catalog.For("_top", "0201"))
            {
                string path = catalog.PathOf(option);
                if (!File.Exists(path)) continue;
                if (ModelPartReader.Read(File.ReadAllBytes(path)) is not { } parts) continue;
                if (!string.Equals(BodyCorrespondence.LayoutOf(parts), layout, StringComparison.OrdinalIgnoreCase))
                    break;      // this mod is a different layout; try the next mod
                return (Path.GetFileName(dir), option, path);
            }
        }
        return null;
    }
}
