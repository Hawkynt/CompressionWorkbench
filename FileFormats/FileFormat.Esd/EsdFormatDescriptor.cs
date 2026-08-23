using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Wim;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Esd;

/// <summary>
/// Descriptor for the Microsoft <b>ESD</b> (Electronic Software Download) format —
/// the encrypted-CAB / install-image variant of WIM that the Windows Update service
/// streams down for OS provisioning.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview</c> — Microsoft's WIM/ESD overview</description></item>
///   <item><description>Microsoft "Windows Imaging File Format (WIM)" whitepaper — the on-disk header / resource-table spec ESD shares</description></item>
///   <item><description><c>https://wimlib.net</c> — open-source WIM/ESD implementation (LZMS, solid resources)</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// On disk an ESD file is structurally identical to a <see cref="WimFormatDescriptor"/>:
/// the 8-byte magic at offset 0 is <c>"MSWIM\0\0\0"</c> and the file is laid out as a
/// header + resource table + image-metadata + XML-data stream. The distinguishing
/// feature is that ESD typically uses LZMS compression and ships per-resource
/// encryption keys out of band; we cannot decrypt the encrypted resources without
/// those keys, but we can still parse the header, expose the XML manifest, and
/// surface whichever resources happen to be plaintext.
/// </para>
/// <para>
/// Detection is extension-only: at the magic level ESD is indistinguishable from a
/// regular WIM, so this descriptor declares no <see cref="MagicSignatures"/> and
/// relies on <c>.esd</c> to win over <c>.wim</c> via the file-extension lookup. Any
/// resource that fails to decode produces a <c>note</c> entry in
/// <c>metadata.ini</c> rather than aborting the whole listing.
/// </para>
/// </remarks>
public sealed class EsdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    if (archive.Length < WimConstants.HeaderSize)
      yield break;
    yield return new DefragBlockInfo(0, WimConstants.HeaderSize, DefragBlockKind.MetadataReserved, FileName: "ESD/WIM Header");
    WimReader r;
    try {
      archive.Position = 0;
      r = new WimReader(archive);
    } catch {
      yield break;
    }
    foreach (var res in r.Resources) {
      if (res.CompressedSize <= 0 || res.Offset < 0) continue;
      var kind = res.IsMetadata ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used;
      var label = res.IsMetadata ? "Metadata Resource" : "Data Resource";
      yield return new DefragBlockInfo(res.Offset, res.CompressedSize, kind, FileName: label);
    }
  }

  /// <inheritdoc/>
  public string Id => "Esd";

  /// <inheritdoc/>
  public string DisplayName => "ESD";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <inheritdoc/>
  public string DefaultExtension => ".esd";

  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".esd"];

  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  /// <remarks>
  /// Empty: ESD shares the WIM <c>"MSWIM\0\0\0"</c> magic. Detection is by
  /// extension to avoid first-match conflicts with the WIM descriptor.
  /// </remarks>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("wim", "WIM/ESD")];

  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  public string Description =>
    "Microsoft Electronic Software Download — encrypted-LZMS WIM variant used by " +
    "Windows Update for OS install images.";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = this.BuildEntries(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i,
      Name: e.Name,
      OriginalSize: e.Data.Length,
      CompressedSize: e.Data.Length,
      Method: e.Method,
      IsDirectory: false,
      IsEncrypted: false,
      LastModified: null)).ToList();
  }

  /// <inheritdoc/>
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
    foreach (var e in this.BuildEntries(archive)) {
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
    foreach (var e in this.BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Community-known ESD marker bit set in the WIM header flags field.
  /// Microsoft's <c>WIM_HDR_FLAG_ESD</c> bit, documented across community
  /// references on the encrypted-WIM variant. We don't perform encryption —
  /// resources are written as a plain WIM and the bit serves only to identify
  /// the produced file as belonging to the ESD sub-family.
  /// </summary>
  internal const uint FlagEsdMarker = 0x00100000u;

  /// <summary>
  /// Emits an ESD-flagged WIM file containing <paramref name="inputs"/> as
  /// uncompressed resources. The standard <see cref="WimWriter"/> is invoked
  /// with <see cref="WimConstants.CompressionNone"/>, after which we patch the
  /// header's flags field to set <see cref="FlagEsdMarker"/> so the produced
  /// file is recognisable as an ESD variant.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Real ESD files carry per-resource encryption keyed by the Microsoft Store
  /// install pipeline; we cannot reproduce that, so the marker is presentational
  /// only. The resulting file remains parseable by the standard
  /// <see cref="WimReader"/> and round-trips through this descriptor's
  /// <see cref="List"/> / <see cref="Extract"/> paths.
  /// </para>
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!output.CanSeek)
      throw new ArgumentException("ESD writing requires a seekable stream.", nameof(output));

    var headerStart = output.Position;

    // Phase 1 — emit a standard uncompressed WIM via the existing writer.
    var resources = FormatHelpers.FilesOnly(inputs).ToList();
    new WimWriter(output, WimConstants.CompressionNone).Write(resources);

    // Phase 2 — patch the header flags field at offset 16 to set the ESD marker.
    // WimWriter writes a 208-byte header at headerStart with flags at +16.
    var endPos = output.Position;
    output.Seek(headerStart + 16, SeekOrigin.Begin);
    Span<byte> existing = stackalloc byte[4];
    output.ReadExactly(existing);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(existing) | FlagEsdMarker;
    output.Seek(headerStart + 16, SeekOrigin.Begin);
    Span<byte> patched = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(patched, flags);
    output.Write(patched);
    output.Seek(endPos, SeekOrigin.Begin);
  }

  /// <summary>
  /// Pre-materialises the entries this descriptor surfaces for an ESD file:
  /// a <c>metadata.ini</c> summary, the XML manifest if present, and the
  /// raw bytes of every resource the underlying <see cref="WimReader"/> can
  /// decode. Encrypted/unsupported resources are skipped and noted in the
  /// metadata summary.
  /// </summary>
  private List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    WimHeader header;
    var resources = new List<WimResourceEntry>();
    byte[]? xml = null;
    var decoded = new List<(int Index, byte[] Data)>();
    var failedResources = new List<(int Index, string Reason)>();

    try {
      var reader = new WimReader(stream);
      header = reader.Header;
      resources = [.. reader.Resources];

      // Try to extract the XML manifest if the header points to one. The XML
      // resource lives outside the lookup table (it's a header pointer) and is
      // stored uncompressed by spec, so we read its bytes directly.
      xml = TryReadXmlManifest(stream, header);

      for (var i = 0; i < resources.Count; ++i) {
        var entry = resources[i];
        if (entry.IsMetadata)
          continue; // Surfaced separately below if XML is present
        try {
          var bytes = reader.ReadResource(i);
          decoded.Add((i, bytes));
        } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
          failedResources.Add((i, ex.Message));
        }
      }
    } catch (InvalidDataException) {
      throw; // Not a WIM/ESD at all — propagate so FormatDetector can fall back.
    }

    var result = new List<(string, byte[], string)> {
      ("metadata.ini", BuildMetadata(header, resources, decoded.Count, failedResources), "Tag"),
    };

    if (xml is { Length: > 0 })
      result.Add(("manifest.xml", xml, "Manifest"));

    foreach (var (idx, data) in decoded)
      result.Add(($"resource_{idx:D4}.bin", data, "Payload"));

    return result;
  }

  /// <summary>
  /// Reads the XML manifest pointed at by <see cref="WimHeader.XmlDataResource"/>.
  /// The XML resource is a header pointer (not part of the lookup table) and is
  /// stored uncompressed in WIM/ESD; we therefore seek directly and read the
  /// declared number of bytes. Returns <see langword="null"/> if absent or unreadable.
  /// </summary>
  private static byte[]? TryReadXmlManifest(Stream stream, WimHeader header) {
    var ptr = header.XmlDataResource;
    if (ptr is null || ptr.OriginalSize <= 0 || ptr.OriginalSize > int.MaxValue)
      return null;
    try {
      stream.Seek(ptr.Offset, SeekOrigin.Begin);
      var buf = new byte[(int)ptr.OriginalSize];
      stream.ReadExactly(buf);
      return buf;
    } catch (Exception ex) when (ex is IOException or EndOfStreamException) {
      _ = ex;
      return null;
    }
  }

  /// <summary>
  /// Builds a human-readable summary of the parsed ESD header plus a per-resource
  /// inventory. Every resource that failed to decode is listed with its reason so
  /// the caller can see what was skipped.
  /// </summary>
  private static byte[] BuildMetadata(
    WimHeader header,
    IReadOnlyList<WimResourceEntry> resources,
    int decodedCount,
    IReadOnlyList<(int Index, string Reason)> failed) {

    var sb = new StringBuilder();
    sb.AppendLine("[esd]");
    sb.Append("magic = MSWIM\\0\\0\\0").AppendLine();
    sb.Append("format_version = 0x")
      .Append(header.Version.ToString("X8", CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("compression_type = ").Append(CompressionName(header.CompressionType)).AppendLine();
    sb.Append("chunk_size = ").Append(header.ChunkSize.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("part_number = ").Append(header.PartNumber.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("total_parts = ").Append(header.TotalParts.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("image_count = ").Append(header.ImageCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("resource_count = ").Append(resources.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("decoded_resources = ").Append(decodedCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
    if (failed.Count > 0) {
      sb.Append("failed_resources = ").Append(failed.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
      sb.AppendLine("note = ESD encryption is not implemented; encrypted resources skipped");
      foreach (var (idx, reason) in failed)
        sb.Append("# resource_")
          .Append(idx.ToString("D4", CultureInfo.InvariantCulture))
          .Append(": ").AppendLine(reason);
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string CompressionName(uint type) => type switch {
    WimConstants.CompressionNone => "none",
    WimConstants.CompressionXpress => "xpress",
    WimConstants.CompressionLzx => "lzx",
    WimConstants.CompressionLzms => "lzms",
    WimConstants.CompressionXpressHuffman => "xpress-huffman",
    _ => $"unknown(0x{type:X8})",
  };
}
