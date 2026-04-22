#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Fmod;

/// <summary>
/// FMOD Sample Bank (.fsb, version 5) surfaced as an archive. Enumerates the individual
/// sample blobs plus the header / name-table raw sections and summary metadata. Audio
/// payloads are kept in their native encoded form (Vorbis/XMA/ADPCM/etc.) — no decoding.
/// </summary>
public sealed class FmodFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Fmod";
  public string DisplayName => "FMOD Sample Bank";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".fsb";
  public IReadOnlyList<string> Extensions => [".fsb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("FSB5"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "FMOD Sample Bank (FSB5) — extracts per-sample raw encoded audio (Vorbis/XMA/ADPCM/...) plus header metadata. " +
    "Audio is not decoded.";

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
      ("FULL.fsb", "Track", blob),
    };

    // Header = 60 bytes (magic+version+5 sizes+mode+32 flags/hash).
    if (blob.Length < 60) return entries;
    if (blob[0] != 'F' || blob[1] != 'S' || blob[2] != 'B' || blob[3] != '5') return entries;

    try {
      var version = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4));
      var numSamples = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8));
      var sampleHeadersSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12));
      var nameTableSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(16));
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(20));
      var mode = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(24));

      const int headerSize = 60;
      var headersStart = headerSize;
      var headersEnd = (long)headersStart + sampleHeadersSize;
      var namesStart = headersEnd;
      var namesEnd = namesStart + nameTableSize;
      var dataStart = namesEnd;
      var dataEnd = dataStart + dataSize;

      // Clamp against blob length so we never throw.
      headersEnd = Math.Min(headersEnd, blob.Length);
      namesEnd = Math.Min(namesEnd, blob.Length);
      dataEnd = Math.Min(dataEnd, blob.Length);

      // Raw sections.
      if (sampleHeadersSize > 0 && headersEnd > headersStart) {
        entries.Add(("sample_headers.bin", "Tag",
          blob.AsSpan(headersStart, (int)(headersEnd - headersStart)).ToArray()));
      }
      if (nameTableSize > 0 && namesEnd > namesStart) {
        entries.Add(("name_table.bin", "Tag",
          blob.AsSpan((int)namesStart, (int)(namesEnd - namesStart)).ToArray()));
      }

      // Parse sample headers: each starts with a 64-bit little-endian packed field
      // whose top bits contain the offset-in-data (in 32-byte units). The remaining
      // bits encode flags (has-name, has-extra-chunks, ...). If extra-chunks flag is
      // set, 4-byte chunk headers follow until a chunk has more_chunks=0.
      //
      // We only need the offset-in-data here; the sample length is derived from the
      // next header's offset (or data_size for the last entry).
      var sampleExt = ExtensionForMode(mode);
      var sampleOffsets = new List<long>();
      var headerCursor = headersStart;
      for (var i = 0; i < numSamples && headerCursor + 8 <= headersEnd; ++i) {
        var packed = BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(headerCursor));
        // Layout (common FSB5):
        //   bit 0     : has_next_chunk
        //   bits 1-30 : offset_in_data (in 32-byte units)
        //   bits 31-62: frequency-index / channels / etc.
        // We only need offset_in_data → treat bits 7..33 as offset for robustness.
        var offsetUnits = (packed >> 7) & 0xFFFFFFFFUL;
        var sampleOffset = (long)offsetUnits * 16L; // FSB5 offset scale = 16 bytes
        sampleOffsets.Add(sampleOffset);

        headerCursor += 8;

        // Skip chunk extensions if the first flag bit is set.
        var hasChunks = (packed & 0x1UL) != 0;
        while (hasChunks && headerCursor + 4 <= headersEnd) {
          var chunk = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(headerCursor));
          headerCursor += 4;
          var chunkSize = (int)((chunk >> 7) & 0xFFFFFF);
          hasChunks = (chunk & 0x1U) != 0;
          if (chunkSize < 0 || headerCursor + chunkSize > headersEnd) break;
          headerCursor += chunkSize;
        }
      }

      // Parse name table. Format: numSamples * uint32 offsets (relative to nameTable start),
      // then null-terminated strings at those offsets.
      var names = new string?[numSamples];
      if (nameTableSize >= numSamples * 4) {
        for (var i = 0; i < numSamples; ++i) {
          var nameOffsetPtr = (int)namesStart + i * 4;
          if (nameOffsetPtr + 4 > namesEnd) break;
          var nameOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(nameOffsetPtr));
          var strStart = (int)namesStart + nameOff;
          if (strStart < 0 || strStart >= namesEnd) continue;
          var end = strStart;
          while (end < namesEnd && blob[end] != 0) ++end;
          if (end > strStart)
            names[i] = Encoding.UTF8.GetString(blob.AsSpan(strStart, end - strStart));
        }
      }

      // Emit sample blobs.
      for (var i = 0; i < sampleOffsets.Count; ++i) {
        var start = dataStart + sampleOffsets[i];
        var end = i + 1 < sampleOffsets.Count ? dataStart + sampleOffsets[i + 1] : dataEnd;
        if (start < 0 || start >= dataEnd || end <= start) continue;
        end = Math.Min(end, dataEnd);
        var len = (int)(end - start);
        if (len <= 0) continue;
        var bytes = blob.AsSpan((int)start, len).ToArray();
        var baseName = SanitizeName(names[i]) ?? $"sample_{i:D4}";
        entries.Add(($"samples/{baseName}{sampleExt}", "Track", bytes));
      }

      // Metadata INI.
      var ini = new StringBuilder();
      ini.AppendLine("; FMOD Sample Bank (FSB5) metadata");
      ini.Append("version=").Append(version.ToString(CultureInfo.InvariantCulture)).AppendLine();
      ini.Append("num_samples=").Append(numSamples.ToString(CultureInfo.InvariantCulture)).AppendLine();
      ini.Append("mode=").Append(mode.ToString(CultureInfo.InvariantCulture)).AppendLine();
      ini.Append("mode_name=").AppendLine(ModeName(mode));
      ini.Append("sample_headers_size=").Append(sampleHeadersSize.ToString(CultureInfo.InvariantCulture)).AppendLine();
      ini.Append("name_table_size=").Append(nameTableSize.ToString(CultureInfo.InvariantCulture)).AppendLine();
      ini.Append("data_size=").Append(dataSize.ToString(CultureInfo.InvariantCulture)).AppendLine();
      entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));
    } catch {
      // Any parse failure → keep FULL.fsb only; do not throw.
    }

    return entries;
  }

  private static string ModeName(uint mode) => mode switch {
    1 => "PCM8", 2 => "PCM16", 3 => "PCM24", 4 => "PCM32", 5 => "PCMFLOAT",
    6 => "GCADPCM", 7 => "IMAADPCM", 8 => "VAG", 9 => "HEVAG", 10 => "XMA",
    11 => "MPEG", 12 => "CELT", 13 => "AT9", 14 => "XWMA", 15 => "Vorbis",
    16 => "FADPCM", 17 => "Opus",
    _ => "Unknown",
  };

  private static string ExtensionForMode(uint mode) => mode switch {
    1 or 2 or 3 or 4 or 5 => ".pcm",
    6 or 7 or 8 or 9 or 16 => ".adpcm",
    10 => ".xma",
    11 => ".mp3",
    12 => ".celt",
    13 => ".at9",
    14 => ".xwma",
    15 => ".ogg",
    17 => ".opus",
    _ => ".bin",
  };

  private static string? SanitizeName(string? name) {
    if (string.IsNullOrEmpty(name)) return null;
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
    return sb.Length > 0 ? sb.ToString() : null;
  }
}
