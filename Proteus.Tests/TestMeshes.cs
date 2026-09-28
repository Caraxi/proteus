using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;

namespace Proteus.Tests;

/// <summary>Model helpers shared by tests.</summary>
internal static class TestMeshes
{
    /// <summary>uv0 in <see cref="ModelPartReader"/>'s vertex order, which is what a body correspondence needs.</summary>
    internal static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null)
            ? uv
            : [];

    /// <summary>Every authored toe cap the plugin ships, paired with its binding — the same rule as the service. The
    /// build copies the plugin's Meshes folder beside the test assembly.</summary>
    internal static List<SecondSkinWriter.AuthoredCapSet> CapSets()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Meshes");
        if (!Directory.Exists(dir)) return [];
        var found = new List<SecondSkinWriter.AuthoredCapSet>();
        foreach (var mp in Directory.GetFiles(dir, "toecap*.mdl").OrderBy(x => x))
        {
            var bind = Path.ChangeExtension(mp, ".bind");
            found.Add(new SecondSkinWriter.AuthoredCapSet(
                File.ReadAllBytes(mp),
                File.Exists(bind) ? File.ReadAllBytes(bind) : null,
                Path.GetFileNameWithoutExtension(mp)));
        }
        return found;
    }
}
