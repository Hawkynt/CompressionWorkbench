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
public sealed partial class WozFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
    IArchiveModifiable, IArchiveWriteConstraints, ISyntheticEntryNames {
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
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries;
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
    bool Valid) {
    public byte[]? InfoRaw { get; init; }
    public byte[]? TmapRaw { get; init; }
    public Dictionary<int, uint> BitCounts { get; init; } = [];
    public string? MetaRaw => this.Meta;
  }

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
    if (info.Valid && info.InfoRaw != null)
      entries.Add(new ArchiveEntryInfo(idx++, "info.bin", info.InfoRaw.Length, info.InfoRaw.Length, "Stored", false, false, null));
    if (info.Valid && info.TmapRaw != null)
      entries.Add(new ArchiveEntryInfo(idx++, "tmap.bin", info.TmapRaw.Length, info.TmapRaw.Length, "Stored", false, false, null));
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
    if (info.Valid && info.InfoRaw != null && Wants(files, "info.bin"))
      WriteFile(outputDir, "info.bin", info.InfoRaw);
    if (info.Valid && info.TmapRaw != null && Wants(files, "tmap.bin"))
      WriteFile(outputDir, "tmap.bin", info.TmapRaw);

    if (info.Valid) {
      foreach (var (name, bytes) in info.Tracks) {
        if (Wants(files, name))
          WriteFile(outputDir, name, bytes);
      }
    }
  }

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FULL.woz", "metadata.ini" };

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  [System.Text.RegularExpressions.GeneratedRegex(@"^(?:.*/)?track(\d{3})\.bin$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
  private static partial System.Text.RegularExpressions.Regex TrackName();

  /// <inheritdoc />
  public long? MaxTotalArchiveSize => null;

  /// <inheritdoc />
  public string AcceptedInputsDescription => "accepts: tracks/trackNNN.bin (bitstreams), info.bin, tmap.bin, meta.ini, metadata.ini (track_NNN_bits), or one whole FULL.woz";

  /// <inheritdoc />
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = input.ArchiveName.Replace('\\', '/');
    var leaf = Path.GetFileName(name);
    if (TrackName().IsMatch(name) || leaf.ToLowerInvariant() is "info.bin" or "tmap.bin" or "meta.ini" or "metadata.ini" or "full.woz") {
      reason = null;
      return true;
    }
    reason = $"'{input.ArchiveName}' is not a WOZ member; {this.AcceptedInputsDescription}";
    return false;
  }

  /// <summary>
  /// Writes a WOZ 2 image from the members this reader lists. Missing INFO/TMAP get defaults (a
  /// 5.25" disk, whole tracks at quarter-tracks 4t−1…4t+1); a track's bit count comes from
  /// <c>metadata.ini</c> (<c>track_NNN_bits</c>) or is 8 × its length. A lone FULL.woz is taken whole.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var parts = this.Collect(new Parts(), inputs);
    if (parts.Full is not null && parts.Tracks.Count == 0) {
      if (!Parse(parts.Full).Valid) throw new ArgumentException("FULL.woz is not a WOZ image.");
      output.Write(parts.Full);
      return;
    }
    Emit(output, parts);
  }

  /// <summary>Replaces or adds tracks, INFO, TMAP, META and bit counts; the image is rebuilt as WOZ 2.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var parts = this.Collect(Existing(archive), inputs);
    Compression.Registry.WholeImageRebuildCommitter.Replace(archive, s => Emit(s, parts), Verify);
  }

  /// <summary>Removes tracks (their TMAP slots are cleared) or the META chunk.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var parts = Existing(archive);
    foreach (var raw in entryNames) {
      var name = raw.Replace('\\', '/');
      var m = TrackName().Match(name);
      if (m.Success) {
        var t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        parts.Tracks.Remove(t);
        parts.BitCounts.Remove(t);
        if (parts.Tmap is not null)
          for (var q = 0; q < parts.Tmap.Length; ++q) if (parts.Tmap[q] == t) parts.Tmap[q] = 0xFF;
      } else if (Path.GetFileName(name).Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) parts.Meta = null;
      else if (!Synthetic.Contains(Path.GetFileName(name))) throw new NotSupportedException($"WOZ: '{raw}' cannot be removed.");
    }
    Compression.Registry.WholeImageRebuildCommitter.Replace(archive, s => Emit(s, parts), Verify);
  }

  private sealed class Parts {
    public byte[]? Info;
    public byte[]? Tmap;
    public string? Meta;
    public byte[]? Full;
    public Dictionary<int, byte[]> Tracks = [];
    public Dictionary<int, uint> BitCounts = [];
  }

  private static Parts Existing(Stream archive) {
    var data = ReadAll(archive);
    var info = Parse(data);
    if (!info.Valid) throw new InvalidDataException("WOZ: not a valid image.");
    var parts = new Parts { Info = UpgradeInfo(info.InfoRaw), Tmap = info.TmapRaw?.ToArray(), Meta = info.Meta };
    foreach (var (name, bytes) in info.Tracks) {
      var t = int.Parse(TrackName().Match(name).Groups[1].Value, CultureInfo.InvariantCulture);
      parts.Tracks[t] = bytes;
      if (info.BitCounts.TryGetValue(t, out var bits)) parts.BitCounts[t] = bits;
    }
    return parts;
  }

  private static byte[]? UpgradeInfo(byte[]? raw) {
    if (raw is null) return null;
    var info = new byte[WozWriter.InfoSize];
    raw.AsSpan(0, Math.Min(raw.Length, info.Length)).CopyTo(info);
    if (info[0] < 2) {
      // WOZ 1 INFO stops after the creator; give the version-2 fields their neutral values.
      info[37] = 1;
      info[39] = (byte)(info[1] == 2 ? 16 : 32);
    }
    return info;
  }

  private Parts Collect(Parts parts, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (!this.CanAccept(input, out var reason)) throw new ArgumentException(reason);
      var name = input.ArchiveName.Replace('\\', '/');
      var m = TrackName().Match(name);
      if (m.Success) {
        var t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        parts.Tracks[t] = input.ReadContent();
        parts.BitCounts.Remove(t);
        continue;
      }
      var content = input.ReadContent();
      switch (Path.GetFileName(name).ToLowerInvariant()) {
        case "info.bin": parts.Info = UpgradeInfo(content); break;
        case "tmap.bin": parts.Tmap = content; break;
        case "meta.ini": parts.Meta = FromMetaIni(Encoding.UTF8.GetString(content)); break;
        case "full.woz": parts.Full = content; break;
        case "metadata.ini":
          foreach (var line in Encoding.UTF8.GetString(content).Split('\n')) {
            var l = line.Trim();
            if (!l.StartsWith("track_", StringComparison.Ordinal) || !l.Contains("_bits=", StringComparison.Ordinal)) continue;
            var t = int.Parse(l.AsSpan(6, 3), CultureInfo.InvariantCulture);
            parts.BitCounts[t] = uint.Parse(l[(l.IndexOf('=') + 1)..], CultureInfo.InvariantCulture);
          }
          break;
      }
    }
    return parts;
  }

  private static void Emit(Stream output, Parts parts) {
    var tracks = parts.Tracks.ToDictionary(kv => kv.Key,
      kv => (kv.Value, parts.BitCounts.TryGetValue(kv.Key, out var b) ? b : (uint)kv.Value.Length * 8));
    WozWriter.Write(output, parts.Info ?? WozWriter.DefaultInfo(), parts.Tmap ?? WozWriter.DefaultTmap(parts.Tracks.Keys), tracks, parts.Meta);
  }

  private static void Verify(Stream candidate) {
    if (!Parse(ReadAll(candidate)).Valid) throw new InvalidDataException("WOZ: rebuilt image does not parse.");
  }

  /// <summary>meta.ini (<c>key=value</c>) back to the META chunk's tab-separated lines.</summary>
  private static string? FromMetaIni(string ini) {
    var lines = ini.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains('=') && !l.StartsWith('['))
      .Select(l => { var i = l.IndexOf('='); return l[..i].Trim() + "\t" + l[(i + 1)..].Trim(); }).ToList();
    return lines.Count == 0 ? null : string.Join("\n", lines) + "\n";
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
      byte[]? infoPayload = null;

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
            infoPayload = payload.ToArray();
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

      var bitCounts = new Dictionary<int, uint>();
      if (trksPayload != null)
        SplitTracks(version, trksPayload, tmapPayload, data, tracks, bitCounts);

      return new WozInfo(version, infoVersion, diskType, writeProtected, synchronized,
        meta, tracks, Valid: true) { InfoRaw = infoPayload, TmapRaw = tmapPayload, BitCounts = bitCounts };
    } catch {
      return Invalid(tracks);
    }

    static WozInfo Invalid(List<(string, byte[])> t) => new(0, 0, 0, false, false, null, t, Valid: false);
  }

  // WOZ1: TRKS holds 160 slots of 6656 bytes each; last 10 bytes hold byte/bit
  // counts. WOZ2: the TRKS payload starts with a 160-entry TRK table (startBlock
  // u16, blockCount u16, bitCount u32); startBlock is a *file-absolute* 512-byte
  // block, so bitstreams are resolved against the whole file buffer.
  private static void SplitTracks(int version, byte[] trks, byte[]? tmap, byte[] file, List<(string, byte[])> tracks, Dictionary<int, uint> bitCounts) {
    try {
      if (version == 1) {
        const int slot = 6656;
        var count = trks.Length / slot;
        for (var t = 0; t < count && t < 160; ++t) {
          if (IsEmptyTmapSlot(tmap, t)) continue;
          var bytesUsed = BinaryPrimitives.ReadUInt16LittleEndian(trks.AsSpan(t * slot + 6646, 2));
          var bitsUsed = BinaryPrimitives.ReadUInt16LittleEndian(trks.AsSpan(t * slot + 6648, 2));
          var len = bytesUsed > 0 && bytesUsed <= slot ? bytesUsed : slot;
          var block = new byte[len];
          Array.Copy(trks, t * slot, block, 0, len);
          tracks.Add((string.Create(CultureInfo.InvariantCulture, $"tracks/track{t:D3}.bin"), block));
          bitCounts[t] = bitsUsed > 0 ? bitsUsed : (uint)len * 8;
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
          bitCounts[t] = bitCount;
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
    foreach (var (t, bits) in info.BitCounts.OrderBy(kv => kv.Key))
      sb.Append(CultureInfo.InvariantCulture, $"track_{t:D3}_bits={bits}\n");
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
