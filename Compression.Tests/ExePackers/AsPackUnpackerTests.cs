using System.Buffers.Binary;
using System.Text;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using NUnit.Framework;

namespace Compression.Tests.ExePackers;

/// <summary>
/// End-to-end tests for the ASPack handler. There is no ASPack compressor to
/// round-trip against, so these build the packed side by hand: a minimal PE whose
/// stub carries a region table plus the byte anchors the handler reads its
/// configuration from, and whose packed section holds a stream written by
/// <see cref="AsPackStreamWriter"/> below.
/// </summary>
/// <remarks>
/// The writer is an independent statement of the format — canonical codes over a
/// 24-bit space, the pre-tree-coded block header, and the main/length/aligned
/// alphabets — so it pins the decoder against regressions. It is not an oracle for
/// the format itself; that is the packed corpus, against which the decoder restores
/// 371 of 504 regions byte-identically to the unpacked originals (the rest are the
/// resource bytes ASPack moves out of .rsrc before compressing).
/// </remarks>
[TestFixture]
public class AsPackUnpackerTests {

  private const int TextRva = 0x1000;
  private const int StubRva = 0x2000;
  private const byte CallFilterMarker = 0x05;

  [Test, Category("HappyPath")]
  public void AsPackHandler_RestoresRegion_FromLiteralsMatchesAndRepeatDistances() {
    var original = BuildPayload();
    var packed = BuildAsPackPe(original, aligned: false, wideCallFilter: false);

    var result = Unpack(packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(RestoredRegion(result), Is.EqualTo(original).AsCollection);
      Assert.That(result.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void AsPackHandler_RestoresRegion_WhenBlockUsesAlignedOffsets() {
    var original = BuildPayload();
    var packed = BuildAsPackPe(original, aligned: true, wideCallFilter: false);

    Assert.That(RestoredRegion(Unpack(packed)), Is.EqualTo(original).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void AsPackHandler_ReversesWideCallFilterVariant() {
    var original = BuildPayload();
    var packed = BuildAsPackPe(original, aligned: false, wideCallFilter: true);

    Assert.That(RestoredRegion(Unpack(packed)), Is.EqualTo(original).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void AsPackHandler_ReportsOriginalEntryPointAndRegionTable() {
    var packed = BuildAsPackPe(BuildPayload(), aligned: false, wideCallFilter: false);

    var metadata = Encoding.UTF8.GetString(
      Unpack(packed).Artifacts.Single(a => a.Name == "metadata.json").Data);

    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("\"compressionCore\": \"aspack-lz\""));
      Assert.That(metadata, Does.Contain("\"originalEntryPointRva\": \"0x00001234\""));
      Assert.That(metadata, Does.Contain("\"regionsDecoded\": 1"));
    });
  }

  [Test, Category("EdgeCase")]
  public void AsPackHandler_LeavesStoredRegionsAlone() {
    var original = BuildPayload();
    var packed = BuildAsPackPe(original, aligned: false, wideCallFilter: false, addStoredRegion: true);

    var result = Unpack(packed);
    var metadata = Encoding.UTF8.GetString(result.Artifacts.Single(a => a.Name == "metadata.json").Data);

    Assert.Multiple(() => {
      Assert.That(RestoredRegion(result), Is.EqualTo(original).AsCollection);
      Assert.That(metadata, Does.Contain("\"regionsStored\": 1"));
      Assert.That(result.Artifacts.Count(a => a.Name.StartsWith("sections/", StringComparison.Ordinal)), Is.EqualTo(1));
    });
  }

  private static UnpackResult Unpack(byte[] image) {
    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == "aspack");
    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True);
    return handler.Unpack(handler.Parse(image, detection), new());
  }

  private static byte[] RestoredRegion(UnpackResult result) =>
    result.Artifacts.Single(a => a.Name.StartsWith("sections/", StringComparison.Ordinal)).Data;

  /// <summary>
  /// A payload that forces the decoder through every token shape: plain literals,
  /// a short match, a repeat-distance match, a match whose length comes from the
  /// length alphabet, a match whose distance carries extra bits, and a near call
  /// whose operand the packer's filter rewrote.
  /// </summary>
  private static byte[] BuildPayload() {
    var payload = new List<byte>();
    for (var i = 0; i < 48; ++i) payload.Add((byte)(0x30 + i));      // literals
    payload.Add(0xE8);                                               // near call
    payload.AddRange([0x23, 0x01, 0x00, 0x00]);                      // displacement 0x123
    for (var i = 0; i < 16; ++i) payload.Add((byte)(0x70 + i));      // more literals
    for (var i = 0; i < 7; ++i) payload.Add(payload[^1]);            // run: distance 1
    for (var i = 0; i < 9; ++i) payload.Add(payload[^1]);            // repeat distance, length alphabet
    var at = payload.Count - 16;
    for (var i = 0; i < 6; ++i) payload.Add(payload[at + i]);        // distance 16: extra bits
    return [.. payload];
  }

  private static byte[] BuildAsPackPe(byte[] original, bool aligned, bool wideCallFilter, bool addStoredRegion = false) {
    var filtered = ApplyCallFilter(original, wideCallFilter);
    var stream = AsPackStreamWriter.Write(filtered, aligned);
    var stub = BuildStub(original.Length, wideCallFilter, addStoredRegion);

    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;
    const int textRaw = 0x400;
    var stubRaw = textRaw + Align(stream.Length, 0x200);
    var image = new byte[stubRaw + Align(stub.Length, 0x200)];

    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), StubRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);

    WriteSection(image, sectionOffset, ".text", TextRva, 0x1000, stream.Length, textRaw, 0xE0000060);
    WriteSection(image, sectionOffset + 40, ".aspack", StubRva, 0x1000, stub.Length, stubRaw, 0xE0000060);
    WriteSection(image, sectionOffset + 80, ".adata", 0x3000, 0x1000, 0, 0, 0xE0000040);

    stream.CopyTo(image.AsSpan(textRaw));
    stub.CopyTo(image.AsSpan(stubRaw));
    return image;
  }

