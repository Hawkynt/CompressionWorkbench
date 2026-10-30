#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Hfe;

/// <summary>
/// HxC Floppy Emulator (HFE) image. 512-byte header beginning with the 8-byte
/// signature "HXCPICFE" (v1) or "HXCHFEV3" (v3): formatRevision(u8),
/// numberOfTracks(u8), numberOfSides(u8), trackEncoding(u8), bitRate(u16 LE),
/// floppyRPM(u16 LE), interfaceMode(u8), reserved(u8), trackListOffset(u16 LE, in
/// 512-byte blocks), then write-allowed / single-step flags. At
/// <c>trackListOffset*512</c> sits the track LUT: per track an offset(u16 LE, in
/// 512-byte blocks) and a length(u16 LE, in bytes). Each track block holds the raw
/// MFM/FM bitstream for both sides interleaved in 256-byte chunks.
///
/// <para>Surfaces <c>FULL.hfe</c> verbatim, a <c>metadata.ini</c> (tracks, sides,
/// encoding, bitrate, rpm, interface) and the raw per-track blocks under
/// <c>tracks/</c>. Read-only; malformed input degrades to FULL + partial metadata
/// without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://hxc2001.com/</c> — HxC Floppy Emulator project (Jean-François Del Nero) — publisher of the HFE file-format specification</description></item>
///   <item><description><c>https://github.com/jfdelnero/HxCFloppyEmulator</c> — canonical implementation and conversion tooling</description></item>
/// </list>
/// </summary>
public sealed partial class HfeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
    IArchiveModifiable, IArchiveWriteConstraints, ISyntheticEntryNames {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Hfe";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "HxC Floppy Emulator (HFE)";
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
  public string DefaultExtension => ".hfe";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".hfe"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("HXCPICFE"u8.ToArray(), Confidence: 0.95),
    new("HXCHFEV3"u8.ToArray(), Confidence: 0.95),
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
    "HxC Floppy Emulator (HFE) image: header + per-track raw bitstream blocks.";

  private static readonly string[] EncodingNames =
    ["ISOIBM_MFM", "AMIGA_MFM", "ISOIBM_FM", "EMU_FM", "UNKNOWN"];

  internal static string[] EncodingNamesForWriter => EncodingNames;

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FULL.hfe", "metadata.ini" };

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  [System.Text.RegularExpressions.GeneratedRegex(@"^(?:.*/)?track(\d{3})\.bin$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
  private static partial System.Text.RegularExpressions.Regex TrackName();

  /// <inheritdoc />
  public long? MaxTotalArchiveSize => null;

  /// <inheritdoc />
  public string AcceptedInputsDescription => "accepts: tracks/trackNNN.bin (side-interleaved track blocks), metadata.ini, or one whole FULL.hfe";

  /// <inheritdoc />
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = input.ArchiveName.Replace('\\', '/');
    var leaf = Path.GetFileName(name);
    if (TrackName().IsMatch(name) || leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase) || leaf.Equals("FULL.hfe", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"'{input.ArchiveName}' is not an HFE member; {this.AcceptedInputsDescription}";
    return false;
  }

  /// <summary>
  /// Builds an image from <c>tracks/trackNNN.bin</c> (consecutive from 000) and the header fields in
  /// <c>metadata.ini</c>; a lone <c>FULL.hfe</c> is taken as the whole image.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var (header, tracks, full) = this.Collect(new HfeWriter.Header(), [], inputs);
    if (full is not null && tracks.Count == 0) {
      if (!Parse(full, out _).Valid) throw new ArgumentException("FULL.hfe is not an HFE image.");
      output.Write(full);
      return;
    }
    HfeWriter.Write(output, header, Consecutive(tracks));
  }

  /// <summary>Replaces or appends tracks and header fields; the image is rebuilt with a fresh track list.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var data = ReadAll(archive);
    var info = Parse(data, out var existing);
    if (!info.Valid) throw new InvalidDataException("HFE: not a valid image.");
    var current = existing.ToDictionary(t => int.Parse(TrackName().Match(t.Name).Groups[1].Value, CultureInfo.InvariantCulture), t => t.Data);
    var (header, tracks, _) = this.Collect(HfeWriter.Header.Parse(data), current, inputs);
    var rebuilt = Consecutive(tracks);
    Compression.Registry.WholeImageRebuildCommitter.Replace(archive, s => HfeWriter.Write(s, header, rebuilt), s => {
      if (!Parse(ReadAll(s), out _).Valid) throw new InvalidDataException("HFE: rebuilt image does not parse.");
    });
  }

  /// <summary>Only trailing tracks can be removed: an HFE track list has no holes.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var data = ReadAll(archive);
    var info = Parse(data, out var existing);
    if (!info.Valid) throw new InvalidDataException("HFE: not a valid image.");
    var tracks = existing.ToDictionary(t => int.Parse(TrackName().Match(t.Name).Groups[1].Value, CultureInfo.InvariantCulture), t => t.Data);
    foreach (var raw in entryNames) {
      var m = TrackName().Match(raw.Replace('\\', '/'));
      if (m.Success) tracks.Remove(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
      else if (!Synthetic.Contains(Path.GetFileName(raw))) throw new NotSupportedException($"HFE has no entry '{raw}'.");
    }
    if (tracks.Count == 0) throw new NotSupportedException("HFE: an image needs at least one track.");
    var rebuilt = Consecutive(tracks);
    var header = HfeWriter.Header.Parse(data);
    Compression.Registry.WholeImageRebuildCommitter.Replace(archive, s => HfeWriter.Write(s, header, rebuilt), null);
  }

  /// <summary>An HFE image has no empty instance (the track count is at least one).</summary>
  public bool CanPurgeToEmpty => false;

  private (HfeWriter.Header Header, Dictionary<int, byte[]> Tracks, byte[]? Full) Collect(
      HfeWriter.Header header, Dictionary<int, byte[]> tracks, IReadOnlyList<ArchiveInputInfo> inputs) {
    byte[]? full = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (!this.CanAccept(input, out var reason)) throw new ArgumentException(reason);
      var name = input.ArchiveName.Replace('\\', '/');
      var leaf = Path.GetFileName(name);
      if (leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)) header = header.With(input.ReadContent());
      else if (leaf.Equals("FULL.hfe", StringComparison.OrdinalIgnoreCase)) full = input.ReadContent();
      else tracks[int.Parse(TrackName().Match(name).Groups[1].Value, CultureInfo.InvariantCulture)] = input.ReadContent();
    }
    return (header, tracks, full);
  }

  private static List<byte[]> Consecutive(Dictionary<int, byte[]> tracks) {
    var ordered = tracks.OrderBy(kv => kv.Key).ToList();
    for (var i = 0; i < ordered.Count; ++i)
      if (ordered[i].Key != i)
        throw new ArgumentException($"HFE tracks must be consecutive from track000; track{i:D3}.bin is missing.");
    return ordered.Select(kv => kv.Value).ToList();
  }

  private sealed record HfeInfo(
    int Version,
    byte FormatRevision,
    byte NumberOfTracks,
    byte NumberOfSides,
    byte TrackEncoding,
    int BitRate,
    int FloppyRpm,
    byte InterfaceMode,
    int TrackListOffset,
    bool Valid,
    byte WriteAllowed = 0xFF,
    byte SingleStep = 0xFF,
    byte Track0Side0Alt = 0xFF,
    byte Track0Side0Enc = 0xFF,
    byte Track0Side1Alt = 0xFF,
    byte Track0Side1Enc = 0xFF);

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var data = ReadAll(stream);
    var info = Parse(data, out var tracks);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.hfe", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };
    if (info.Valid) {
      var idx = 2;
      foreach (var (name, bytes) in tracks)
        entries.Add(new ArchiveEntryInfo(idx++, name, bytes.Length, bytes.Length, "Stored", false, false, null, "Track"));
    }
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (Wants(files, "FULL.hfe"))
      WriteFile(outputDir, "FULL.hfe", data);

    var info = Parse(data, out var tracks);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(info)));

    if (info.Valid) {
      foreach (var (name, bytes) in tracks) {
        if (Wants(files, name))
          WriteFile(outputDir, name, bytes);
      }
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static HfeInfo Parse(byte[] data, out List<(string Name, byte[] Data)> tracks) {
    tracks = [];
    try {
      if (data.Length < 512) return Invalid();
      var sig = Encoding.ASCII.GetString(data, 0, 8);
      int version;
      if (sig == "HXCPICFE") version = 1;
      else if (sig == "HXCHFEV3") version = 3;
      else return Invalid();

      var formatRevision = data[8];
      var numTracks = data[9];
      var numSides = data[10];
      var trackEncoding = data[11];
      var bitRate = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12, 2));
      var rpm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14, 2));
      var interfaceMode = data[16];
      var trackListOffsetBlocks = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18, 2));

      var info = new HfeInfo(version, formatRevision, numTracks, numSides, trackEncoding,
        bitRate, rpm, interfaceMode, trackListOffsetBlocks, Valid: true,
        data[20], data[21], data[22], data[23], data[24], data[25]);

      var lutPos = trackListOffsetBlocks * 512;
      if (numTracks == 0 || numTracks > 200 || lutPos <= 0 || lutPos + numTracks * 4 > data.Length)
        return info; // header valid but track table out of range

      for (var t = 0; t < numTracks; ++t) {
        var entryPos = lutPos + t * 4;
        var offsetBlocks = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entryPos, 2));
        var lengthBytes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entryPos + 2, 2));
        var trackPos = offsetBlocks * 512;
        if (trackPos < 0 || lengthBytes <= 0 || (long)trackPos + lengthBytes > data.Length)
          continue;
        var block = new byte[lengthBytes];
        Array.Copy(data, trackPos, block, 0, lengthBytes);
        var name = string.Create(CultureInfo.InvariantCulture, $"tracks/track{t:D3}.bin");
        tracks.Add((name, block));
      }
      return info;
    } catch {
      return Invalid();
    }

    static HfeInfo Invalid() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, Valid: false);
  }

  private static string BuildMetadataIni(HfeInfo info) {
    var sb = new StringBuilder();
    sb.Append("[Hfe]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(info.Valid ? 1 : 0)}\n");
    if (!info.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"version={info.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"format_revision={info.FormatRevision}\n");
    sb.Append(CultureInfo.InvariantCulture, $"tracks={info.NumberOfTracks}\n");
    sb.Append(CultureInfo.InvariantCulture, $"sides={info.NumberOfSides}\n");
    var enc = info.TrackEncoding < EncodingNames.Length ? EncodingNames[info.TrackEncoding] : $"0x{info.TrackEncoding:X2}";
    sb.Append(CultureInfo.InvariantCulture, $"track_encoding={enc}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bit_rate_kbps={info.BitRate}\n");
    sb.Append(CultureInfo.InvariantCulture, $"floppy_rpm={info.FloppyRpm}\n");
    sb.Append(CultureInfo.InvariantCulture, $"interface_mode=0x{info.InterfaceMode:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"write_allowed=0x{info.WriteAllowed:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"single_step=0x{info.SingleStep:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"track0_side0_alt_encoding=0x{info.Track0Side0Alt:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"track0_side0_encoding=0x{info.Track0Side0Enc:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"track0_side1_alt_encoding=0x{info.Track0Side1Alt:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"track0_side1_encoding=0x{info.Track0Side1Enc:X2}\n");
    sb.Append("parse_status=ok\n");
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
