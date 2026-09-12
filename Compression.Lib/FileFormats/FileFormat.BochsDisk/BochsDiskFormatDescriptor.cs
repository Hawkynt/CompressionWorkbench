#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.BochsDisk;

/// <summary>
/// Bochs "Redolog" growing / undoable disk image. The 512-byte header begins with
/// the 32-byte ASCII magic <c>"Bochs Virtual HD Image"</c> (NUL-padded), a 16-byte
/// type field (<c>"Redolog"</c>), a 16-byte subtype (<c>"Growing"</c>,
/// <c>"Undoable"</c> or <c>"Volatile"</c>), then a header struct: version (u32 BE),
/// catalog entry count (u32 BE), bitmap bytes per entry (u32 BE), extent bytes per
/// entry (u32 BE) and — for header version 0x00020000 — the virtual disk size
/// (u64 BE). The catalog (one u32 BE per extent, <c>0xFFFFFFFF</c> = unallocated)
/// follows, and each allocated extent stores a bitmap immediately ahead of its
/// data slab.
///
/// <para>This descriptor surfaces <c>FULL</c> verbatim, a <c>metadata.ini</c>
/// (type, subtype, geometry, virtual size) and a reconstructed <c>disk.raw</c>
/// built by walking the catalog and copying each allocated extent's data into the
/// flat image (unallocated extents read back as zero). Read-only; malformed
/// headers degrade to FULL + partial metadata.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/bochs-emu/Bochs</c> — canonical Bochs source — <c>iodev/hdimage/hdimage.h</c> defines the redolog header</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Bochs</c> — background</description></item>
/// </list>
/// </summary>
public sealed class BochsDiskFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BochsDisk";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Bochs Redolog Disk";
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
  public string DefaultExtension => ".redolog";
  // Generic extensions (.img) would collide; rely on the strong leading magic.
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".redolog"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("Bochs Virtual HD Image"u8.ToArray(), Confidence: 0.97),
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
    "Bochs Redolog growing/undoable disk: catalog + per-extent bitmap; reconstructs disk.raw read-only.";

  private const int HeaderMagicLen = 32;
  private const int TypeLen = 16;
  private const int SubtypeLen = 16;

  private sealed record BochsHeader(
    string Type,
    string Subtype,
    uint Version,
    uint CatalogEntries,
    uint BitmapBytes,
    uint ExtentBytes,
    ulong DiskSize,
    long CatalogOffset,
    bool Valid);

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.redolog", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };

    // Route the reconstructed guest disk through the partition-aware lister so
    // its MBR/GPT/APM partitions and inner filesystems browse — exactly like the
    // VHD descriptor. The lazy catalog-backed stream avoids materialising the disk.
    var guest = BochsGuestDiskStream.TryOpen(stream);
    if (guest != null) {
      using (guest) {
        entries.Add(new ArchiveEntryInfo(entries.Count, "disk.raw", guest.Length, guest.Length,
          "Stored", false, false, null));
        try {
          guest.Position = 0;
          if (PartitionedDiskLister.List(guest, password) is { } partitioned)
            foreach (var e in partitioned)
              entries.Add(e with { Index = entries.Count });
        } catch {
          // Partition/inner-FS enumeration failed — the raw views above still stand.
        }
      }
    }
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Wants(files, "FULL.redolog"))
      WriteFile(outputDir, "FULL.redolog", ReadAll(stream));

    var h = TryReadHeader(stream);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(h)));

    var guest = BochsGuestDiskStream.TryOpen(stream);
    if (guest != null) {
      using (guest) {
        // Stream the reconstructed disk straight to disk.raw — no 2 GiB byte[] cap.
        if (Wants(files, "disk.raw")) {
          Directory.CreateDirectory(outputDir);
          using var raw = File.Create(Path.Combine(outputDir, "disk.raw"));
          guest.Position = 0;
          guest.CopyTo(raw);
        }

        // Partition-aware extraction into PartitionN_/ subdirectories.
        try {
          guest.Position = 0;
          PartitionedDiskLister.Extract(guest, outputDir, password, files);
        } catch {
          // best effort — the raw views above are already written
        }
      }
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static BochsHeader TryReadHeader(Stream stream) {
    try {
      if (!stream.CanSeek) return Invalid();
      stream.Position = 0;
      // Magic + type + subtype + header struct (up to disk size for v2).
      Span<byte> head = stackalloc byte[HeaderMagicLen + TypeLen + SubtypeLen + 24];
      if (!TryReadExact(stream, head)) return Invalid();

      var magic = AsciiZ(head[..HeaderMagicLen]);
      if (!magic.StartsWith("Bochs Virtual HD Image", StringComparison.Ordinal))
        return Invalid();

      var type = AsciiZ(head.Slice(HeaderMagicLen, TypeLen));
      var subtype = AsciiZ(head.Slice(HeaderMagicLen + TypeLen, SubtypeLen));
      var p = HeaderMagicLen + TypeLen + SubtypeLen;
      var version = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(p, 4));
      var catalog = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(p + 4, 4));
      var bitmap = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(p + 8, 4));
      var extent = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(p + 12, 4));
      ulong diskSize = 0;
      if (version >= 0x00020000)
        diskSize = BinaryPrimitives.ReadUInt64BigEndian(head.Slice(p + 16, 8));
      else if (extent > 0)
        diskSize = (ulong)catalog * extent;

      // The catalog sits at a 512-byte aligned boundary after the header.
      const long catalogOffset = 512;

      return new BochsHeader(type, subtype, version, catalog, bitmap, extent, diskSize,
        catalogOffset, Valid: true);
    } catch {
      return Invalid();
    }
  }

  private static BochsHeader Invalid()
    => new(string.Empty, string.Empty, 0, 0, 0, 0, 0, 0, Valid: false);

  private static string BuildMetadataIni(BochsHeader h) {
    var sb = new StringBuilder();
    sb.Append("[BochsDisk]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(h.Valid ? 1 : 0)}\n");
    if (!h.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"type={h.Type}\n");
    sb.Append(CultureInfo.InvariantCulture, $"subtype={h.Subtype}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version=0x{h.Version:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"catalog_entries={h.CatalogEntries}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bitmap_bytes={h.BitmapBytes}\n");
    sb.Append(CultureInfo.InvariantCulture, $"extent_bytes={h.ExtentBytes}\n");
    sb.Append(CultureInfo.InvariantCulture, $"disk_size={h.DiskSize}\n");
    sb.Append("parse_status=ok\n");
    return sb.ToString();
  }

  private static string AsciiZ(ReadOnlySpan<byte> span) {
    var end = span.IndexOf((byte)0);
    if (end < 0) end = span.Length;
    return Encoding.ASCII.GetString(span[..end]).Trim();
  }

  private static bool TryReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
