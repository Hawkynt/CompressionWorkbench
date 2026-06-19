using System.Security.Cryptography;
using FileFormat.Acronis;

namespace Compression.Tests.Acronis;

/// <summary>
///   Self-round-trip acceptance gate for <see cref="AcronisWriter"/> — the whole-archive writer
///   for Acronis classic <c>.tib</c> Windows file-system slices.
///
///   <para>
///     Given a set of files, when <see cref="AcronisWriter.Build"/> emits a slice, then
///     <see cref="AcronisReader"/> must parse it back: header valid, trailer file-system form,
///     every entry surfaced by name + size, FileMeta chain walk complete, and every file's
///     content extracted byte-identical with a passing MD5 integrity check.
///   </para>
///
///   <para>
///     This pins the writer as the byte-exact inverse of the reader. The writer is NOT yet
///     advertised via <c>CanCreate</c> — vendor-restore validation is the gate for that flip.
///   </para>
/// </summary>
[TestFixture]
public class AcronisWriterTests {

  private static readonly byte[] Compressible =
    System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox. ", 4096)));

  private static byte[] Incompressible(int len, int seed) {
    var data = new byte[len];
    var rng = new Random(seed);
    rng.NextBytes(data);
    return data;
  }

  // ─── header / trailer structure ───────────────────────────────────

  [Test, Category("HappyPath")]
  public void Build_EmitsValidVolumeHeader() {
    var bytes = AcronisWriter.Build([new AcronisWriter.FileSpec("", "a.txt", "hello"u8.ToArray())]);
    using var ms = new MemoryStream(bytes);
    var header = AcronisVolumeHeader.Read(ms);
    Assert.Multiple(() => {
      Assert.That(header.HeaderLength, Is.EqualTo(0x20));
      Assert.That(header.Version, Is.EqualTo(AcronisVolumeVersion.Windows));
      Assert.That(header.BlockSize, Is.EqualTo(32u));
    });
  }

  [Test, Category("HappyPath")]
  public void Build_EmitsFileSystemTrailerWithValidMirror() {
    var bytes = AcronisWriter.Build([new AcronisWriter.FileSpec("", "a.txt", "hello"u8.ToArray())]);
    using var ms = new MemoryStream(bytes);
    var header = AcronisVolumeHeader.Read(ms);
    var trailer = AcronisSliceTrailer.TryRead(ms, header);
    Assert.That(trailer, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(trailer!.Form, Is.EqualTo(AcronisSliceForm.FileSystem));
      Assert.That(trailer.MirrorValid, Is.True, "Footer's reversed-header mirror must validate.");
      Assert.That(trailer.MetadataOffset, Is.EqualTo(0x20), "Metadata starts right after the header.");
    });
  }

  // ─── listing round-trip ───────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void RoundTrip_SurfacesEveryEntryByNameAndSize() {
    var files = new[] {
      new AcronisWriter.FileSpec("", "readme.txt", "abc"u8.ToArray()),
      new AcronisWriter.FileSpec("sub/", "nested.txt", "nested body"u8.ToArray()),
      new AcronisWriter.FileSpec("sub/deep/", "deeper.txt", "deeper body!"u8.ToArray()),
    };
    var bytes = AcronisWriter.Build(files);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    var names = reader.Entries.Select(e =>
      string.IsNullOrEmpty(e.Path) ? e.Name : e.Path.TrimEnd('/') + "/" + e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "readme.txt", "sub/nested.txt", "sub/deep/deeper.txt" }));
    for (var i = 0; i < files.Length; i++)
      Assert.That(reader.Entries[i].FileSize, Is.EqualTo(files[i].Content.LongLength));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_ChainWalkResolvesEveryEntry() {
    var files = new[] {
      new AcronisWriter.FileSpec("", "a.bin", Incompressible(500, 1)),
      new AcronisWriter.FileSpec("", "b.bin", Incompressible(700, 2)),
    };
    var bytes = AcronisWriter.Build(files);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisReader(ms);
    Assert.That(reader.ChainWalkComplete, Is.True,
      "Every Listing entry must resolve to a RecordIndex via the FileMeta chain walk.");
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_DecodedNameMatchesItemCommonAttribute() {
    var bytes = AcronisWriter.Build([new AcronisWriter.FileSpec("", "umlaut-äöü.txt", "x"u8.ToArray())]);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisReader(ms);
    Assert.That(reader.DecodedNamesByEntry, Has.Count.EqualTo(1));
    Assert.That(reader.DecodedNamesByEntry[0], Is.EqualTo("umlaut-äöü.txt"),
      "The 102 ItemCommon attribute must carry the file name through the chain walk.");
  }

  // ─── content extraction round-trip (byte-identical) ───────────────

  [Test, Category("HappyPath")]
  public void RoundTrip_ExtractsContentByteIdentical() {
    var files = new[] {
      new AcronisWriter.FileSpec("", "compressible.txt", Compressible),
      new AcronisWriter.FileSpec("", "incompressible.bin", Incompressible(4096, 7)),
      new AcronisWriter.FileSpec("dir/", "small.txt", "small content"u8.ToArray()),
    };
    var bytes = AcronisWriter.Build(files);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisReader(ms);

    for (var i = 0; i < files.Length; i++) {
      var result = reader.ExtractFile(i);
      Assert.That(result.IntegrityValid, Is.True, $"file {i} MD5 must validate");
      Assert.That(result.Data, Is.EqualTo(files[i].Content), $"file {i} content must be byte-identical");
    }
  }

  [Test, Category("BoundaryValue")]
  public void RoundTrip_EmptyFile() {
    var bytes = AcronisWriter.Build([new AcronisWriter.FileSpec("", "empty.dat", [])]);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var result = reader.ExtractFile(0);
    Assert.That(result.Data, Is.Empty);
    Assert.That(result.IntegrityValid, Is.True);
  }

  [Test, Category("BoundaryValue")]
  public void RoundTrip_NoFiles_ProducesParseableEmptySlice() {
    var bytes = AcronisWriter.Build([]);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisReader(ms);
    Assert.That(reader.Entries, Is.Empty);
    Assert.That(reader.Trailer, Is.Not.Null);
    Assert.That(reader.Trailer!.Form, Is.EqualTo(AcronisSliceForm.FileSystem));
  }

  [Test, Category("BoundaryValue")]
  public void RoundTrip_LargeMultiKilobyteFile_IsByteIdentical() {
    var content = Incompressible(200_000, 99);
    var bytes = AcronisWriter.Build([new AcronisWriter.FileSpec("", "big.bin", content)]);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisReader(ms);
    var result = reader.ExtractFile(0);
    Assert.That(result.Data, Is.EqualTo(content));
    Assert.That(MD5.HashData(result.Data), Is.EqualTo(MD5.HashData(content)));
  }

  // ─── extraction via the public descriptor (end-to-end) ────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ExtractsWrittenSliceToDisk() {
    var files = new[] {
      new AcronisWriter.FileSpec("", "one.txt", "one"u8.ToArray()),
      new AcronisWriter.FileSpec("sub/", "two.txt", "two"u8.ToArray()),
    };
    var bytes = AcronisWriter.Build(files);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb-acr-" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      var descriptor = new AcronisFormatDescriptor();
      descriptor.Extract(ms, outDir, password: null, files: null);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "one.txt")), Is.EqualTo("one"));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "sub", "two.txt")), Is.EqualTo("two"));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }
}
