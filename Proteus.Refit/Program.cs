using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CheapLoc;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Refit;

/// <summary>
/// Refit a garment .mdl from one body option to others, outside the game — the Studio "Body size" tool's refit, run
/// headless so a mod packer can generate the sizes an author did not model.
/// <para/>
/// Bodies are named by their FILE, relative to the body mod's root (<c>DEFAULT CHEST - SmallClothes/NSFW XS.mdl</c>),
/// never by option name: Neolithe has eight options called "SFW M".
/// <para/>
/// The source body may be in another body mod (<c>--from-root</c>): Neolithe to Rue+, say. That is the Studio's
/// "across bodies" refit — the weights are rewritten for the new body's rig — and where the two bodies' textures are
/// laid out differently the correspondence goes through Proteus's layout maps (<c>--uvmaps</c>).
/// <para/>
/// The legs pair: a top's hem hangs over the hips, and refitting the chest alone left the cloth six times further from
/// the author's own sizes. The legs the hem was made on are detected in the source body mod, then either moved along
/// that family's size axis by the size word at the end of the file name (<c>--legs</c>, within one body mod) or sent
/// to one legs option of the target mod for every size (<c>--legs-to</c>).
/// <para/>
/// Writes <c>&lt;out-dir&gt;/&lt;label&gt;.mdl</c> for each target and one JSON report on stdout. A refused size is in the
/// report with its reason and no file; the exit code is non-zero only when nothing could run at all.
/// </summary>
internal static class Program
{
    private const string Usage =
        """
        Proteus.Refit --garment <xs.mdl> --body-root <body mod> --from <rel> --to <rel>=<label>[,...] --out-dir <dir>
                      [--from-root <body mod the garment was made on>] [--uvmaps <Proteus plugin dir>]
                      [--slot _top] [--race 0201] [--legs-from <rel>]
                      [--legs <label>=<size word>,... | --legs-to <rel in the target mod>]
        Proteus.Refit --list --body-root <body mod> [--slot _top]
        """;

