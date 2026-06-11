#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
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
/// </summary>
public sealed class BochsDiskFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "BochsDisk";
  public string DisplayName => "Bochs Redolog Disk";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".redolog";
  // Generic extensions (.img) would collide; rely on the strong leading magic.
  public IReadOnlyList<string> Extensions => [".redolog"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("Bochs Virtual HD Image"u8.ToArray(), Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
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

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var h = TryReadHeader(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.redolog", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };
    if (h.Valid && h.DiskSize > 0 && h.DiskSize <= int.MaxValue)
      entries.Add(new ArchiveEntryInfo(2, "disk.raw", (long)h.DiskSize, (long)h.DiskSize, "Stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Wants(files, "FULL.redolog"))
      WriteFile(outputDir, "FULL.redolog", ReadAll(stream));

    var h = TryReadHeader(stream);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(h)));

    if (Wants(files, "disk.raw") && h.Valid && h.DiskSize > 0 && h.DiskSize <= int.MaxValue) {
      var disk = TryReconstruct(stream, h);
      if (disk != null)
        WriteFile(outputDir, "disk.raw", disk);
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

  // Walk the catalog: catalog[i] is the extent index in the file (not byte offset).
  // Each extent occupies (bitmap + extent) bytes starting after the catalog;
  // the data slab follows its bitmap.
  private static byte[]? TryReconstruct(Stream stream, BochsHeader h) {
    try {
      if (h.ExtentBytes == 0 || h.CatalogEntries == 0) return null;
      if (h.DiskSize == 0 || h.DiskSize > int.MaxValue) return null;
      var catalogBytes = (long)h.CatalogEntries * 4;
      if (h.CatalogOffset + catalogBytes > stream.Length) return null;

      stream.Position = h.CatalogOffset;
      var catBuf = new byte[catalogBytes];
      if (!TryReadExact(stream, catBuf)) return null;

      var disk = new byte[(int)h.DiskSize];
      // Extent region starts after the catalog, 512-aligned.
      var extentRegion = Align512(h.CatalogOffset + catalogBytes);
      var perExtent = (long)h.BitmapBytes + h.ExtentBytes;
      if (perExtent <= 0) return null;

      for (var i = 0; i < h.CatalogEntries; ++i) {
        var slot = BinaryPrimitives.ReadUInt32BigEndian(catBuf.AsSpan(i * 4, 4));
        if (slot == 0xFFFFFFFFu) continue; // unallocated -> zero
        var extentFileOffset = extentRegion + slot * perExtent + h.BitmapBytes;
        var diskOffset = (long)i * h.ExtentBytes;
        if (diskOffset >= disk.Length) continue;
        if (extentFileOffset + h.ExtentBytes > stream.Length) continue;
        var toCopy = (int)Math.Min(h.ExtentBytes, disk.Length - diskOffset);
        stream.Position = extentFileOffset;
        var slab = new byte[toCopy];
        if (!TryReadExact(stream, slab)) continue;
        Array.Copy(slab, 0, disk, diskOffset, toCopy);
      }
      return disk;
    } catch {
      return null;
    }
  }

  private static long Align512(long v) => (v + 511) & ~511L;

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
