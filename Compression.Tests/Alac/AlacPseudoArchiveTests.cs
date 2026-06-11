using System.Buffers.Binary;
using System.Text;
using FileFormat.Alac;

namespace Compression.Tests.Alac;

/// <summary>
/// Given an ALAC/M4A container, When the descriptor lists/extracts it, Then the
/// FULL passthrough round-trips byte-identically and malformed input falls back
/// to FULL without throwing.
/// </summary>
[TestFixture]
public class AlacPseudoArchiveTests {

  private static void WriteBoxHeader(MemoryStream ms, int size, string type) {
    Span<byte> hdr = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)size);
    Encoding.ASCII.GetBytes(type.AsSpan(), hdr[4..]);
    ms.Write(hdr);
  }

  // A standalone ftyp + free box — valid ISOBMFF but no moov/alac track. The
  // descriptor must still surface FULL.m4a verbatim without throwing.
  private static byte[] BuildFtypOnly() {
    using var ms = new MemoryStream();
    var body = new byte[] { (byte)'M', (byte)'4', (byte)'A', (byte)' ', 0, 0, 0, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m' };
    WriteBoxHeader(ms, 8 + body.Length, "ftyp");
    ms.Write(body);
    WriteBoxHeader(ms, 8 + 4, "free");
    ms.Write(new byte[4]);
    return ms.ToArray();
  }

  [Test, Category("EdgeCase")]
  public void List_ContainerWithoutAlacTrack_FallsBackToFull() {
    var file = BuildFtypOnly();
    using var ms = new MemoryStream(file);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new AlacFormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.m4a"));
  }

  [Test, Category("HappyPath")]
  public void ExtractEntry_Full_RoundTripsByteIdentical() {
    var file = BuildFtypOnly();
    using var ms = new MemoryStream(file);
    using var full = new MemoryStream();
    new AlacFormatDescriptor().ExtractEntry(ms, "FULL.m4a", full, null);
    Assert.That(full.ToArray(), Is.EqualTo(file));
  }
}
