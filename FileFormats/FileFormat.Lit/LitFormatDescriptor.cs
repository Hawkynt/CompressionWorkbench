#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lit;

/// <summary>
/// Microsoft Reader eBook (<c>.lit</c>). The archive view surfaces the raw
/// container structure: <c>FULL.lit</c> (passthrough), <c>metadata.ini</c>
/// (version, header length, offsets) and <c>directory.bin</c> (the raw
/// directory chunk bytes starting at the container's data offset).
/// <para>
/// Scope cut: the payload is LZX-compressed; this descriptor does NOT decode
/// LZX blocks. It stops at structural surfacing, which is enough to confirm
/// format + extract header/directory bytes for forensic triage.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/Microsoft_Reader</c> — Wikipedia — LIT / Microsoft Reader background</description></item>
///   <item><description>ConvertLIT ("clit") — the community tool whose source documents the ITOL/ITLS container and DRM levels</description></item>
/// </list>
/// </summary>
public sealed class LitFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Lit";
  public string DisplayName => "LIT (Microsoft Reader)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lit";
  public IReadOnlyList<string> Extensions => [".lit"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Header: "ITOL" + "ITLS" at offset 0.
    new("ITOLITLS"u8.ToArray(), Offset: 0, Confidence: 0.99),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzx", "LZX")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft Reader LIT eBook; structural surfacing only (no LZX decode).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Each entry's
  /// decoded byte buffer is produced by <see cref="BuildEntries"/> and
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    foreach (var e in BuildEntries(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

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
      ("FULL.lit", "Container", blob),
    };

    // LIT header: magic[8] + version u32 LE + header_len u32 LE + unknown u32 LE
    // + header_len_dir u32 LE + data_offset u32 LE + aOffset u32 LE + bOffset u32 LE
    // + mask u8. Minimum 33 bytes for a well-formed header.
    if (blob.Length < 33) return entries;
    var magic = Encoding.ASCII.GetString(blob, 0, 8);
    if (magic != "ITOLITLS") return entries;

    var version = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8));
    var headerLen = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12));
    var unknown = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(16));
    var headerLenDir = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(20));
    var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(24));
    var aOffset = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(28));
    var bOffset = blob.Length >= 36 ? BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(32)) : 0u;
    var mask = blob.Length >= 37 ? blob[36] : (byte)0;

    var ini = new StringBuilder();
    ini.AppendLine("; Microsoft Reader LIT header");
    ini.Append("magic=").AppendLine(magic);
    ini.Append("version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    ini.Append("header_len=").AppendLine(headerLen.ToString(CultureInfo.InvariantCulture));
    ini.Append("unknown=").AppendLine(unknown.ToString(CultureInfo.InvariantCulture));
    ini.Append("header_len_dir=").AppendLine(headerLenDir.ToString(CultureInfo.InvariantCulture));
    ini.Append("data_offset=").AppendLine(dataOffset.ToString(CultureInfo.InvariantCulture));
    ini.Append("a_offset=").AppendLine(aOffset.ToString(CultureInfo.InvariantCulture));
    ini.Append("b_offset=").AppendLine(bOffset.ToString(CultureInfo.InvariantCulture));
    ini.Append("mask=").AppendLine(mask.ToString(CultureInfo.InvariantCulture));
    ini.Append("file_size=").AppendLine(blob.Length.ToString(CultureInfo.InvariantCulture));
    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));

    // Surface the directory bytes if the offset/length are sane.
    var dirStart = (long)dataOffset;
    var dirEnd = dirStart + headerLenDir;
    if (dirStart >= 0 && dirStart < blob.Length && dirEnd > dirStart && dirEnd <= blob.Length) {
      var dir = blob.AsSpan((int)dirStart, (int)(dirEnd - dirStart)).ToArray();
      entries.Add(("directory.bin", "Track", dir));
    }

    return entries;
  }
}
