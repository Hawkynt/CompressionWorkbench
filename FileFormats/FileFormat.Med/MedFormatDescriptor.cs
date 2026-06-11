#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Med;

/// <summary>
/// Exposes an Amiga MED / OctaMED module (MMD0..MMD3) as a read-only pseudo-archive
/// of <c>FULL.med</c>, <c>metadata.ini</c>, <c>patterns/block_NN.bin</c> (one raw
/// block — NO decode) and <c>samples/NN_{name}.raw</c> per instrument sample. The
/// big-endian MED layout was recovered through binary inspection of the documented
/// OctaMED MMD0/MMD1/MMD2/MMD3 file format and the OpenMPT/libmodplug loaders.
/// All pointer reads are clamped to the buffer; a malformed module surfaces
/// FULL + metadata(parse_status=partial) rather than throwing.
/// </summary>
public sealed class MedFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Med";
  public string DisplayName => "MED / OctaMED Module";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".med";
  public IReadOnlyList<string> Extensions => [".med", ".mmd0", ".mmd1", ".mmd2", ".mmd3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MMD0"u8.ToArray(), Offset: 0, Confidence: 0.95),
    new("MMD1"u8.ToArray(), Offset: 0, Confidence: 0.95),
    new("MMD2"u8.ToArray(), Offset: 0, Confidence: 0.95),
    new("MMD3"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description =>
    "Amiga MED / OctaMED module (MMD0..MMD3) surfaced as a read-only pseudo-archive " +
    "(FULL + metadata + per-block pattern blobs + raw instrument samples); never synth-emulated.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    Decompose(ReadAll(stream)).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in Decompose(ReadAll(stream))) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private readonly record struct Entry(string Name, byte[] Data, string Kind);

  private static List<Entry> Decompose(byte[] f) {
    var entries = new List<Entry> { new("FULL.med", f, "Track") };
    var meta = new StringBuilder().AppendLine("[med]");
    var ok = false;

    try {
      if (f.Length >= 8 && f[0] == 'M' && f[1] == 'M' && f[2] == 'D' &&
          f[3] >= '0' && f[3] <= '3') {
        var ver = (char)f[3];
        meta.Append("magic = MMD").Append(ver).Append('\n');

        // MMD0 header (big-endian): modlen@4, song ptr@8, blockarr ptr@16,
        // smplarr ptr@24, expdata ptr@32. (MMD2 reuses the same field layout.)
        var songPtr = (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(8, 4));
        var blockArrPtr = ReadU32(f, 16);
        var smplArrPtr = ReadU32(f, 24);
        var expDataPtr = ReadU32(f, 32);

        // MMDSong struct: 63 sample-spec entries (8 bytes each) then counts.
        // numblocks@(songPtr+504) u16, songlen@(songPtr+506) u16,
        // numsamples@(songPtr+767) byte (after 504 bytes of sample specs +
        // 256 playseq + ...). Field positions vary subtly across MMD0..MMD3;
        // we read defensively and clamp everything.
        var numSamples = 0;
        var numBlocks = 0;
        if (InRange(f, songPtr, 768)) {
          numBlocks = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(songPtr + 504, 2));
          numSamples = f[songPtr + 767];
        }
        meta.Append("song_offset = ").Append(songPtr).Append('\n');
        meta.Append("num_blocks = ").Append(numBlocks).Append('\n');
        meta.Append("num_samples = ").Append(numSamples).Append('\n');

        // Block pointer table: numBlocks u32 pointers at blockArrPtr.
        if (numBlocks is > 0 and <= 4096 && InRange(f, blockArrPtr, numBlocks * 4)) {
          for (var b = 0; b < numBlocks; ++b) {
            var bp = (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(blockArrPtr + b * 4, 4));
            if (!InRange(f, bp, 4)) continue;
            // MMD0Block: 2 bytes (lines, tracks); MMD1Block: numtracks u16, lines u16.
            // Conservatively surface a bounded slice as the raw block.
            var blockLen = EstimateBlockLen(f, bp, ver);
            var take = Math.Min(blockLen, f.Length - bp);
            if (take <= 0) continue;
            entries.Add(new($"patterns/block_{b + 1:D2}.bin", f.AsSpan(bp, take).ToArray(), "Pattern"));
          }
        }

        // Sample pointer table: numSamples u32 pointers at smplArrPtr.
        if (numSamples is > 0 and <= 256 && InRange(f, smplArrPtr, numSamples * 4)) {
          for (var s = 0; s < numSamples; ++s) {
            var sp = (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(smplArrPtr + s * 4, 4));
            if (sp == 0 || !InRange(f, sp, 6)) continue;
            // InstrHdr: length u32, type s16, then raw sample bytes.
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(sp, 4));
            var dataOff = sp + 6;
            if (len <= 0 || !InRange(f, dataOff, 1)) continue;
            var take = Math.Min(len, f.Length - dataOff);
            if (take <= 0) continue;
            entries.Add(new($"samples/{s + 1:D2}_instrument.raw", f.AsSpan(dataOff, take).ToArray(), "Sample"));
          }
        }

        // Annotation / song name lives in the expansion data block when present.
        var name = ReadExpName(f, expDataPtr);
        if (name.Length > 0) meta.Append("song_name = ").Append(name).Append('\n');

        ok = true;
      }
    } catch { /* fall through to partial */ }

    if (!ok) meta.Append("parse_status = partial\n");
    entries.Insert(1, new("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString()), "Tag"));
    return entries;
  }

  private static int EstimateBlockLen(byte[] f, int bp, char ver) {
    // MMD0: lines byte@bp, numtracks byte@bp+1; note triplets follow.
    // MMD1+: numtracks u16@bp, lines u16@bp+2; 4-byte note cells.
    if (ver == '0') {
      var lines = f[bp] + 1;
      var tracks = f[bp + 1];
      return 2 + lines * tracks * 3;
    }
    if (!InRange(f, bp, 4)) return 4;
    var t = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(bp, 2));
    var l = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(bp + 2, 2)) + 1;
    return 8 + l * t * 4;
  }

  private static string ReadExpName(byte[] f, int expDataPtr) {
    // MMDExp: ...; s_ext_entrsz etc. The annotation pointer (annotxt) lives at
    // expDataPtr+8, length at +12. We clamp and read printable ASCII.
    if (!InRange(f, expDataPtr, 16)) return "";
    var annoPtr = (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(expDataPtr + 8, 4));
    var annoLen = (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(expDataPtr + 12, 4));
    if (annoPtr == 0 || annoLen <= 0 || !InRange(f, annoPtr, 1)) return "";
    var take = Math.Min(annoLen, f.Length - annoPtr);
    var sb = new StringBuilder();
    for (var i = 0; i < take; ++i) {
      var b = f[annoPtr + i];
      if (b == 0) break;
      if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static uint ReadU32Raw(byte[] f, int off) =>
    InRange(f, off, 4) ? BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(off, 4)) : 0u;

  private static int ReadU32(byte[] f, int off) => (int)ReadU32Raw(f, off);

  private static bool InRange(byte[] f, int off, int len) =>
    off >= 0 && len >= 0 && (long)off + len <= f.Length;
}
