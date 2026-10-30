#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.C64Tap;

/// <summary>
/// Commodore raw tape image (<c>C64-TAPE-RAW</c> / <c>C16-TAPE-RAW</c>, the ".tap" written by VICE
/// and tape-capture tools): a 20-byte header (version 0/1/2, platform, video standard, data size)
/// and a stream of pulse bytes — each the pulse length in 8-cycle units, with 0 introducing a
/// 24-bit cycle count in versions 1 and 2. Lists <c>metadata.ini</c> (header fields plus pulse
/// statistics) and the raw <c>pulses.bin</c>; creates and edits images from those parts.
/// Extension-only detection is impossible (".tap" belongs to the ZX Spectrum TAP), so the magic
/// identifies it.
/// </summary>
public sealed class C64TapFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {
  public const string PulsesName = "pulses.bin";
  public const string MetadataName = "metadata.ini";
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName };

  public string Id => "C64Tap";
  public string DisplayName => "Commodore raw tape (C64-TAPE-RAW)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tap";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("C64-TAPE-RAW"u8.ToArray(), Confidence: 0.98),
    new("C16-TAPE-RAW"u8.ToArray(), Confidence: 0.98),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pulses", "Pulse lengths")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 8-bit raw tape pulse image (C64/VIC-20/C16/PET, versions 0–2)";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  public sealed record Header(bool C16, byte Version, byte Platform, byte Video);

  /// <exception cref="InvalidDataException">No signature, unknown version or a size that disagrees with the file.</exception>
  public static (Header Header, byte[] Pulses) Read(ReadOnlySpan<byte> d) {
    if (d.Length < 20) throw new InvalidDataException("C64 TAP: shorter than the 20-byte header.");
    var c16 = d[..12].SequenceEqual("C16-TAPE-RAW"u8);
    if (!c16 && !d[..12].SequenceEqual("C64-TAPE-RAW"u8)) throw new InvalidDataException("C64 TAP: signature missing.");
    if (d[12] > 2) throw new InvalidDataException($"C64 TAP: unknown version {d[12]}.");
    var size = BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
    if (size > d.Length - 20) throw new InvalidDataException("C64 TAP: data size runs past the end of the file.");
    return (new Header(c16, d[12], d[13], d[14]), d.Slice(20, (int)size).ToArray());
  }

  public static void Write(Stream output, Header header, ReadOnlySpan<byte> pulses) {
    var h = new byte[20];
    (header.C16 ? "C16-TAPE-RAW"u8 : "C64-TAPE-RAW"u8).CopyTo(h);
    h[12] = header.Version;
    h[13] = header.Platform;
    h[14] = header.Video;
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), (uint)pulses.Length);
    output.Write(h);
    output.Write(pulses);
  }

  /// <summary>Pulse durations in CPU cycles (a version-0 zero is an unknown long pulse, reported as 256 × 8).</summary>
  public static List<long> Durations(Header header, ReadOnlySpan<byte> pulses) {
    var list = new List<long>();
    for (var i = 0; i < pulses.Length; ++i) {
      var b = pulses[i];
      if (b != 0) { list.Add(b * 8L); continue; }
      if (header.Version == 0 || i + 3 >= pulses.Length) { list.Add(256 * 8); continue; }   // overflow / truncated tail
      list.Add(pulses[i + 1] | pulses[i + 2] << 8 | pulses[i + 3] << 16);
      i += 3;
    }
    return list;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var (header, pulses) = Read(All(stream));
      var meta = Metadata(header, pulses);
      List<ArchiveEntryInfo> result = [new(0, MetadataName, meta.Length, meta.Length, "stored", false, false, null)];
      // An empty tape holds no pulse stream to list.
      if (pulses.Length > 0) result.Add(new(1, PulsesName, pulses.Length, pulses.Length, "pulses", false, false, null));
      return result;
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.tap", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (header, pulses) = Read(All(stream));
    if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files)) WriteFile(outputDir, MetadataName, Metadata(header, pulses));
    if (pulses.Length > 0 && (files is not { Length: > 0 } || MatchesFilter(PulsesName, files))) WriteFile(outputDir, PulsesName, pulses);
  }

  /// <summary><c>pulses.bin</c> (or any single file, taken as the pulse stream) plus optional <c>metadata.ini</c>.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var (header, pulses) = Collect(new Header(false, 1, 0, 0), [], inputs);
    Write(output, header, pulses);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var (header, pulses) = Read(All(archive));
    var (h, p) = Collect(header, pulses, inputs);
    WholeImageRebuildCommitter.Replace(archive, s => Write(s, h, p), s => Read(All(s)));
  }

  /// <summary>Removing <c>pulses.bin</c> leaves an empty tape; the header is a rendering.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var (header, pulses) = Read(All(archive));
    foreach (var raw in entryNames) {
      var leaf = Path.GetFileName(raw.Replace('\\', '/'));
      if (leaf.Equals(PulsesName, StringComparison.OrdinalIgnoreCase)) pulses = [];
      else if (!Synthetic.Contains(leaf)) throw new NotSupportedException($"C64 TAP has no entry '{raw}'.");
    }
    WholeImageRebuildCommitter.Replace(archive, s => Write(s, header, pulses), s => Read(All(s)));
  }

  private static (Header, byte[]) Collect(Header header, byte[] pulses, IReadOnlyList<ArchiveInputInfo> inputs) {
    byte[]? newPulses = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      var leaf = Path.GetFileName(input.ArchiveName.Replace('\\', '/'));
      if (leaf.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) {
        header = Apply(header, Encoding.UTF8.GetString(input.ReadContent()));
        continue;
      }
      if (newPulses is not null) throw new ArgumentException("A C64 TAP holds one pulse stream.", nameof(inputs));
      newPulses = input.ReadContent();
    }
    return (header, newPulses ?? pulses);
  }

  private static Header Apply(Header header, string ini) {
    foreach (var line in ini.Split('\n')) {
      var l = line.Trim();
      var eq = l.IndexOf('=');
      if (eq <= 0 || l.StartsWith('[')) continue;
      var key = l[..eq].Trim().ToLowerInvariant();
      var value = l[(eq + 1)..].Trim().Split(' ', 2)[0];
      switch (key) {
        case "signature": header = header with { C16 = value.StartsWith("C16", StringComparison.OrdinalIgnoreCase) }; break;
        case "version": header = header with { Version = byte.Parse(value, CultureInfo.InvariantCulture) }; break;
        case "platform": header = header with { Platform = byte.Parse(value, CultureInfo.InvariantCulture) }; break;
        case "video": header = header with { Video = byte.Parse(value, CultureInfo.InvariantCulture) }; break;
      }
    }
    if (header.Version > 2) throw new ArgumentException("C64 TAP versions are 0, 1 and 2.");
    return header;
  }

  private static byte[] Metadata(Header h, byte[] pulses) {
    var inv = CultureInfo.InvariantCulture;
    var platform = h.Platform switch { 0 => "C64", 1 => "VIC-20", 2 => "C16/Plus4", 3 => "PET", 4 => "C5x0", 5 => "C6x0/C7x0", _ => "?" };
    var video = h.Video switch { 0 => "PAL", 1 => "NTSC", 2 => "old NTSC", 3 => "PAL-N", _ => "?" };
    var durations = Durations(h, pulses);
    var sb = new StringBuilder("[c64tap]\n");
    sb.Append(inv, $"signature={(h.C16 ? "C16-TAPE-RAW" : "C64-TAPE-RAW")}\nversion={h.Version}\nplatform={h.Platform} ({platform})\nvideo={h.Video} ({video})\n");
    sb.Append(inv, $"pulse_bytes={pulses.Length}\npulses={durations.Count}\ntotal_cycles={durations.Sum()}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] All(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