  private static void WriteSection(byte[] image, int at, string name, uint rva, uint virtualSize, int rawSize, int rawOffset, uint characteristics) {
    Encoding.ASCII.GetBytes(name).CopyTo(image.AsSpan(at, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 8), virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 12), rva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 16), (uint)rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 20), (uint)rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 36), characteristics);
  }

  /// <summary>
  /// The stub bytes the handler actually reads: the region table first (so the
  /// scan cannot latch onto an earlier accidental RVA-shaped word), then the
  /// anchors for the call-filter variant, the filter-enable flag and the original
  /// entry point.
  /// </summary>
  private static byte[] BuildStub(int regionSize, bool wideCallFilter, bool addStoredRegion) {
    var stub = new byte[0x200];
    var at = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(at), TextRva);
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(at + 4), (uint)regionSize);
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(at + 8), 0x60000020);
    at += 12;
    if (addStoredRegion) {
      BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(at), 0x3000);
      BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(at + 4), 0xFFFFFEF2);
      BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(at + 8), 0xE0000040);
      at += 12;
    }

    at += 12; // terminating zero record

    // mov eax,[esi] / jmp $+2 / cmp byte [esi],marker / jne / and al,0 / rol eax,24 / sub eax,ebx / mov [esi],eax
    // A jump displacement of 10 skips the marker test and the 24-bit repack, which is the wide variant.
    byte[] filterAnchor = [
      0x8B, 0x06, 0xEB, wideCallFilter ? (byte)0x0A : (byte)0x00, 0x80, 0x3E, CallFilterMarker, 0x75, 0xF3,
      0x24, 0x00, 0xC1, 0xC0, 0x18, 0x2B, 0xC3, 0x89, 0x06,
    ];
    filterAnchor.CopyTo(stub.AsSpan(at));
    at += filterAnchor.Length;

    byte[] flagAnchor = [0xB3, 0x00, 0x80, 0xFB, 0x00, 0x75];
    flagAnchor.CopyTo(stub.AsSpan(at));
    at += flagAnchor.Length;

    byte[] entryAnchor = [
      0xB8, 0x34, 0x12, 0x00, 0x00, 0x50, 0x03, 0x85, 0x88, 0x04, 0x00, 0x00, 0x59, 0x0B, 0xC9, 0x89, 0x85,
    ];
    entryAnchor.CopyTo(stub.AsSpan(at));
    at += entryAnchor.Length;

    "ASPack"u8.CopyTo(stub.AsSpan(at));
    return stub;
  }

  /// <summary>
  /// The packer side of the E8/E9 filter: rewrite each near call's displacement to
  /// an absolute address, either as a marker byte plus a 24-bit value or as a plain
  /// 32-bit one. Mirrors the reversal the stub performs, including its scan
  /// bookkeeping, so the two agree on which operands were touched.
  /// </summary>
  private static byte[] ApplyCallFilter(byte[] payload, bool wide) {
    var buffer = (byte[])payload.Clone();
    var remaining = buffer.Length - 5;
    var position = 0;
    while (remaining > 0) {
      var opcode = buffer[position];
      ++position;
      if (opcode is 0xE8 or 0xE9) {
        var opcodeOffset = (uint)(position - 1);
        var absolute = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(position)) + opcodeOffset;
        Assert.That(absolute, Is.LessThan(1u << 24), "test payload's call target must fit the 24-bit filter form");
        if (wide)
          BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(position), absolute);
        else {
          buffer[position] = CallFilterMarker;
          buffer[position + 1] = (byte)absolute;
          buffer[position + 2] = (byte)(absolute >> 8);
          buffer[position + 3] = (byte)(absolute >> 16);
        }

        position += 4;
        remaining -= 5;
      } else
        --remaining;
    }

    return buffer;
  }

  private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}

