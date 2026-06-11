#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ay;

/// <summary>
/// Exposes a ZX Spectrum / Amstrad CPC AY-3-8910 music file (.ay) as a read-only
/// pseudo-archive of <c>FULL.ay</c>, <c>metadata.ini</c> (author, misc, song names)
/// and <c>songs/NN_{name}.bin</c> per-song data block. The big-endian, relative
/// signed-16-bit pointer layout was recovered through binary inspection of the
/// documented ZXAYEMUL file format. The AY chip is never emulated; every pointer is
/// resolved relative to its own offset and clamped to the buffer, and a malformed
/// file surfaces FULL + metadata(parse_status=partial) instead of throwing.
/// </summary>
public sealed class AyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Ay";
  public string DisplayName => "AY (ZX Spectrum / AY-3-8910)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ay";
  public IReadOnlyList<string> Extensions => [".ay"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ZXAYEMUL"u8.ToArray(), Offset: 0, Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "ZX Spectrum / Amstrad AY-3-8910 music file surfaced as a read-only pseudo-archive " +
    "(FULL + metadata + per-song data blocks); the AY chip is never emulated.";

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
    var entries = new List<Entry> { new("FULL.ay", f, "Track") };
    var meta = new StringBuilder().AppendLine("[ay]");
    var ok = false;

    try {
      if (f.Length >= 0x14 && f.AsSpan(0, 8).SequenceEqual("ZXAYEMUL"u8)) {
        // Header (big-endian, all pointers are signed-16 relative to their offset):
        //  0x08 FileVersion, 0x09 PlayerVersion,
        //  0x0A rel ptr SpecialPlayer, 0x0C rel ptr Author,
        //  0x0E rel ptr Misc, 0x10 NumOfSongs (u8), 0x11 FirstSong (u8),
        //  0x12 rel ptr SongStructure.
        meta.Append("file_version = ").Append(f[0x08]).Append('\n');
        meta.Append("player_version = ").Append(f[0x09]).Append('\n');

        var author = ReadRelString(f, 0x0C);
        var misc = ReadRelString(f, 0x0E);
        if (author.Length > 0) meta.Append("author = ").Append(author).Append('\n');
        if (misc.Length > 0) meta.Append("misc = ").Append(misc).Append('\n');

        var numSongs = f[0x10];
        var firstSong = f[0x11];
        meta.Append("num_songs = ").Append(numSongs).Append('\n');
        meta.Append("first_song = ").Append(firstSong).Append('\n');

        var songStruct = ResolveRel(f, 0x12);
        // SongStructure: per song, 4 bytes (rel ptr SongName @0, rel ptr SongData @2).
        if (songStruct >= 0 && numSongs > 0) {
          for (var s = 0; s < numSongs; ++s) {
            var entryOff = songStruct + s * 4;
            if (!InRange(f, entryOff, 4)) break;
            var name = ReadRelString(f, entryOff);
            if (name.Length > 0) meta.Append($"song_{s + 1:D2}_name = ").Append(name).Append('\n');

            // SongData: 14-byte block then per-channel pointers; we surface a
            // bounded slice from the SongData pointer up to the next song's data
            // (or EOF) as the raw per-song block.
            var dataPtr = ResolveRel(f, entryOff + 2);
            if (dataPtr < 0 || !InRange(f, dataPtr, 1)) continue;
            var nextPtr = f.Length;
            if (s + 1 < numSongs && InRange(f, songStruct + (s + 1) * 4 + 2, 2)) {
              var np = ResolveRel(f, songStruct + (s + 1) * 4 + 2);
              if (np > dataPtr && np <= f.Length) nextPtr = np;
            }
            var take = Math.Min(nextPtr - dataPtr, f.Length - dataPtr);
            if (take <= 0) continue;
            var safe = name.Length == 0 ? "song" : Sanitize(name);
            entries.Add(new($"songs/{s + 1:D2}_{safe}.bin", f.AsSpan(dataPtr, take).ToArray(), "Track"));
          }
        }
        ok = true;
      }
    } catch { /* fall through to partial */ }

    if (!ok) meta.Append("parse_status = partial\n");
    entries.Insert(1, new("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString()), "Tag"));
    return entries;
  }

  // Resolve a big-endian signed-16 relative pointer stored at <paramref name="off"/>.
  private static int ResolveRel(byte[] f, int off) {
    if (!InRange(f, off, 2)) return -1;
    var rel = (short)BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(off, 2));
    if (rel == 0) return -1;
    var target = off + rel;
    return InRange(f, target, 1) ? target : -1;
  }

  private static string ReadRelString(byte[] f, int off) {
    var target = ResolveRel(f, off);
    if (target < 0) return "";
    var sb = new StringBuilder();
    for (var i = target; i < f.Length; ++i) {
      var b = f[i];
      if (b == 0) break;
      if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static string Sanitize(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
    var s = sb.ToString().Trim('_');
    return s.Length == 0 ? "song" : s;
  }

  private static bool InRange(byte[] f, int off, int len) =>
    off >= 0 && len >= 0 && (long)off + len <= f.Length;
}
