#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rpm;

/// <summary>
/// RPM package — lead + signature header + main header + compressed cpio payload.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/rpm-software-management/rpm</c> — canonical rpm sources (docs/manual describes the package format)</description></item>
///   <item><description>Edward C. Bailey, "Maximum RPM" — classic format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/RPM_Package_Manager</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class RpmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the RPM archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the RPM archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new RpmReader(stream);
        // The decompressed one: a package's payload is a compressed cpio
        // archive, and every other operation here reads it that way. Reading
        // the raw bytes handed the cpio reader the compressor's own header,
        // which it rejected — so defragmenting any package threw.
        using var payload = r.GetDecompressedPayloadStream();
        var cpioReader = new FileFormat.Cpio.CpioReader(payload);
        return cpioReader.ReadAll()
          .Where(x => !x.Entry.IsDirectory)
          .Select(x => (x.Entry.Name, x.Data))
          .ToList();
      },
      buildImage: files => {
        var w = new RpmWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    // RPM layout: Lead (96 B) + Signature header + Main header + Payload
    // Walk the RPM header structures to find where the payload starts.
    if (archive.Length < 96)
      yield break;
    archive.Position = 0;
    yield return new DefragBlockInfo(0, RpmConstants.LeadSize, DefragBlockKind.MetadataReserved, FileName: "RPM Lead");

    var pos = (long)RpmConstants.LeadSize;
    // Skip Signature header + Main header by reading their index/store sizes
    for (var h = 0; h < 2; h++) {
      if (pos + 16 > archive.Length) yield break;
      archive.Position = pos;
      var hdr = new byte[16];
      if (archive.Read(hdr, 0, 16) < 16) yield break;
      // Validate header magic 8E AD E8 01
      if (hdr[0] != 0x8E || hdr[1] != 0xAD || hdr[2] != 0xE8 || hdr[3] != 0x01) yield break;
      var nindex = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(8));
      var hsize = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(12));
      var headerLen = 16 + nindex * 16 + hsize;
      var label = h == 0 ? "Signature Header" : "Main Header";
      yield return new DefragBlockInfo(pos, headerLen, DefragBlockKind.MetadataReserved, FileName: label);
      pos += headerLen;
      // After signature header, align to 8 bytes
      if (h == 0) {
        var rem = pos % 8;
        if (rem != 0) pos += 8 - rem;
      }
    }

    // Payload: rest of file
    var payloadLen = archive.Length - pos;
    if (payloadLen > 0)
      yield return new DefragBlockInfo(pos, payloadLen, DefragBlockKind.Used, FileName: "payload.cpio");
  }

  public string Id => "Rpm";
  public string DisplayName => "RPM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".rpm";
  public IReadOnlyList<string> Extensions => [".rpm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xED, 0xAB, 0xEE, 0xDB], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rpm", "RPM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Red Hat Package Manager archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (stream.CanSeek) stream.Position = 0;
    var r = new RpmReader(stream);
    using var payload = r.GetDecompressedPayloadStream();
    var cpioReader = new FileFormat.Cpio.CpioReader(payload, leaveOpen: true);

    var result = new List<ArchiveEntryInfo>();
    var index = 0;
    foreach (var (entry, data) in cpioReader.ReadAll())
      result.Add(new ArchiveEntryInfo(
        index++, entry.Name, data.Length, data.Length, "cpio", entry.IsDirectory, false, null));

    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (stream.CanSeek) stream.Position = 0;
    var r = new RpmReader(stream);
    using var payload = r.GetDecompressedPayloadStream();
    var cpioReader = new FileFormat.Cpio.CpioReader(payload, leaveOpen: true);
    foreach (var (entry, data) in cpioReader.ReadAll()) {
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      if (entry.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, entry.Name)); continue; }
      WriteFile(outputDir, entry.Name, data);
    }
  }

  /// <summary>
  /// Opens a single RPM entry as a bounded read-only stream. RPM wraps an
  /// inner CPIO payload; entry names route to the matching CPIO member's
  /// decoded bytes, wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new RpmReader(archive);
    using var payload = r.GetDecompressedPayloadStream();
    var cpioReader = new FileFormat.Cpio.CpioReader(payload, leaveOpen: true);
    foreach (var (entry, data) in cpioReader.ReadAll()) {
      if (entry.IsDirectory) continue;
      if (!string.Equals(entry.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new RpmWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