    private static int Main(string[] args)
    {
        // Loc.Localize returns "#key" for an assembly that was never set up, and refusals are worded through it.
        Loc.SetupWithFallbacks(typeof(Proteus.Plugin).Assembly);

        try
        {
            var opts = Parse(args);
            if (opts.ContainsKey("list")) return List(opts);
            return Run(opts);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static int List(Dictionary<string, string> opts)
    {
        var catalog = BodySizeCatalog.Read(Required(opts, "body-root"));
        var slots = opts.TryGetValue("slot", out var s) ? [s] : catalog.Slots.ToArray();
        foreach (string slot in slots)
            foreach (var option in catalog.For(slot))
                Console.WriteLine($"{slot}\t{option.Rel}\t{option.FullLabel}");
        return 0;
    }

    private static int Run(Dictionary<string, string> opts)
    {
        string garmentPath = Required(opts, "garment");
        string outDir = Required(opts, "out-dir");
        string slot = opts.GetValueOrDefault("slot", "_top");
        string? race = opts.GetValueOrDefault("race", "0201");
        bool male = race != null && BodySizeCatalog.IsMaleRace(race);

        string targetRoot = Required(opts, "body-root");
        string sourceRoot = opts.GetValueOrDefault("from-root", targetRoot);
        bool acrossBodies = !string.Equals(Path.GetFullPath(sourceRoot).TrimEnd('\\'),
                                           Path.GetFullPath(targetRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var dst = BodySizeCatalog.Read(targetRoot);
        var src = acrossBodies ? BodySizeCatalog.Read(sourceRoot) : dst;
        var uvRemap = opts.TryGetValue("uvmaps", out var pluginDir) ? new UVRemapService(NullLog(), pluginDir) : null;

        var from = Find(src, slot, Required(opts, "from"));
        var targets = Required(opts, "to").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Select(t => Pair(t, "--to"))
                                          .Select(t => (Label: t.Value, Option: Find(dst, slot, t.Key)))
                                          .ToList();

        var garmentBytes = File.ReadAllBytes(garmentPath);
        var garment = ModelPartReader.Read(garmentBytes)
                   ?? throw new UsageException($"{garmentPath} could not be read as a model.");
        Directory.CreateDirectory(outDir);

        ushort? sourceMask = MaskOf(src, slot), targetMask = MaskOf(dst, slot);

        string? legsSkipped = null;
        Legs? legs = null;
        if (slot != "_dwn" && (opts.ContainsKey("legs") || opts.ContainsKey("legs-to")))
        {
            if (acrossBodies && opts.ContainsKey("legs") && !opts.ContainsKey("legs-to"))
                throw new UsageException("--legs moves along one body mod's size words; across body mods give --legs-to.");
            var pinned = opts.TryGetValue("legs-from", out var legsFrom) ? Find(src, "_dwn", legsFrom) : null;
            var fixedTo = opts.TryGetValue("legs-to", out var legsTo) ? Find(dst, "_dwn", legsTo) : null;
            legs = Legs.Detect(src, dst, garment, garmentBytes, race, opts.GetValueOrDefault("legs"), fixedTo,
                               targets.Select(t => t.Label).ToList(), pinned, out legsSkipped);
        }

        var sizes = new List<object>();
        foreach (var (label, option) in targets)
        {
            var pairs = new List<BodyRetarget.SlotPair>();
            if (BodyRetarget.BuildPair(slot, src.PathOf(from), dst.PathOf(option), slot, male, sourceMask, targetMask,
                                       uvRemap, out var pair) is { } refusal)
            {
                sizes.Add(new { label, to = option.Rel, refusal });
                continue;
            }
            pairs.Add(pair);

            // The hips follow along; a size whose legs are the source's own needs none.
            string? legsTo = legs?.TargetFor(label);
            string? legsNote = legs == null ? null : legsTo == null ? legs.NoteFor(label) : null;
            if (legs != null && legsTo != null)
            {
                if (BodyRetarget.BuildPair("_dwn", legs.SourcePath, legsTo, "_dwn", male, legs.SourceMask,
                                           legs.TargetMask, uvRemap, out var legsPair) is { } legsRefusal)
                    legsNote = legsRefusal;
                else
                    pairs.Add(legsPair);
            }

            var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, slot, replaceSkin: true,
                                            acrossBodies: acrossBodies, cutHidden: true);
            string written = Path.Combine(outDir, label + ".mdl");
            File.WriteAllBytes(written, planned.Model);

            var r = planned.Report;
            sizes.Add(new
            {
                label,
                to = option.Rel,
                legsTo = legsTo == null ? null : Path.GetRelativePath(dst.ModRoot, legsTo),
                legsNote,
                written,
                report = new
                {
                    nodes = r.Nodes, snapped = r.Snapped, transferred = r.Transferred, missed = r.Missed,
                    pushed = r.Pushed, folded = r.Folded, laid = r.Laid,
                    worstMoveMm = r.WorstMove * 1000f, worstPushMm = r.WorstPush * 1000f,
                },
            });
        }

        var result = new
        {
            garment = garmentPath,
            from = from.Rel,
            acrossBodies,
            legs = legs == null
                ? (object?)(legsSkipped == null ? null : new { skipped = legsSkipped })
                : new { from = legs.SourceRel, confidence = legs.Confidence, note = legs.Note },
            sizes,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>A body mod's own IMC mask for a slot, under its default settings — no player here to ask.</summary>
    private static ushort? MaskOf(BodySizeCatalog catalog, string slot)
        => BodyRetarget.ImcSlotName(slot) is { } equip ? ImcEntrySource.MaskFor(catalog.ModRoot, 0, equip, null) : null;

    /// <summary>The option whose model is <paramref name="rel"/> — slashes and case as Penumbra treats them.</summary>
    private static BodyOption Find(BodySizeCatalog catalog, string slot, string rel)
    {
        string want = Normal(rel);
        return catalog.For(slot).FirstOrDefault(o => Normal(o.Rel) == want)
            ?? throw new UsageException($"No {slot} body option in {catalog.ModRoot} uses \"{rel}\". Run --list to see them.");
    }

    internal static string Normal(string rel) => rel.Replace('\\', '/').Trim('/').ToLowerInvariant();

    private static KeyValuePair<string, string> Pair(string text, string what)
    {
        int eq = text.LastIndexOf('=');
        if (eq <= 0 || eq == text.Length - 1) throw new UsageException($"{what} wants <value>=<label>, got \"{text}\".");
        return new(text[..eq].Trim(), text[(eq + 1)..].Trim());
    }

    private static string Required(Dictionary<string, string> opts, string key)
        => opts.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new UsageException($"--{key} is required.");

    private static Dictionary<string, string> Parse(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"Unexpected argument \"{args[i]}\".");
            string key = args[i][2..];
            opts[key] = key == "list" ? "" : i + 1 < args.Length ? args[++i] : throw new UsageException($"--{key} needs a value.");
        }
        return opts;
    }

    /// <summary>
    /// A log that says nothing, for <see cref="UVRemapService"/>, which only wants somewhere to write. Every member
    /// returns its type's default.
    /// </summary>
    private static IPluginLog NullLog() => DispatchProxy.Create<IPluginLog, SilentLog>();

    /// <summary>
    /// Where the garment's hem sits on the legs, and which legs each size moves it to.
    /// <para/>
    /// Detected the way the Studio panel does (<see cref="BodySizeMatch.Rank"/>), which for a top reads the hips from the
    /// cloth within the author's first-listed legs family. Then, within one body mod, moved along that family by the
    /// size word ending its file name (<c>GEN A Small.mdl</c> for S becomes <c>GEN A Large.mdl</c> for L); or, into
    /// another body mod, sent to the one legs option given for every size — Rue+ files every size as
    /// <c>c0201e0000_dwn.mdl</c> in a folder of its own, and its hips do not change with the chest size anyway.
    /// </summary>
    private sealed class Legs
    {
        public required string SourceRel { get; init; }
        public required string SourcePath { get; init; }
        public required string Confidence { get; init; }
        public string? Note { get; init; }
        public ushort? SourceMask { get; init; }
        public ushort? TargetMask { get; init; }
        public required Dictionary<string, string?> Targets { get; init; }
        public required Dictionary<string, string> Notes { get; init; }

        public string? TargetFor(string label) => Targets.GetValueOrDefault(label);
        public string? NoteFor(string label) => Notes.GetValueOrDefault(label);

        /// <param name="src">The body mod the garment was made on — where its legs are detected.</param>
        /// <param name="dst">The body mod the sizes are for.</param>
        /// <param name="map"><c>S=Small,M=Medium,L=Large</c>: the legs size word each garment size wants, within
        /// <paramref name="src"/>. Null with <paramref name="fixedTo"/>.</param>
        /// <param name="fixedTo">One legs option of <paramref name="dst"/> every size moves the hem to.</param>
        /// <param name="pinned">The legs the garment was made on, when the caller knows: detection is skipped. Worth
        /// giving — within one mesh the cloth reading favours whichever legs fill the hem most, so a top made on the
        /// plain legs can read as Neobelly.</param>
        /// <param name="why">When null is returned, why the hips are not refitted.</param>
        public static Legs? Detect(BodySizeCatalog src, BodySizeCatalog dst, ModelParts garment, byte[] garmentBytes,
                                   string? race, string? map, BodyOption? fixedTo, IReadOnlyList<string> labels,
                                   BodyOption? pinned, out string? why)
        {
            why = null;
            var options = src.For("_dwn", race);
            if (options.Count == 0)
            {
                why = "the body mod the garment was made on has no legs options";
                return null;
            }

            BodyOption source;
            string confidence;
            string? note = null;
            if (pinned != null)
            {
                source = pinned;
                confidence = "Given";
            }
            else
            {
                var bones = new HashSet<string>(SecondSkinWriter.Parse(garmentBytes).BoneNames, StringComparer.Ordinal);
                var ranking = BodySizeMatch.Rank(garment, options, src.PathOf, bones);
                if (ranking.Best is not { } best
                    || ranking.Confidence is BodySizeMatch.Confidence.NoBodyMesh or BodySizeMatch.Confidence.TooLittle
                                          or BodySizeMatch.Confidence.Ambiguous)
                {
                    why = $"could not tell which legs the garment was made on ({ranking.Confidence})";
                    return null;
                }
                source = best.Option;
                confidence = ranking.Confidence.ToString();
                if (ranking.FromCloth) note = "read from how the cloth sits on the hips";
            }

            string sourcePath = src.PathOf(source);
            var targets = new Dictionary<string, string?>(StringComparer.Ordinal);
            var notes = new Dictionary<string, string>(StringComparer.Ordinal);

            if (fixedTo != null)
            {
                string to = dst.PathOf(fixedTo);
                foreach (string label in labels)
                {
                    if (string.Equals(Path.GetFullPath(to), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                        notes[label] = $"the hem already sits on \"{fixedTo.Label}\"";
                    else
                        targets[label] = to;
                }
            }
            else
            {
                var words = (map ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(t => Pair(t, "--legs"))
                                       .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                string stem = Path.GetFileNameWithoutExtension(sourcePath);
                string? word = words.Values.Distinct()
                                    .FirstOrDefault(w => stem.EndsWith(" " + w, StringComparison.OrdinalIgnoreCase));
                foreach (var (label, want) in words)
                {
                    if (word == null)
                    {
                        notes[label] = $"the detected legs \"{stem}\" do not end in a size word, so the hips were not refitted";
                        continue;
                    }
                    if (string.Equals(want, word, StringComparison.OrdinalIgnoreCase))
                    {
                        notes[label] = $"the hem already sits on \"{stem}\"";
                        continue;
                    }
                    string file = Path.Combine(Path.GetDirectoryName(sourcePath)!,
                                               stem[..^word.Length] + want + Path.GetExtension(sourcePath));
                    string? match = options.Select(src.PathOf)
                                           .FirstOrDefault(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(file),
                                                                              StringComparison.OrdinalIgnoreCase));
                    if (match == null) notes[label] = $"no legs option \"{Path.GetFileName(file)}\" beside \"{stem}\"";
                    else targets[label] = match;
                }
            }

            return new Legs
            {
                SourceRel = source.Rel,
                SourcePath = sourcePath,
                Confidence = confidence,
                Note = note,
                SourceMask = MaskOf(src, "_dwn"),
                TargetMask = MaskOf(dst, "_dwn"),
                Targets = targets,
                Notes = notes,
            };
        }
    }

    private sealed class UsageException(string message) : Exception(message);
}

/// <summary>The body of <c>Program.NullLog</c>. Public and unsealed, as <see cref="DispatchProxy"/> requires.</summary>
public class SilentLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method?.ReturnType is { IsValueType: true } t && t != typeof(void) ? Activator.CreateInstance(t) : null;
}