/// <summary>
/// Minimal writer for the ASPack stream: one block whose alphabets are complete
/// codes over the symbols it needs, followed by literal and match tokens.
/// </summary>
internal static class AsPackStreamWriter {

  private const int MainSymbols = 0x2D1;
  private const int LengthSymbols = 0x1C;
  private const int AlignedSymbols = 8;
  private const int PreTreeSymbols = 0x13;
  private const int CodeLengthCount = MainSymbols + LengthSymbols + AlignedSymbols;
  private const int CodeBits = 24;

  private static readonly int[] PositionExtraBits = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
    7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
    15, 15, 16, 16, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
    17, 17, 18, 18, 18, 18, 18, 18, 18, 18,
  ];

  private static readonly uint[] PositionBases = BuildPositionBases();

  private static uint[] BuildPositionBases() {
    var bases = new uint[PositionExtraBits.Length];
    var accumulator = 0u;
    for (var i = 0; i < bases.Length; ++i) {
      bases[i] = accumulator;
      accumulator += 1u << PositionExtraBits[i];
    }

    return bases;
  }

  public static byte[] Write(byte[] payload, bool aligned) {
    var tokens = Parse(payload);

    // Main alphabet: every literal, plus the (slot, footer) symbol of each match.
    var usableMain = new SortedSet<int>();
    for (var i = 0; i < 0x100; ++i) usableMain.Add(i);
    foreach (var token in tokens)
      if (token.IsMatch)
        usableMain.Add(0x100 + token.Slot * 8 + token.Footer);

    var lengths = new byte[CodeLengthCount];
    AssignCompleteLengths(lengths, 0, [.. usableMain]);
    AssignCompleteLengths(lengths, MainSymbols, [0, 1]);
    // Eight uniform 3-bit codes are the stub's "aligned offsets are not in use"
    // signal, so the aligned block needs a deliberately lopsided complete code.
    byte[] alignedLengths = aligned ? [1, 2, 3, 4, 5, 6, 7, 7] : [3, 3, 3, 3, 3, 3, 3, 3];
    alignedLengths.CopyTo(lengths.AsSpan(MainSymbols + LengthSymbols));

    var preTreeLengths = new byte[PreTreeSymbols];
    var usedLengths = new SortedSet<int>(lengths.Select(l => (int)l));
    AssignCompleteLengths(preTreeLengths, 0, [.. usedLengths]);

    var writer = new BitWriter();
    writer.Write(0, 1);                                     // reset the previous block's code lengths
    foreach (var length in preTreeLengths) writer.Write(length, 4);

    var preTree = new CanonicalCode(preTreeLengths);
    foreach (var length in lengths) preTree.Emit(writer, length);

    var main = new CanonicalCode(lengths.AsSpan(0, MainSymbols).ToArray());
    var lengthCode = new CanonicalCode(lengths.AsSpan(MainSymbols, LengthSymbols).ToArray());
    var alignedCode = new CanonicalCode(lengths.AsSpan(MainSymbols + LengthSymbols, AlignedSymbols).ToArray());

    foreach (var token in tokens) {
      if (!token.IsMatch) {
        main.Emit(writer, token.Literal);
        continue;
      }

      main.Emit(writer, 0x100 + token.Slot * 8 + token.Footer);
      if (token.Footer == 7) {
        lengthCode.Emit(writer, token.LengthSlot);
        writer.Write((uint)token.LengthExtra, token.LengthExtraBits);
      }

      var extraBits = PositionExtraBits[token.Slot];
      var verbatim = token.DistanceCode - PositionBases[token.Slot];
      if (aligned && extraBits >= 3) {
        writer.Write(verbatim >> 3, extraBits - 3);
        alignedCode.Emit(writer, (int)(verbatim & 7));
      } else
        writer.Write(verbatim, extraBits);
    }

    return writer.ToArray();
  }

  private readonly record struct Token(
    bool IsMatch, int Literal, int Slot, int Footer, uint DistanceCode,
    int LengthSlot, int LengthExtra, int LengthExtraBits);

  /// <summary>
  /// A deliberately literal-first parse: emit a match only where the payload
  /// repeats one of the shapes the tests care about, everything else as literals.
  /// </summary>
  private static List<Token> Parse(byte[] payload) {
    var tokens = new List<Token>();
    var position = 0;
    var recent = new uint[3];
    while (position < payload.Length) {
      var best = 0;
      var bestDistance = 0;
      for (var distance = 1; distance <= Math.Min(position, 0x2000); ++distance) {
        var length = 0;
        while (length < 264 && position + length < payload.Length && payload[position + length] == payload[position - distance + length])
          ++length;
        if (length > best) {
          best = length;
          bestDistance = distance;
        }
      }

      if (best < 3) {
        tokens.Add(new(false, payload[position], 0, 0, 0, 0, 0, 0));
        ++position;
        continue;
      }

      var distanceCode = RecencyCode(recent, (uint)(bestDistance - 1));
      var slot = SlotOf(distanceCode);
      if (best <= 8)
        tokens.Add(new(true, 0, slot, best - 2, distanceCode, 0, 0, 0));
      else {
        // Length alphabet slot 0 is base 0 with no extra bits, so it spells 9.
        best = 9;
        tokens.Add(new(true, 0, slot, 7, distanceCode, 0, 0, 0));
      }

      position += best;
    }

    return tokens;
  }

  private static uint RecencyCode(uint[] recent, uint value) {
    for (var i = 0; i < recent.Length; ++i)
      if (recent[i] == value) {
        if (i != 0) (recent[0], recent[i]) = (recent[i], recent[0]);
        return (uint)i;
      }

    recent[2] = recent[1];
    recent[1] = recent[0];
    recent[0] = value;
    return value + 3;
  }

  private static int SlotOf(uint distanceCode) {
    var slot = 0;
    while (slot + 1 < PositionBases.Length && PositionBases[slot + 1] <= distanceCode)
      ++slot;
    return slot;
  }

  /// <summary>
  /// Gives <paramref name="symbols"/> the depths of a complete binary tree, so the
  /// resulting canonical code fills the 24-bit space exactly; every other symbol of
  /// the alphabet keeps length zero and is never emitted.
  /// </summary>
  private static void AssignCompleteLengths(Span<byte> lengths, int offset, IReadOnlyList<int> symbols) {
    var count = symbols.Count;
    var depth = 0;
    while (1 << (depth + 1) <= count) ++depth;
    var remainder = count - (1 << depth);
    for (var i = 0; i < count; ++i)
      lengths[offset + symbols[i]] = (byte)(i < 2 * remainder ? depth + 1 : depth);
  }

  private sealed class CanonicalCode {

    private readonly uint[] _codes;
    private readonly int[] _lengths;

    public CanonicalCode(byte[] lengths) {
      var counts = new int[16];
      foreach (var length in lengths) ++counts[length];

      var limits = new uint[16];
      var first = new int[16];
      var accumulated = 0u;
      for (var length = 1; length <= 15; ++length) {
        accumulated += (uint)counts[length] << (CodeBits - length);
        limits[length] = accumulated;
        first[length] = first[length - 1] + counts[length - 1];
      }

      if (accumulated != 1u << CodeBits)
        throw new InvalidOperationException("test code lengths do not form a complete code");

      var next = (int[])first.Clone();
      this._codes = new uint[lengths.Length];
      this._lengths = new int[lengths.Length];
      for (var symbol = 0; symbol < lengths.Length; ++symbol) {
        var length = lengths[symbol];
        if (length == 0) continue;
        var index = next[length]++;
        this._lengths[symbol] = length;
        this._codes[symbol] =
          (limits[length - 1] + ((uint)(index - first[length]) << (CodeBits - length))) >> (CodeBits - length);
      }
    }

    public void Emit(BitWriter writer, int symbol) {
      if (this._lengths[symbol] == 0)
        throw new InvalidOperationException($"symbol {symbol} has no code");
      writer.Write(this._codes[symbol], this._lengths[symbol]);
    }
  }

  private sealed class BitWriter {

    private readonly List<byte> _bytes = [];
    private uint _accumulator;
    private int _count;

    public void Write(uint value, int bits) {
      for (var i = bits - 1; i >= 0; --i) {
        this._accumulator = (this._accumulator << 1) | ((value >> i) & 1);
        if (++this._count != 8) continue;
        this._bytes.Add((byte)this._accumulator);
        this._accumulator = 0;
        this._count = 0;
      }
    }

    public byte[] ToArray() {
      while (this._count != 0) this.Write(0, 1);
      return [.. this._bytes];
    }
  }
}
