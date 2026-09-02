#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Woz;

/// <summary>
/// Apple II WOZ disk image. 12-byte file header: signature "WOZ1" or "WOZ2",
/// then 0xFF 0x0A 0x0D 0x0A and a CRC32(u32 LE) over the remaining bytes. The body
/// is a sequence of chunks, each: 4-byte id + size(u32 LE) + payload. Known
/// chunks: INFO (version, disk type, write-protected, synchronized, …), TMAP
/// (track map), TRKS (track bitstreams), META (UTF-8 tab/newline key-value
/// metadata), WRIT.
///
/// <para>Surfaces <c>FULL.woz</c> verbatim, a <c>metadata.ini</c> distilled from
/// INFO, a <c>meta.ini</c> from the META key-value block (when present) and the
/// per-track TRKS blocks under <c>tracks/</c>. Read-only; malformed input degrades
/// to FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://applesaucefdc.com/woz/</c> — official WOZ disk image reference (Applesauce, John K. Morris)</description></item>
///   <item><description>WOZ 1.0 / 2.x reference documents published by the Applesauce project</description></item>
/// </list>
/// </summary>
public sealed class WozFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Woz";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Apple II WOZ";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".woz";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".woz"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x57, 0x4F, 0x5A, 0x31, 0xFF, 0x0A, 0x0D, 0x0A], Confidence: 0.97), // WOZ1
    new([0x57, 0x4F, 0x5A, 0x32, 0xFF, 0x0A, 0x0D, 0x0A], Confidence: 0.97), // WOZ2
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Apple II WOZ disk image: INFO/TMAP/TRKS/META chunks; per-track TRKS blocks surfaced.";

  private sealed record WozInfo(
    int Version,
    byte InfoVersion,
    byte DiskType,
    bool WriteProtected,
    bool Synchronized,
    string? Meta,
    List<(string Name, byte[] Data)> Tracks,
    bool Valid);

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var data = ReadAll(stream);
    var info = Parse(data);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.woz", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };
    var idx = 2;
    if (info.Valid && info.Meta != null)
      entries.Add(new ArchiveEntryInfo(idx++, "meta.ini", info.Meta.Length, info.Meta.Length, "Stored", false, false, null));
    if (info.Valid) {
      foreach (var (name, bytes) in info.Tracks)
        entries.Add(new ArchiveEntryInfo(idx++, name, bytes.Length, bytes.Length, "Stored", false, false, null, "Track"));
    }
    return entries;
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (Wants(files, "FULL.woz"))
      WriteFile(outputDir, "FULL.woz", data);

    var info = Parse(data);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(info)));

    if (info.Valid && info.Meta != null && Wants(files, "meta.ini"))
      WriteFile(outputDir, "meta.ini", Encoding.UTF8.GetBytes(BuildMetaIni(info.Meta)));

    if (info.Valid) {
      foreach (var (name, bytes) in info.Tracks) {
        if (Wants(files, name))
          WriteFile(outputDir, name, bytes);
      }
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static WozInfo Parse(byte[] data) {
    var tracks = new List<(string, byte[])>();
    try {
      if (data.Length < 12) return Invalid(tracks);
      int version;
      if (data[0] == 'W' && data[1] == 'O' && data[2] == 'Z' && data[3] == '1') version = 1;
      else if (data[0] == 'W' && data[1] == 'O' && data[2] == 'Z' && data[3] == '2') version = 2;
      else return Invalid(tracks);
      if (data[4] != 0xFF || data[5] != 0x0A || data[6] != 0x0D || data[7] != 0x0A)
        return Invalid(tracks);

      byte infoVersion = 0, diskType = 0;
      bool writeProtected = false, synchronized = false;
      string? meta = null;
      byte[]? trksPayload = null;
      byte[]? tmapPayload = null;

      var pos = 12;
      var guard = 0;
      while (pos + 8 <= data.Length && guard++ < 256) {
        var id = Encoding.ASCII.GetString(data, pos, 4);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
        pos += 8;
        if (size > int.MaxValue || pos + (long)size > data.Length) break;
        var payload = data.AsSpan(pos, (int)size);

        switch (id) {
          case "INFO":
            if (payload.Length >= 5) {
              infoVersion = payload[0];
              diskType = payload[1];
              writeProtected = payload[2] == 1;
              synchronized = payload[3] == 1;
            }
            break;
          case "META":
            meta = Encoding.UTF8.GetString(payload);
            break;
          case "TMAP":
            tmapPayload = payload.ToArray();
            break;
          case "TRKS":
            trksPayload = payload.ToArray();
            break;
        }
        pos += (int)size;
      }

      if (trksPayload != null)
        SplitTracks(version, trksPayload, tmapPayload, data, tracks);

      return new WozInfo(version, infoVersion, diskType, writeProtected, synchronized,
        meta, tracks, Valid: true);
    } catch {
      return Invalid(tracks);
    }

    static WozInfo Invalid(List<(string, byte[])> t) => new(0, 0, 0, false, false, null, t, Valid: false);
  }

  // WOZ1: TRKS holds 160 slots of 6656 bytes each; last 10 bytes hold byte/bit
  // counts. WOZ2: the TRKS payload starts with a 160-entry TRK table (startBlock
  // u16, blockCount u16, bitCount u32); startBlock is a *file-absolute* 512-byte
  // block, so bitstreams are resolved against the whole file buffer.
  private static void SplitTracks(int version, byte[] trks, byte[]? tmap, byte[] file, List<(string, byte[])> tracks) {
    try {
      if (version == 1) {
        const int slot = 6656;
        var count = trks.Length / slot;
        for (var t = 0; t < count && t < 160; ++t) {
          if (IsEmptyTmapSlot(tmap, t)) continue;
          var bytesUsed = BinaryPrimitives.ReadUInt16LittleEndian(trks.AsSpan(t * slot + 6648, 2));
          var len = bytesUsed > 0 && bytesUsed <= slot ? bytesUsed : slot;
          var block = new byte[len];
          Array.Copy(trks, t * slot, block, 0, len);
          tracks.Add((string.Create(CultureInfo.InvariantCulture, $"tracks/track{t:D3}.bin"), block));
        }
      } else {
        for (var t = 0; t < 160; ++t) {
          var entry = t * 8;
          if (entry + 8 > trks.Length) break;
          var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(trks.AsSpan(entry, 2));
          var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(trks.AsSpan(entry + 2, 2));
          var bitCount = BinaryPrimitives.ReadUInt32LittleEndian(trks.AsSpan(entry + 4, 4));
          if (startBlock == 0 || blockCount == 0 || bitCount == 0) continue;
          var byteLen = (int)((bitCount + 7) / 8);
          var fileOffset = startBlock * 512; // file-absolute
          if (fileOffset < 0 || (long)fileOffset + byteLen > file.Length) continue;
          var block = new byte[byteLen];
          Array.Copy(file, fileOffset, block, 0, byteLen);
          tracks.Add((string.Create(CultureInfo.InvariantCulture, $"tracks/track{t:D3}.bin"), block));
        }
      }
    } catch {
      // best-effort
    }
  }

  private static bool IsEmptyTmapSlot(byte[]? tmap, int track) {
    if (tmap == null || track >= tmap.Length) return false; // can't tell -> keep
    return tmap[track] == 0xFF;
  }

  private static string BuildMetadataIni(WozInfo info) {
    var sb = new StringBuilder();
    sb.Append("[Woz]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(info.Valid ? 1 : 0)}\n");
    if (!info.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"version={info.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"info_version={info.InfoVersion}\n");
    var diskTypeName = info.DiskType switch { 1 => "5.25", 2 => "3.5", _ => $"0x{info.DiskType:X2}" };
    sb.Append(CultureInfo.InvariantCulture, $"disk_type={diskTypeName}\n");
    sb.Append(CultureInfo.InvariantCulture, $"write_protected={(info.WriteProtected ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"synchronized={(info.Synchronized ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"track_count={info.Tracks.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_meta={(info.Meta != null ? 1 : 0)}\n");
    sb.Append("parse_status=ok\n");
    return sb.ToString();
  }

  // META block is tab-delimited key\tvalue pairs separated by newlines.
  private static string BuildMetaIni(string meta) {
    var sb = new StringBuilder();
    sb.Append("[Meta]\n");
    foreach (var line in meta.Split('\n')) {
      var trimmed = line.TrimEnd('\r');
      if (trimmed.Length == 0) continue;
      var tab = trimmed.IndexOf('\t');
      if (tab <= 0) continue;
      var key = trimmed[..tab].Trim();
      var value = trimmed[(tab + 1)..].Replace('\t', ' ').Trim();
      sb.Append(CultureInfo.InvariantCulture, $"{key}={value}\n");
    }
    return sb.ToString();
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
