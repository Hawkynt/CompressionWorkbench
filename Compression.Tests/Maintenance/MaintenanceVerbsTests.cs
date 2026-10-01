using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.Gzip;
using FileFormat.MacBinary;
using FileFormat.SevenZip;
using FileFormat.Zip;
using FileFormat.Zstd;
using Bcl = System.IO.Compression;

namespace Compression.Tests.Maintenance;

/// <summary>
/// The staged maintenance operations — compress, canonicalize, repack, change geometry —
/// run through <see cref="MaintenanceVerbs"/>: a result is handed back only when it keeps
/// everything the format's reader reports, it is never worse than the input, and an
/// operation the format does not offer, or a result that would lose something, is refused
/// with the output untouched.
/// </summary>
[TestFixture]
public sealed class MaintenanceVerbsTests {

  private static readonly DateTime Stamp = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Local);

  private static byte[] Compressible(int length = 64 * 1024) {
    var text = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog; ");
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i) data[i] = text[i % text.Length];
    return data;
  }

  // ── compress: single streams ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAWeakStream_WhenCompressed_ThenItIsSmallerAndDecodesToTheSameBytes() {
    var descriptor = new ZstdFormatDescriptor();
    var payload = Compressible();
    using var weak = new MemoryStream();
    descriptor.Compress(new MemoryStream(payload), weak, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = "1" },
    });
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Compress(descriptor, weak, output);

    using var decoded = new MemoryStream();
    output.Position = 0;
    descriptor.Decompress(output, decoded);
    Assert.Multiple(() => {
      Assert.That(result.NewSize, Is.LessThanOrEqualTo(result.OriginalSize));
      Assert.That(decoded.ToArray(), Is.EqualTo(payload));
    });
  }

  [Test, Category("Boundary")]
  public void GivenAStreamNothingCanImprove_WhenCompressed_ThenTheOutputIsTheInputByteForByte() {
    var descriptor = new ZstdFormatDescriptor();
    using var optimal = new MemoryStream();
    descriptor.CompressOptimal(new MemoryStream(Compressible()), optimal);
    var original = optimal.ToArray();
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Compress(descriptor, new MemoryStream(original), output);

    if (!result.Changed)
      Assert.That(output.ToArray(), Is.EqualTo(original), "an unchanged result must be the input itself");
    else
      Assert.That(result.NewSize, Is.LessThan(original.LongLength), "a changed result must be smaller");
  }

  [Test, Category("HappyPath")]
  public void GivenAGzipWithNameTimeAndComment_WhenCompressed_ThenTheHeaderComesBackVerbatim() {
    using var weak = new MemoryStream();
    using (var encoder = new GzipStream(weak, Compression.Core.Streams.CompressionStreamMode.Compress,
             Compression.Core.Deflate.DeflateCompressionLevel.None, leaveOpen: true) {
             Header = new GzipHeader { FileName = "report.txt", Comment = "kept", ModificationTime = 981173106, OperatingSystem = 3 },
           })
      encoder.Write(Compressible());
    using var output = new MemoryStream();

    MaintenanceVerbs.Compress(new GzipFormatDescriptor(), weak, output);

    output.Position = 0;
    var header = GzipHeader.Read(output);
    Assert.Multiple(() => {
      Assert.That(header.FileName, Is.EqualTo("report.txt"));
      Assert.That(header.Comment, Is.EqualTo("kept"));
      Assert.That(header.ModificationTime, Is.EqualTo(981173106u));
      Assert.That(header.OperatingSystem, Is.EqualTo((byte)3));
    });
  }

  [Test, Category("Exception")]
  public void GivenAMultiMemberGzip_WhenCompressed_ThenItIsRefusedAndNothingIsWritten() {
    using var two = new MemoryStream();
    foreach (var part in new[] { "first ", "second" }) {
      using var encoder = new GzipStream(two, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
      encoder.Write(Encoding.ASCII.GetBytes(part));
    }
    using var output = new MemoryStream();

    Assert.That(() => MaintenanceVerbs.Compress(new GzipFormatDescriptor(), two, output), Throws.TypeOf<NotSupportedException>());
    Assert.That(output.Length, Is.Zero, "a refusal must not write a partial result");
  }

  [Test, Category("Exception")]
  public void GivenAFormatThatDoesNotCompress_WhenAskedTo_ThenItIsRefusedBeforeAnythingIsRead() {
    using var input = new MemoryStream([1, 2, 3]);
    using var output = new MemoryStream();
    Assert.That(() => MaintenanceVerbs.Compress(new MacBinaryFormatDescriptor(), input, output), Throws.TypeOf<NotSupportedException>());
    Assert.That(input.Position, Is.Zero);
  }

  // ── compress / repack: ZIP rewritten from its own directory ────────

  private static byte[] BclZip(Bcl.CompressionLevel level, bool withHole = false) {
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
      zip.Comment = "archive comment";
      foreach (var (name, data, comment) in new[] {
                 ("docs/readme.txt", Compressible(20_000), "readme"),
                 ("big.bin", Compressible(80_000), "big"),
                 ("empty.txt", Array.Empty<byte>(), "nothing"),
               }) {
        var entry = zip.CreateEntry(name, level);
        entry.Comment = comment;
        entry.ExternalAttributes = unchecked((int)0x81A40000); // a Unix regular file, mode 0644
        entry.LastWriteTime = new DateTimeOffset(Stamp);
        using var content = entry.Open();
        content.Write(data);
      }
      zip.CreateEntry("folder/");
    }
    if (withHole) {
      buffer.Position = 0;
      ZipModifier.RemoveFile(buffer, "big.bin");
    }
    return buffer.ToArray();
  }

  private static List<string> BclListing(byte[] zip) {
    using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
    var lines = archive.Entries.Select(e => {
      using var content = e.Open();
      using var copy = new MemoryStream();
      content.CopyTo(copy);
      return $"{e.FullName}|{e.Comment}|{e.ExternalAttributes:X8}|{e.LastWriteTime:O}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.ToArray()))}";
    }).ToList();
    lines.Add("comment|" + archive.Comment);
    return lines;
  }

  [Test, Category("HappyPath")]
  public void GivenAStoredZip_WhenCompressed_ThenEntriesAreDeflatedAndEveryFieldSurvives() {
    var stored = BclZip(Bcl.CompressionLevel.NoCompression);
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Compress(new ZipFormatDescriptor(), new MemoryStream(stored), output);

    Assert.Multiple(() => {
      Assert.That(result.Changed, Is.True);
      Assert.That(result.NewSize, Is.LessThan(stored.LongLength));
      Assert.That(BclListing(output.ToArray()), Is.EqualTo(BclListing(stored)),
        "names, comments, attributes, times and contents must all come back as they were");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAZipWithAHoleFromARemoval_WhenRepacked_ThenTheHoleIsGoneAndEveryEntryIsByteIdentical() {
    var holed = BclZip(Bcl.CompressionLevel.Optimal, withHole: true);
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Repack(new ZipFormatDescriptor(), new MemoryStream(holed), output);

    Assert.Multiple(() => {
      Assert.That(result.Changed, Is.True);
      Assert.That(result.NewSize, Is.LessThan(holed.LongLength));
      Assert.That(BclListing(output.ToArray()), Is.EqualTo(BclListing(holed)));
    });
  }

  [Test, Category("Boundary")]
  public void GivenAZipWithNothingToReclaim_WhenRepacked_ThenItIsUnchanged() {
    var tight = BclZip(Bcl.CompressionLevel.Optimal);
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Repack(new ZipFormatDescriptor(), new MemoryStream(tight), output);

    Assert.Multiple(() => {
      Assert.That(output.ToArray(), Has.Length.EqualTo(tight.Length));
      Assert.That(BclListing(output.ToArray()), Is.EqualTo(BclListing(tight)));
    });
  }

  [Test, Category("Exception")]
  public void GivenAZipBehindAStub_WhenRepacked_ThenItIsRefusedRatherThanLosingTheStub() {
    // A real self-extractor: the archive is written after the stub, so its offsets count the stub.
    using var buffer = new MemoryStream();
    buffer.Write(Encoding.ASCII.GetBytes("MZ self-extractor stub"));
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
      using (var content = zip.CreateEntry("a.txt").Open())
        content.Write(Compressible(1000));
    var stubbed = buffer.ToArray();
    using var output = new MemoryStream();
    Assert.That(() => MaintenanceVerbs.Repack(new ZipFormatDescriptor(), new MemoryStream(stubbed), output), Throws.TypeOf<NotSupportedException>());
    Assert.That(output.Length, Is.Zero);
  }

  // ── compress: 7z keeps entry metadata ──────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAStored7z_WhenCompressed_ThenTimesAndAttributesSurvive() {
    using var source = new MemoryStream();
    using (var writer = new SevenZipWriter(source, SevenZipCodec.Copy, leaveOpen: true)) {
      writer.AddDirectory(new SevenZipEntry { Name = "dir", LastWriteTime = Stamp.ToUniversalTime(), Attributes = 0x10 });
      writer.AddEntry(new SevenZipEntry { Name = "dir/a.txt", LastWriteTime = Stamp.ToUniversalTime(), CreationTime = Stamp.AddDays(-1).ToUniversalTime(), Attributes = 0x21 }, Compressible(30_000));
      writer.AddEntry(new SevenZipEntry { Name = "b.txt", LastWriteTime = Stamp.AddHours(3).ToUniversalTime(), Attributes = 0x20 }, Compressible(10_000));
      writer.Finish();
    }
    var original = source.ToArray();
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Compress(new SevenZipFormatDescriptor(), new MemoryStream(original), output);

    static List<string> Listing(byte[] archive) {
      var reader = new SevenZipReader(new MemoryStream(archive));
      return [.. reader.Entries.Select((e, i) => $"{e.Name}|{e.IsDirectory}|{e.LastWriteTime:O}|{e.CreationTime:O}|{e.Attributes}|"
        + (e.IsDirectory ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(reader.Extract(i)))))
        .Order(StringComparer.Ordinal)];
    }
    Assert.Multiple(() => {
      Assert.That(result.Changed, Is.True);
      Assert.That(result.NewSize, Is.LessThan(original.LongLength));
      Assert.That(Listing(output.ToArray()), Is.EqualTo(Listing(original)));
    });
  }

  // ── canonicalize ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenMacBinaryWithTrailingGarbage_WhenCanonicalized_ThenOnlyTheGarbageGoes() {
    using var encoded = new MemoryStream();
    MacBinaryWriter.Write(encoded, "file", "data fork"u8.ToArray(), "resource fork"u8.ToArray(), "TEXT", "ttxt", Stamp);
    var clean = encoded.ToArray();
    var dirty = clean.Concat(new byte[300]).ToArray();
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Canonicalize(new MacBinaryFormatDescriptor(), new MemoryStream(dirty), output);

    output.Position = 0;
    var resource = MacBinaryReader.ReadResourceFork(output);
    output.Position = 0;
    var data = MacBinaryReader.ReadDataFork(output);
    Assert.Multiple(() => {
      Assert.That(result.Changed, Is.True);
      Assert.That(output.Length, Is.LessThan(dirty.LongLength));
      Assert.That(data, Is.EqualTo("data fork"u8.ToArray()));
      Assert.That(resource, Is.EqualTo("resource fork"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnMp4WithMoovAtTheEnd_WhenCanonicalized_ThenItIsFastStartAndTheTracksAreUnchanged() {
    var original = Mp4.Mp4FastStartTests.BuildMoovAtEnd(out _);
    var descriptor = FormatRegistry.GetById("Mp4") ?? throw new AssertionException("Mp4 is not registered.");
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Canonicalize(FormatRegistry.GetArchiveOps("Mp4") ?? (object)descriptor, new MemoryStream(original), output);

    var moov = IndexOf(output.ToArray(), "moov"u8);
    var mdat = IndexOf(output.ToArray(), "mdat"u8);
    Assert.Multiple(() => {
      Assert.That(result.Changed, Is.True);
      Assert.That(output.Length, Is.EqualTo(original.LongLength));
      Assert.That(moov, Is.LessThan(mdat), "moov must precede mdat after canonicalization");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAJpegWithExifBehindOtherSegments_WhenCanonicalized_ThenExifComesFirstAndEveryEntryIsKept() {
    var original = Jpeg.JpegLayoutMapTests.BuildUnoptimizedJpeg();
    var descriptor = new FileFormat.JpegArchive.JpegArchiveDescriptor();
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.Canonicalize(descriptor, new MemoryStream(original), output);

    var canonical = output.ToArray();
    Assert.Multiple(() => {
      Assert.That(result.Changed, Is.True);
      Assert.That(canonical, Has.Length.EqualTo(original.Length));
      Assert.That(canonical[2..4], Is.EqualTo(new byte[] { 0xFF, 0xE1 }), "the first segment after SOI must be APP1/EXIF");
    });
  }

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

  [Test, Category("Exception")]
  public void GivenAFormatWithoutACanonicalForm_WhenAskedTo_ThenItIsRefused() {
    using var output = new MemoryStream();
    Assert.That(() => MaintenanceVerbs.Canonicalize(new ZipFormatDescriptor(), new MemoryStream(BclZip(Bcl.CompressionLevel.Optimal)), output),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test, Category("Exception")]
  public void GivenAFormatThatDoesNotRepack_WhenAskedTo_ThenItIsRefused() {
    using var output = new MemoryStream();
    Assert.That(() => MaintenanceVerbs.Repack(new GzipFormatDescriptor(), new MemoryStream([0x1F, 0x8B]), output),
      Throws.TypeOf<NotSupportedException>());
  }

  // ── change geometry ────────────────────────────────────────────────

  [Test, Category("Exception")]
  public void GivenARealFilesystemWhoseRelayoutLosesMetadata_WhenAskedToChangeGeometry_ThenItIsRefused() {
    var image = new FileSystem.Fat.FatWriter().Build();
    using var output = new MemoryStream();
    Assert.That(() => MaintenanceVerbs.ChangeGeometry(new FileSystem.Fat.FatFormatDescriptor(), new MemoryStream(image), output,
      new Dictionary<string, string> { ["ClusterSize"] = "2 KB" }), Throws.TypeOf<NotSupportedException>());
    Assert.That(output.Length, Is.Zero);
  }

  [Test, Category("HappyPath")]
  public void GivenALosslessRelayout_WhenGeometryChanges_ThenTheVerifiedResultIsKept() {
    var archive = BclZip(Bcl.CompressionLevel.Optimal);
    using var output = new MemoryStream();

    var result = MaintenanceVerbs.ChangeGeometry(new GeometryProbe(GeometryProbe.Behaviour.Faithful), new MemoryStream(archive), output,
      new Dictionary<string, string> { ["ClusterSize"] = "4 KB" });

    Assert.That(BclListing(output.ToArray()), Is.EqualTo(BclListing(archive)));
    Assert.That(result.OriginalSize, Is.EqualTo(archive.LongLength));
  }

  [Test, Category("Exception")]
  public void GivenARelayoutThatResetsTimestamps_WhenGeometryChanges_ThenTheManifestCatchesItAndNothingIsWritten() {
    using var output = new MemoryStream();
    var ex = Assert.Throws<NotSupportedException>(() => MaintenanceVerbs.ChangeGeometry(
      new GeometryProbe(GeometryProbe.Behaviour.GenericRebuild), new MemoryStream(BclZip(Bcl.CompressionLevel.Optimal)), output,
      new Dictionary<string, string> { ["ClusterSize"] = "4 KB" }));
    Assert.Multiple(() => {
      Assert.That(ex!.Message, Does.Contain("would not keep the contents intact"), "the refusal says what went wrong");
      Assert.That(output.Length, Is.Zero);
    });
  }

  [Test, Category("Exception")]
  public void GivenAKeyThatIsNotGeometry_WhenGeometryChanges_ThenItIsRefusedNamingTheAllowedKeys() {
    using var output = new MemoryStream();
    var ex = Assert.Throws<NotSupportedException>(() => MaintenanceVerbs.ChangeGeometry(
      new GeometryProbe(GeometryProbe.Behaviour.Faithful), new MemoryStream(BclZip(Bcl.CompressionLevel.Optimal)), output,
      new Dictionary<string, string> { ["VolumeLabel"] = "NEW" }));
    Assert.That(ex!.Message, Does.Contain("ClusterSize"));
  }

  /// <summary>
  /// A ZIP dressed up as a relayout-capable volume, so the geometry path can be exercised
  /// both ways: a relayout that copies the archive (lossless) and the generic rebuild
  /// (which re-stamps every file with the extraction time).
  /// </summary>
  private sealed class GeometryProbe(GeometryProbe.Behaviour behaviour)
      : IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema, ILayoutOptimizable {
    public enum Behaviour { Faithful, GenericRebuild }

    private readonly ZipFormatDescriptor _zip = new();

    public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
      FilesystemSchemaPresets.ClusterSize(),
      FilesystemSchemaPresets.VolumeLabel(),
    ];

    public bool RelayoutPreservesEverything => true;

    public List<ArchiveEntryInfo> List(Stream stream, string? password) => this._zip.List(stream, password);
    public void Extract(Stream stream, string outputDir, string? password, string[]? files) => this._zip.Extract(stream, outputDir, password, files);
    public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) => this._zip.Create(output, inputs, options);

    public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
      if (behaviour == Behaviour.GenericRebuild) {
        RebuildVerb.RebuildToStream(source, target, this, this, options.Parameters);
        return;
      }
      source.Position = 0;
      source.CopyTo(target);
    }
  }
}
