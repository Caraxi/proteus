using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// Which body slots the game is NOT drawing because the worn gear's EQP entry hides them: a top that ships its own
/// legs clears BodyShowLeg, and the pants model still loads (the live model walk lists it) but never renders. A
/// second skin cut from that hidden model doubles the shell over the top's own legs.
/// <para/>
/// Mirrors Penumbra's EqpCache.GetValues: the body item decides legs and hands; the legs entry (the BODY item's own
/// when legs are hidden) decides feet. Each entry is Penumbra's override when the collection has one, otherwise the
/// vanilla equipmentparameter.eqp table.
/// </summary>
internal static class EqpVisibility
{
    internal const string VanillaEqpPath = "chara/xls/equipmentparameter/equipmentparameter.eqp";

    private const ulong BodyShowLeg  = 0x0100;
    private const ulong BodyShowHand = 0x0200;
    private const ulong LegsShowFoot = 0x20ul << 16;

    private const ulong BodyMask = 0xFFFF;
    private const ulong LegsMask = 0xFFul << 16;

    /// <summary>Penumbra.GameData's Eqp.DefaultEntry: what a set in a collapsed block of the table reads as.</summary>
    private const ulong DefaultEntry = 0x3fe00070603f00;

    // Penumbra.GameData EquipSlot values.
    internal const byte SlotBody = 4;
    internal const byte SlotLegs = 7;

    private const int BlockSize = 160;
    private const int NumBlocks = 64;

