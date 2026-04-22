#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.GameMaker;

/// <summary>
/// GameMaker Studio data file (data.win / game.unx / game.ios). IFF-style FORM container
/// with typed chunks. Surfaces each chunk as a raw blob plus split PNG textures (TXTR),
/// WAV/OGG audio (AUDO) and the string table (STRG).
/// </summary>
public sealed class GameMakerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "GameMaker";
  public string DisplayName => "GameMaker data file";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".win";
  public IReadOnlyList<string> Extensions => [".win", ".unx", ".ios"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("FORM"u8.ToArray(), Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "GameMaker Studio runtime data file (FORM/IFF-style). Extracts chunks, split PNG textures, " +
    "per-entry audio blobs, and the string table.";

  private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.win", "Track", blob),
    };

    if (blob.Length < 8) return entries;
    if (blob[0] != 'F' || blob[1] != 'O' || blob[2] != 'R' || blob[3] != 'M') return entries;

    var detected = new List<string>();
    var parseStatuses = new Dictionary<string, string>();
    string? gameTitle = null, gameVersion = null, timestamp = null;

    try {
      var formSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4));
      var formEnd = Math.Min(8L + formSize, blob.Length);

      var cursor = 8;
      while (cursor + 8 <= formEnd) {
        var tag = Encoding.ASCII.GetString(blob, cursor, 4);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(cursor + 4));
        var dataStart = cursor + 8;
        var dataEnd = dataStart + (long)size;
        if (dataEnd > formEnd) break;

        // Sanitize tag to safe chars (chunk tags are ASCII letters, but guard anyway).
        var safeTag = SanitizeTag(tag);
        detected.Add(safeTag);

        var chunkBytes = blob.AsSpan(dataStart, (int)size).ToArray();
        entries.Add(($"chunks/{safeTag}.bin", "Tag", chunkBytes));

        try {
          switch (safeTag) {
            case "GEN8":
              (gameTitle, gameVersion, timestamp) = TryParseGen8(chunkBytes, blob);
              break;
            case "TXTR":
              SplitTextures(chunkBytes, entries);
              break;
            case "AUDO":
              SplitAudio(chunkBytes, entries);
              break;
            case "STRG":
              ExtractStrings(chunkBytes, entries);
              break;
          }
        } catch {
          parseStatuses[safeTag] = "partial";
        }

        cursor = (int)dataEnd;
      }
    } catch {
      // Fall through with whatever was collected.
    }

    // Metadata.
    var ini = new StringBuilder();
    ini.AppendLine("; GameMaker data.win metadata");
    ini.Append("total_chunks=").Append(detected.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
    ini.Append("detected_chunks=").AppendLine(string.Join(",", detected));
    if (gameTitle != null) ini.Append("game_title=").AppendLine(gameTitle);
    if (gameVersion != null) ini.Append("game_version=").AppendLine(gameVersion);
    if (timestamp != null) ini.Append("timestamp=").AppendLine(timestamp);
    foreach (var kv in parseStatuses)
      ini.Append(kv.Key).Append("_parse_status=").AppendLine(kv.Value);
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));

    return entries;
  }

  private static (string? title, string? version, string? timestamp) TryParseGen8(byte[] chunk, byte[] fullBlob) {
    // GEN8 layout (variable across versions); attempt a best-effort read.
    // Header starts with:
    //   uint32 disableDebugger(+flags), uint32 bytecodeVersion, uint16 pad, uint16 pad,
    //   uint32 namePtr, uint32 configPtr, uint32 lastObjId, uint32 lastTileId,
    //   uint32 gameId, ...
    // Then after a ways: name ptr, major/minor/release/build, window w/h, ...
    // We just scan for the 64-bit timestamp by looking at offset 0x20..0x28 area.
    if (chunk.Length < 0x40) return (null, null, null);

    string? title = null;
    var namePtr = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(0x10));
    if (namePtr > 0 && namePtr + 4 < fullBlob.Length) {
      var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(fullBlob.AsSpan(namePtr));
      if (nameLen > 0 && nameLen < 512 && namePtr + 4 + nameLen <= fullBlob.Length)
        title = Encoding.UTF8.GetString(fullBlob, namePtr + 4, nameLen);
    }

    string? version = null;
    // Version quadruplet historically lives near the beginning — try a few common offsets.
    // We don't error if the guesses look wrong; just emit nothing.
    for (var probe = 0x14; probe + 16 <= Math.Min(0x60, chunk.Length); probe += 4) {
      var a = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(probe));
      var b = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(probe + 4));
      var c = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(probe + 8));
      var d = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(probe + 12));
      if (a < 10 && b < 100 && c < 100 && d < 1_000_000) {
        version = $"{a}.{b}.{c}.{d}";
        break;
      }
    }

    string? timestamp = null;
    // Timestamp is a uint64 unix time; skip — too version-dependent to guess reliably.

    return (title, version, timestamp);
  }

  private static void SplitTextures(byte[] chunk, List<(string Name, string Kind, byte[] Data)> entries) {
    if (chunk.Length < 4) return;
    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(0));
    if (count < 0 || count > 100_000 || 4 + count * 4 > chunk.Length) return;

    // Collect all PNG start offsets by scanning directly: each entry is a small
    // struct (uint32 scaled flag + uint32 png-offset) but layout varies by version.
    // Easier: scan the whole chunk for PNG magic and split.
    var pngStarts = new List<int>();
    for (var i = 0; i <= chunk.Length - PngMagic.Length; ++i) {
      var match = true;
      for (var j = 0; j < PngMagic.Length; ++j) {
        if (chunk[i + j] != PngMagic[j]) { match = false; break; }
      }
      if (match) pngStarts.Add(i);
    }

    for (var i = 0; i < pngStarts.Count; ++i) {
      var start = pngStarts[i];
      var end = i + 1 < pngStarts.Count ? pngStarts[i + 1] : chunk.Length;
      entries.Add(($"textures/{i:D4}.png", "Track",
        chunk.AsSpan(start, end - start).ToArray()));
    }
  }

  private static void SplitAudio(byte[] chunk, List<(string Name, string Kind, byte[] Data)> entries) {
    if (chunk.Length < 4) return;
    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(0));
    if (count < 0 || count > 1_000_000) return;

    var ptrArea = 4 + count * 4;
    if (ptrArea > chunk.Length) return;

    for (var i = 0; i < count; ++i) {
      var ptrLoc = 4 + i * 4;
      var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(ptrLoc));
      if (off < 0 || off + 4 > chunk.Length) continue;
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(off));
      if (len <= 0 || off + 4 + len > chunk.Length) continue;
      var data = chunk.AsSpan(off + 4, len).ToArray();

      var ext = ".bin";
      if (len >= 4) {
        if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F') ext = ".wav";
        else if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S') ext = ".ogg";
      }
      entries.Add(($"audio/{i:D4}{ext}", "Track", data));
    }
  }

  private static void ExtractStrings(byte[] chunk, List<(string Name, string Kind, byte[] Data)> entries) {
    if (chunk.Length < 4) return;
    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(0));
    if (count < 0 || count > 10_000_000) return;
    if (4 + count * 4 > chunk.Length) return;

    var sb = new StringBuilder();
    // Offsets in STRG point at absolute file offsets, but the extracted chunk body is
    // self-contained: the offsets are relative to the start of the FORM chunk payload
    // area — so simply walk the per-entry (uint32 len + utf8 + NUL) records starting
    // after the pointer table.
    var cursor = 4 + count * 4;
    for (var i = 0; i < count && cursor + 4 <= chunk.Length; ++i) {
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(cursor));
      cursor += 4;
      if (len < 0 || cursor + len > chunk.Length) break;
      sb.AppendLine(Encoding.UTF8.GetString(chunk, cursor, len).Replace("\r", " ").Replace("\n", " "));
      cursor += len + 1; // trailing NUL
    }
    entries.Add(("strings.txt", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
  }

  private static string SanitizeTag(string tag) {
    var sb = new StringBuilder(4);
    foreach (var c in tag)
      sb.Append(char.IsLetterOrDigit(c) ? c : '_');
    return sb.ToString();
  }
}
