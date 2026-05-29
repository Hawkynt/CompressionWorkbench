#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Crx;

public sealed class CrxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FileFormat.Zip.ZipLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag: strips the CRX envelope, defrags the inner ZIP, then re-emits.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: strips the CRX envelope, defrags the inner ZIP, then re-emits.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        StripCrxHeader(stream);
        var r = new FileFormat.Zip.ZipReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        // Re-emit a fresh CRX3 envelope (matching Create()).
        ms.Write([(byte)'C', (byte)'r', (byte)'2', (byte)'4']);
        Span<byte> u32 = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(u32, 3);
        ms.Write(u32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(u32, 0);
        ms.Write(u32);
        using (var w = new FileFormat.Zip.ZipWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }

  public string Id => "Crx";
  public string DisplayName => "CRX";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".crx";
  public IReadOnlyList<string> Extensions => [".crx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'C', (byte)'r', (byte)'2', (byte)'4'], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Chrome extension package (CRX3 header + ZIP)";

  private static Stream StripCrxHeader(Stream stream) {
    var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    var magic = reader.ReadBytes(4);
    if (magic is not [(byte)'C', (byte)'r', (byte)'2', (byte)'4'])
      throw new InvalidDataException("Not a CRX file.");
    var version = reader.ReadUInt32();
    var headerLen = reader.ReadUInt32();
    stream.Position = 12 + headerLen;
    return stream;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    StripCrxHeader(stream);
    var r = new FileFormat.Zip.ZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    StripCrxHeader(stream);
    var r = new FileFormat.Zip.ZipReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // Write a minimal CRX3 envelope: "Cr24" magic, version 3, empty signed header.
    // Roundtrips through our reader. NOTE: not browser-loadable because the
    // CrxFileHeader protobuf is empty (no signing keys/signatures). Real signing
    // requires a private key and is out of scope.
    output.Write([(byte)'C', (byte)'r', (byte)'2', (byte)'4']);
    Span<byte> u32 = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(u32, 3);
    output.Write(u32);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(u32, 0);
    output.Write(u32);

    using var w = new FileFormat.Zip.ZipWriter(output, leaveOpen: true);
    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.ArchiveName); continue; }
      w.AddEntry(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
  }
}