    /// <summary>
    /// The slots ("dwn", "glv", "sho") the worn body and legs items hide. <paramref name="entry"/> answers one set's
    /// entry for one slot, already masked to it; <paramref name="topSet"/>/<paramref name="dwnSet"/> are 0 for an empty slot.
    /// </summary>
    internal static HashSet<string> HiddenParts(int topSet, int dwnSet, Func<int, byte, ulong> entry)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var body = entry(topSet, SlotBody);
        bool legsShown = (body & BodyShowLeg) != 0;
        if (!legsShown) hidden.Add("dwn");
        if ((body & BodyShowHand) == 0) hidden.Add("glv");
        var legs = entry(legsShown ? dwnSet : topSet, SlotLegs);
        if ((legs & LegsShowFoot) == 0) hidden.Add("sho");
        return hidden;
    }

    /// <summary>
    /// One set's entry for one slot: Penumbra's override when there is one, else the vanilla table. Null when the vanilla
    /// table is unreadable and no override covers it — the caller must then assume the slot is drawn.
    /// </summary>
    internal static ulong? Entry(int set, byte slot, IReadOnlyDictionary<(int Set, byte Slot), ulong>? overrides, byte[]? vanilla)
    {
        if (overrides != null && overrides.TryGetValue((set, slot), out var over)) return over & SlotMask(slot);
        if (vanilla == null || VanillaEntry(vanilla, set) is not { } v) return null;
        return v & SlotMask(slot);
    }

    private static ulong SlotMask(byte slot) => slot switch
    {
        SlotBody => BodyMask,
        SlotLegs => LegsMask,
        _        => 0,
    };

    /// <summary>
    /// A set's full entry in the vanilla table: a 64-bit control word saying which 160-entry blocks are present, then the
    /// present blocks packed (the control word doubles as entry 0, so set 0 reads as set 1). Null on a short file.
    /// </summary>
    internal static ulong? VanillaEntry(byte[] file, int set)
    {
        if (file.Length < 16) return null;
        if (set <= 0) set = 1;
        int block = set / BlockSize;
        if (block >= NumBlocks) return DefaultEntry;
        ulong control = BinaryPrimitives.ReadUInt64LittleEndian(file);
        ulong bit = 1ul << block;
        if ((control & bit) == 0) return DefaultEntry;
        int present = System.Numerics.BitOperations.PopCount(control & (bit - 1));
        long offset = ((long)present * BlockSize + set % BlockSize) * 8;
        if (offset + 8 > file.Length) return null;
        return BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan((int)offset));
    }

    /// <summary>
    /// The EQP overrides in Penumbra's GetPlayerMetaManipulations blob, keyed (set, EquipSlot), or null when the blob is
    /// empty-handed or in a form this does not read. Versions 1 (fixed section order: IMC, then EQP) and 2 (labelled
    /// sections) are binary; IMC is the only section ahead of EQP in either, so only its record size matters here.
    /// </summary>
    internal static Dictionary<(int Set, byte Slot), ulong>? ParsePenumbraEqp(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        byte[] data;
        try
        {
            using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(blob)), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (Exception) { return null; }
        return ParseDecompressed(data);
    }

    // ImcIdentifier (PrimaryId u16, Variant u8, ObjectType u8, SecondaryId u16, EquipSlot u8, BodySlot u8) + ImcEntry (6 bytes).
    private const int ImcRecord = 8 + 6;
    // EqpIdentifier (PrimaryId u16, EquipSlot u8, padded to 4) + EqpEntry (u64).
    private const int EqpRecord = 4 + 8;

    private const uint ImcKey = ((uint)'I' << 24) | ((uint)'M' << 16) | ((uint)'C' << 8);
    private const uint EqpKey = ((uint)'E' << 24) | ((uint)'Q' << 16) | ((uint)'P' << 8);

    internal static Dictionary<(int Set, byte Slot), ulong>? ParseDecompressed(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9) return null;
        byte version = data[0];
        var magic = Encoding.ASCII.GetString(data.Slice(1, 8));
        var r = data[9..];
        switch (version)
        {
            case 1 when magic == "META0001":
            {
                if (!TryCount(ref r, out var imc) || !Skip(ref r, (long)imc * ImcRecord)) return null;
                if (!TryCount(ref r, out var eqp)) return null;
                return ReadEqp(r, eqp);
            }
            case 2 when magic == "META0002":
            {
                // Labelled sections in a fixed order; IMC is the only one that can precede EQP.
                while (r.Length >= 8)
                {
                    uint label = BinaryPrimitives.ReadUInt32LittleEndian(r);
                    int count = BinaryPrimitives.ReadInt32LittleEndian(r[4..]);
                    r = r[8..];
                    if (count < 0) return null;
                    if (label == EqpKey) return ReadEqp(r, count);
                    // Any other section sits past where EQP would be: the collection overrides none.
                    if (label != ImcKey) return new();
                    if (!Skip(ref r, (long)count * ImcRecord)) return null;
                }
                return new();
            }
            default:
                return null;
        }
    }

    private static Dictionary<(int Set, byte Slot), ulong>? ReadEqp(ReadOnlySpan<byte> r, int count)
    {
        if ((long)count * EqpRecord > r.Length) return null;
        var result = new Dictionary<(int Set, byte Slot), ulong>(count);
        for (int i = 0; i < count; i++)
        {
            var rec = r.Slice(i * EqpRecord, EqpRecord);
            int set = BinaryPrimitives.ReadUInt16LittleEndian(rec);
            byte slot = rec[2];
            // A slot outside EQP's five means the layout moved under us; trust none of it.
            if (slot is not (3 or SlotBody or 5 or SlotLegs or 8)) return null;
            result[(set, slot)] = BinaryPrimitives.ReadUInt64LittleEndian(rec[4..]);
        }
        return result;
    }

    private static bool TryCount(ref ReadOnlySpan<byte> r, out int count)
    {
        count = 0;
        if (r.Length < 4) return false;
        count = BinaryPrimitives.ReadInt32LittleEndian(r);
        r = r[4..];
        return count >= 0;
    }

    private static bool Skip(ref ReadOnlySpan<byte> r, long bytes)
    {
        if (bytes > r.Length) return false;
        r = r[(int)bytes..];
        return true;
    }
}
