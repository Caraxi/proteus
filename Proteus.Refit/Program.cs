using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CheapLoc;
using Proteus.Services;

namespace Proteus.Refit;

/// <summary>
/// Refit a garment .mdl from one body option to others, outside the game — the Studio "Body size" tool's refit, run
/// headless so a mod packer can generate the sizes an author did not model.
/// <para/>
/// Bodies are named by their FILE, relative to the body mod's root (<c>DEFAULT CHEST - SmallClothes/NSFW XS.mdl</c>),
/// never by option name: Neolithe has eight options called "SFW M".
/// <para/>
/// The legs pair: a top's hem hangs over the hips, and refitting the chest alone left the cloth six times further from
/// the author's own sizes. So <c>--legs</c> detects the legs option the hem was made on and moves it along the same
/// family's size axis, by replacing the size word at the end of its file name.
/// <para/>
/// Writes <c>&lt;out-dir&gt;/&lt;label&gt;.mdl</c> for each target and one JSON report on stdout. A refused size is in the
/// report with its reason and no file; the exit code is non-zero only when nothing could run at all.
/// </summary>
internal static class Program
{
    private const string Usage =
        """
        Proteus.Refit --garment <xs.mdl> --body-root <body mod> --from <rel> --to <rel>=<label>[,...] --out-dir <dir>
                      [--slot _top] [--race 0201] [--legs <label>=<size word>,...] [--legs-from <rel>]
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

        var catalog = BodySizeCatalog.Read(Required(opts, "body-root"));
        var from = Find(catalog, slot, Required(opts, "from"));
        var targets = Required(opts, "to").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Select(t => Pair(t, "--to"))
                                          .Select(t => (Label: t.Value, Option: Find(catalog, slot, t.Key)))
                                          .ToList();

        var garmentBytes = File.ReadAllBytes(garmentPath);
        var garment = ModelPartReader.Read(garmentBytes)
                   ?? throw new UsageException($"{garmentPath} could not be read as a model.");
        Directory.CreateDirectory(outDir);

        ushort? mask = BodyRetarget.ImcSlotName(slot) is { } equip
            ? ImcEntrySource.MaskFor(catalog.ModRoot, 0, equip, null)
            : null;

        string? legsSkipped = null;
        var legs = opts.TryGetValue("legs", out var legsArg) && slot != "_dwn"
            ? Legs.Detect(catalog, garment, garmentBytes, race, legsArg,
                          opts.TryGetValue("legs-from", out var legsFrom) ? Find(catalog, "_dwn", legsFrom) : null,
                          out legsSkipped)
            : null;

        var sizes = new List<object>();
        foreach (var (label, option) in targets)
        {
            var pairs = new List<BodyRetarget.SlotPair>();
            if (BodyRetarget.BuildPair(slot, catalog.PathOf(from), catalog.PathOf(option), slot, male, mask, mask, null,
                                       out var pair) is { } refusal)
            {
                sizes.Add(new { label, to = option.Rel, refusal });
                continue;
            }
            pairs.Add(pair);

            // The hips follow along the legs family's own size axis; a size whose legs are the source's own needs none.
            string? legsTo = legs?.TargetFor(label);
            string? legsNote = legs == null ? null : legsTo == null ? legs.NoteFor(label) : null;
            if (legs != null && legsTo != null)
            {
                if (BodyRetarget.BuildPair("_dwn", legs.SourcePath, legsTo, "_dwn", male, legs.Mask, legs.Mask, null,
                                           out var legsPair) is { } legsRefusal)
                    legsNote = legsRefusal;
                else
                    pairs.Add(legsPair);
            }

            var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, slot, replaceSkin: true, cutHidden: true);
            string written = Path.Combine(outDir, label + ".mdl");
            File.WriteAllBytes(written, planned.Model);

            var r = planned.Report;
            sizes.Add(new
            {
                label,
                to = option.Rel,
                legsTo = legsTo == null ? null : Path.GetRelativePath(catalog.ModRoot, legsTo),
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
            legs = legs == null
                ? (object?)(legsSkipped == null ? null : new { skipped = legsSkipped })
                : new { from = legs.SourceRel, confidence = legs.Confidence, note = legs.Note },
            sizes,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>The option whose model is <paramref name="rel"/> — slashes and case as Penumbra treats them.</summary>
    private static BodyOption Find(BodySizeCatalog catalog, string slot, string rel)
    {
        string want = Normal(rel);
        return catalog.For(slot).FirstOrDefault(o => Normal(o.Rel) == want)
            ?? throw new UsageException($"No {slot} body option uses \"{rel}\". Run --list to see them.");
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
    /// Where the garment's hem sits on the legs, and which legs each size moves it to.
    /// <para/>
    /// Detected the way the Studio panel does (<see cref="BodySizeMatch.Rank"/>), which for a top reads the hips from the
    /// cloth within the author's first-listed legs family. The size is then moved within that family by its file name:
    /// <c>GEN A Small.mdl</c> for S becomes <c>GEN A Large.mdl</c> for L.
    /// </summary>
    private sealed class Legs
    {
        public required string SourceRel { get; init; }
        public required string SourcePath { get; init; }
        public required string Confidence { get; init; }
        public string? Note { get; init; }
        public ushort? Mask { get; init; }
        public required Dictionary<string, string?> Targets { get; init; }
        public required Dictionary<string, string> Notes { get; init; }

        public string? TargetFor(string label) => Targets.GetValueOrDefault(label);
        public string? NoteFor(string label) => Notes.GetValueOrDefault(label);

        /// <param name="map"><c>S=Small,M=Medium,L=Large</c>: the legs size word each garment size wants.</param>
        /// <param name="pinned">The legs the garment was made on, when the caller knows: detection is skipped. Worth
        /// giving — within one mesh the cloth reading favours whichever legs fill the hem most, so a top made on the
        /// plain legs can read as Neobelly.</param>
        /// <param name="why">When null is returned, why the hips are not refitted.</param>
        public static Legs? Detect(BodySizeCatalog catalog, ModelParts garment, byte[] garmentBytes, string? race,
                                   string map, BodyOption? pinned, out string? why)
        {
            why = null;
            var words = map.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(t => Pair(t, "--legs"))
                           .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var options = catalog.For("_dwn", race);
            if (options.Count == 0)
            {
                why = "the body mod has no legs options";
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
                var ranking = BodySizeMatch.Rank(garment, options, catalog.PathOf, bones);
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

            string sourcePath = catalog.PathOf(source);
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            string? word = words.Values.Distinct().FirstOrDefault(w => stem.EndsWith(" " + w, StringComparison.OrdinalIgnoreCase));

            var targets = new Dictionary<string, string?>(StringComparer.Ordinal);
            var notes = new Dictionary<string, string>(StringComparer.Ordinal);
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
                string? match = options.Select(catalog.PathOf)
                                       .FirstOrDefault(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(file),
                                                                          StringComparison.OrdinalIgnoreCase));
                if (match == null) notes[label] = $"no legs option \"{Path.GetFileName(file)}\" beside \"{stem}\"";
                else targets[label] = match;
            }

            return new Legs
            {
                SourceRel = source.Rel,
                SourcePath = sourcePath,
                Confidence = confidence,
                Note = note,
                Mask = BodyRetarget.ImcSlotName("_dwn") is { } equip
                    ? ImcEntrySource.MaskFor(catalog.ModRoot, 0, equip, null)
                    : null,
                Targets = targets,
                Notes = notes,
            };
        }
    }

    private sealed class UsageException(string message) : Exception(message);
}
