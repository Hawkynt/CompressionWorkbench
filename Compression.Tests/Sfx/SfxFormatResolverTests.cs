#pragma warning disable CS1591
using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Compression.Registry;
using Compression.Sfx;
using NUnit.Framework;

namespace Compression.Tests.Sfx;

/// <summary>
/// The universal stub identifies a payload with no filename to go on, from its bytes alone. These
/// cover each kind of payload it can meet — an archive, a compressed tar, a compressed lone file,
/// and nothing it understands — plus the tar header forms that decide which of the middle two it is.
/// </summary>
/// <remarks>
/// The resolver's universal branch is compiled straight into the tests from the stub's own source,
/// so this is the code that ships, run against the full registry.
/// </remarks>
[TestFixture]
public class SfxFormatResolverTests {
  private string _dir = null!;

  private static readonly (string Name, byte[] Data)[] Files = [
    ("readme.txt", "hello from a self-extracting archive\n"u8.ToArray()),
    ("nested/data.bin", Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray()),
  ];

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-sfxresolve-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { }
  }

  private static byte[] Tar(TarEntryFormat format) {
    var buffer = new MemoryStream();
    using (var writer = new TarWriter(buffer, format, leaveOpen: true))
      foreach (var (name, data) in Files) {
        TarEntry entry = format switch {
          TarEntryFormat.V7 => new V7TarEntry(TarEntryType.V7RegularFile, name),
          _ => new UstarTarEntry(TarEntryType.RegularFile, name),
        };
        entry.DataStream = new MemoryStream(data);
        writer.WriteEntry(entry);
      }

    return buffer.ToArray();
  }

  private static byte[] Gzip(byte[] data) {
    var buffer = new MemoryStream();
    using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
      gzip.Write(data);
    return buffer.ToArray();
  }

  private static byte[] Zip() {
    var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
      foreach (var (name, data) in Files) {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(data);
      }

    return buffer.ToArray();
  }

  private SfxExtractor? Resolve(byte[] payload, out MemoryStream stream) {
    stream = new MemoryStream(payload);
    return SfxFormatResolver.Resolve(stream);
  }

  private void AssertExtractsAllFiles(SfxExtractor extractor, Stream payload) {
    extractor.Extract(payload, this._dir);

    Assert.Multiple(() => {
      foreach (var (name, data) in Files) {
        var path = Path.Combine(this._dir, name.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(path), Is.True, $"{name} missing");
        if (File.Exists(path)) Assert.That(File.ReadAllBytes(path), Is.EqualTo(data), name);
      }
    });
  }

  // ── archives are handed straight to their reader ────────────────────────────────────────────

  [Test]
  public void GivenAZipPayload_WhenResolved_ThenItIsExtractedAsZip() {
    var extractor = this.Resolve(Zip(), out var payload);

    Assert.That(extractor, Is.Not.Null);
    Assert.That(extractor!.FormatName, Does.Contain("ZIP").IgnoreCase);
    this.AssertExtractsAllFiles(extractor, payload);
  }

  [Test]
  public void GivenAPlainTarPayload_WhenResolved_ThenItIsExtractedAsTar() {
    var extractor = this.Resolve(Tar(TarEntryFormat.Ustar), out var payload);

    Assert.That(extractor, Is.Not.Null);
    this.AssertExtractsAllFiles(extractor!, payload);
  }

  // ── a codec match is only the outer layer ───────────────────────────────────────────────────

  /// <summary>
  /// gzip's magic matches the gzip stream codec, which cannot extract an archive. The stub has to
  /// look inside. This is the case that shipped broken: the resolver stopped at the codec.
  /// </summary>
  [Test]
  public void GivenATarGzPayload_WhenResolved_ThenTheTarInsideIsExtracted() {
    var extractor = this.Resolve(Gzip(Tar(TarEntryFormat.Ustar)), out var payload);

    Assert.That(extractor, Is.Not.Null, "gzip matched, but the stub must look inside rather than give up");
    Assert.That(extractor!.FormatName, Does.Contain("tar").IgnoreCase);
    this.AssertExtractsAllFiles(extractor, payload);
  }

  /// <summary>
  /// A Rust .crate is also tar inside gzip, and only its extension says so. Without one, choosing it
  /// would reject any tarball not laid out like a crate — which is what happened before.
  /// </summary>
  [Test]
  public void GivenATarGzPayload_WhenSeveralFormatsAreTarInsideGzip_ThenThePlainCompoundTarIsChosen() {
    var extractor = this.Resolve(Gzip(Tar(TarEntryFormat.Ustar)), out _);

    Assert.That(extractor!.FormatName, Does.Not.Contain("Crate").IgnoreCase);
  }

  /// <summary>
  /// The original Unix v7 tar has no "ustar" marker. Its header checksum is the only thing that
  /// identifies it, and tar itself validates exactly that sum.
  /// </summary>
  [Test]
  public void GivenAPrePosixV7TarInsideGzip_WhenResolved_ThenTheChecksumIdentifiesIt() {
    var v7 = Tar(TarEntryFormat.V7);
    Assume.That(v7.AsSpan(257, 5).SequenceEqual("ustar"u8), Is.False, "fixture must lack the ustar marker");

    var extractor = this.Resolve(Gzip(v7), out var payload);

    Assert.That(extractor, Is.Not.Null);
    Assert.That(extractor!.FormatName, Does.Contain("tar").IgnoreCase);
    this.AssertExtractsAllFiles(extractor, payload);
  }

  [Test]
  public void GivenAGzippedFileThatIsNotATar_WhenResolved_ThenItIsDecompressedToOneFile() {
    var content = "just one compressed text file, no tar here\n"u8.ToArray();
    var extractor = this.Resolve(Gzip(content), out var payload);

    Assert.That(extractor, Is.Not.Null);
    extractor!.Extract(payload, this._dir);

    var outputs = Directory.GetFiles(this._dir);
    Assert.That(outputs, Has.Length.EqualTo(1));
    Assert.That(File.ReadAllBytes(outputs[0]), Is.EqualTo(content));
  }

  /// <summary>The look inside must not consume the payload the extractor then reads.</summary>
  [Test]
  public void GivenATarGzPayload_WhenResolved_ThenThePayloadIsRewound() {
    this.Resolve(Gzip(Tar(TarEntryFormat.Ustar)), out var payload);

    Assert.That(payload.Position, Is.Zero);
  }

  // ── nothing it understands ──────────────────────────────────────────────────────────────────

  [Test]
  public void GivenNoRecognisableMagic_WhenResolved_ThenNothingClaimsIt() {
    var junk = new byte[4096];
    new Random(42).NextBytes(junk);
    junk[0] = 0x00; junk[1] = 0x00; junk[2] = 0x00; junk[3] = 0x00;

    Assert.That(this.Resolve(junk, out _), Is.Null.Or.Property(nameof(SfxExtractor.FormatName)).Not.Contain("tar"));
  }

  [Test]
  public void GivenAnEmptyPayload_WhenResolved_ThenNothingClaimsIt()
    => Assert.That(this.Resolve([], out _), Is.Null);

  /// <summary>A corrupt gzip must not be mistaken for a tarball just because its magic is right.</summary>
  [Test]
  public void GivenGzipMagicFollowedByGarbage_WhenResolved_ThenItIsNotTreatedAsATar() {
    var corrupt = new byte[1024];
    new Random(7).NextBytes(corrupt);
    corrupt[0] = 0x1F; corrupt[1] = 0x8B; corrupt[2] = 0x08;

    var extractor = this.Resolve(corrupt, out _);

    Assert.That(extractor?.FormatName ?? "", Does.Not.Contain("tar").IgnoreCase);
  }
}
