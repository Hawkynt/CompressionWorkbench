using System.Buffers.Binary;
using System.Text;
using FileFormat.Caf;
using FileFormat.Dsf;

namespace Compression.Tests.Audio;

/// <summary>
/// List() never throws: a CAF or DSF whose structure cannot be parsed still lists as the whole
/// file plus a metadata.ini that records <c>parse_status = partial</c> and why.
/// </summary>
[TestFixture]
public class MalformedAudioListingTests {

  private static byte[] Caf(uint channels) {
    using var ms = new MemoryStream();
    ms.Write("caff\0\u0001\0\0"u8);
    var desc = new byte[32];
    BinaryPrimitives.WriteDoubleBigEndian(desc, 44100);
    "lpcm"u8.CopyTo(desc.AsSpan(8));
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(16), 4);
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(20), 1);
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(24), channels);
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(28), 16);
    WriteChunk(ms, "desc", desc);
    WriteChunk(ms, "data", new byte[4 + 40]);
    return ms.ToArray();
  }

  private static void WriteChunk(Stream s, string type, byte[] body) {
    Span<byte> head = stackalloc byte[12];
    Encoding.ASCII.GetBytes(type).CopyTo(head);
    BinaryPrimitives.WriteInt64BigEndian(head[4..], body.Length);
    s.Write(head);
    s.Write(body);
  }

  /// <summary>A 'DSD ' header whose 'fmt ' chunk is missing.</summary>
  private static byte[] DsfWithoutFmt() {
    var b = new byte[64];
    "DSD "u8.CopyTo(b);
    BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(4), 28);
    BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), 64);
    return b;
  }

  private static void AssertPartialListing(List<Compression.Registry.ArchiveEntryInfo> listing, string fullName, int fullSize) {
    Assert.That(listing.Select(e => e.Name), Is.EquivalentTo(new[] { fullName, "metadata.ini" }));
    Assert.That(listing.Single(e => e.Name == fullName).OriginalSize, Is.EqualTo(fullSize));
  }

  private static string MetadataOf(Action<string> extract) {
    var dir = Directory.CreateTempSubdirectory("cwb_audio_partial_").FullName;
    try {
      extract(dir);
      return File.ReadAllText(Path.Combine(dir, "metadata.ini"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Category("Exception")]
  [TestCase(0u)]
  [TestCase(0xFFFFFFFFu)]
  public void Caf_GivenUnparseableDesc_WhenListed_ThenWholeFileAndPartialMetadata(uint channels) {
    var caf = Caf(channels);
    AssertPartialListing(new CafFormatDescriptor().List(new MemoryStream(caf), null), "FULL.caf", caf.Length);
    var ini = MetadataOf(dir => new CafFormatDescriptor().Extract(new MemoryStream(caf), dir, null, null));
    Assert.That(ini, Does.Contain("parse_status = partial"));
    Assert.That(ini, Does.Contain("error = "));
  }

  [Category("Exception")]
  [Test]
  public void Caf_GivenTruncatedFile_WhenListed_ThenWholeFileAndPartialMetadata() {
    var caf = Caf(2)[..30];
    AssertPartialListing(new CafFormatDescriptor().List(new MemoryStream(caf), null), "FULL.caf", caf.Length);
  }

  [Category("Exception")]
  [Test]
  public void Dsf_GivenMissingFmtChunk_WhenListed_ThenWholeFileAndPartialMetadata() {
    var dsf = DsfWithoutFmt();
    AssertPartialListing(new DsfFormatDescriptor().List(new MemoryStream(dsf), null), "FULL.dsf", dsf.Length);
    var ini = MetadataOf(dir => new DsfFormatDescriptor().Extract(new MemoryStream(dsf), dir, null, null));
    Assert.That(ini, Does.Contain("parse_status = partial"));
  }

  [Category("Exception")]
  [Test]
  public void Dsf_GivenOversizedDsdChunk_WhenListed_ThenWholeFileAndPartialMetadata() {
    var dsf = DsfWithoutFmt();
    BinaryPrimitives.WriteUInt64LittleEndian(dsf.AsSpan(4), 0xFFFFFFFFUL);
    AssertPartialListing(new DsfFormatDescriptor().List(new MemoryStream(dsf), null), "FULL.dsf", dsf.Length);
  }
}
