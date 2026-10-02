using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// Carry a body's suppression tags onto a garment's own skin, triangle by triangle, where the skin swap cannot: a
/// full-body dress draws the whole body in one skin mesh — torso and legs — which is on neither slot's body alone, so
/// the swap keeps it whole and untagged, and boots then draw the calves through their own shafts.
/// <para/>
/// Each skin triangle takes the tags of the body triangle nearest its middle; the submesh is regrouped so every tag
/// set is one run, and each run is tagged. Nothing moves and nothing is added — only the triangle order within the
/// submesh and the masks change.
/// </summary>
internal static class SkinTagTransfer
{
    /// <summary>How far a garment skin triangle's middle may be from the body's nearest one and still take its tags
    /// (3 cm): the skin was made on this body family, so it sits on it; beyond that it is skin the body has no tag for.</summary>
    internal const float Reach = 0.03f;

    private const float Cell = 0.02f;

    /// <param name="Tagged">Triangles given at least one tag, per tag name.</param>
    internal readonly record struct Report(IReadOnlyDictionary<string, int> Tagged, int SkinTriangles);

    /// <summary>Tag the garment's LOD0 skin with <paramref name="tags"/>, as <paramref name="body"/> places them.</summary>
    /// <returns>The edited model, or the input unchanged when no triangle took a tag.</returns>
    internal static byte[] Transfer(byte[] garment, byte[] body, IReadOnlyCollection<string> tags, out Report report)
    {
        var tagged = tags.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        report = new Report(tagged, 0);
        if (ModelPartReader.Read(body) is not { } b || ModelPartReader.Read(garment) is not { } g) return garment;

        // The body's skin triangles by their middle, each with the subset of `tags` its submesh carries.
        var grid = new Dictionary<(int, int, int), List<(Vector3 At, int Set)>>();
        var sets = new List<string[]> { Array.Empty<string>() };
        foreach (var part in b.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
        {
            var names = tags.Where(t => HasAttribute(part.AttributeMask, b.AttributeNames, t)).OrderBy(t => t).ToArray();
            int set = sets.FindIndex(s => s.SequenceEqual(names));
            if (set < 0) { set = sets.Count; sets.Add(names); }
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var mid = Middle(b, part.Triangles, t);
                var key = CellOf(mid);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add((mid, set));
            }
        }

        // The garment's skin submeshes, labelled. Read once: a regroup reorders triangles within one submesh and adds
        // submeshes after it in the same mesh, so each mesh is done back to front — its earlier submeshes keep their
        // indices — and every split is tagged at once, before a later one can move it along.
        var skin = g.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                          .OrderByDescending(p => p.Mesh).ThenByDescending(p => p.Submesh).ToList();
        int skinTris = 0;
        var model = garment;
        foreach (var part in skin)
        {
            var labelOf = new Dictionary<int, int>();   // submesh ordinal → tag set
            for (int k = 0; k * 3 + 2 < part.Triangles.Length; k++)
            {
                int set = Nearest(grid, Middle(g, part.Triangles, k * 3));
                // Already carrying a tag: leave it to what the author or the swap gave it.
                if (sets[set].Any(t => HasAttribute(part.AttributeMask, g.AttributeNames, t))) set = 0;
                labelOf[part.Ordinals[k]] = set;
            }
            skinTris += labelOf.Count;
            if (labelOf.Values.All(s => s == 0)) continue;

            var (edited, byGroup) = ModelAttributeWriter.RegroupSubmesh(model, part.Mesh, part.Submesh,
                                                                         t => labelOf.GetValueOrDefault(t));
            model = edited;
            foreach (var (set, submeshes) in byGroup)
            {
                if (set == 0) continue;
                int count = labelOf.Values.Count(s => s == set);
                foreach (var name in sets[set])
                {
                    tagged[name] += count;
                    model = ModelAttributeWriter.AddAttribute(model, name, submeshes.Select(s => (part.Mesh, s)).ToList());
                }
            }
        }

        report = new Report(tagged, skinTris);
        return model;
    }

    private static bool HasAttribute(uint mask, IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count && i < 32; i++)
            if ((mask & (1u << i)) != 0 && string.Equals(names[i], name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static int Nearest(Dictionary<(int, int, int), List<(Vector3 At, int Set)>> grid, Vector3 p)
    {
        var (cx, cy, cz) = CellOf(p);
        int reach = (int)MathF.Ceiling(Reach / Cell);
        float best = Reach * Reach;
        int set = 0;
        for (int x = cx - reach; x <= cx + reach; x++)
            for (int y = cy - reach; y <= cy + reach; y++)
                for (int z = cz - reach; z <= cz + reach; z++)
                    if (grid.TryGetValue((x, y, z), out var list))
                        foreach (var (at, s) in list)
                        {
                            float d = Vector3.DistanceSquared(at, p);
                            if (d < best) { best = d; set = s; }
                        }
        return set;
    }

    private static (int, int, int) CellOf(Vector3 p)
        => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

    private static Vector3 Middle(ModelParts m, int[] tris, int t)
        => (At(m, tris[t]) + At(m, tris[t + 1]) + At(m, tris[t + 2])) / 3f;

    private static Vector3 At(ModelParts m, int v)
        => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
}
