#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Fmod;

namespace Compression.Tests.Fmod;

[TestFixture]
public class FmodTests {

  private const int HeaderSize = 60;

  /// <summary>
  /// Builds a synthetic minimal FSB5 with two samples, no name table, mode = Vorbis.
  /// </summary>
  private static byte[] MakeMinimalFsb(uint mode = 15 /* Vorbis */, bool includeNames = true) {
    // Two samples, 8-byte headers each (no extra chunks, hasChunks=0, hasNext=0).
    // Sample 0 starts at offset 0 in data; sample 1 starts at offset 32 bytes.
    var sample0 = Encoding.ASCII.GetBytes("sample0_body__padding_to_32bytes!"); // 32 bytes
    var sample1 = Encoding.ASCII.GetBytes("sample1_body_here..."); // 20 bytes

    // Pad sample 0 to exactly 32 bytes so the next sample's offset (in 16-byte units) is 2.
    if (sample0.Length != 32) {
      Array.Resize(ref sample0, 32);
    }
    var dataSize = (uint)(sample0.Length + sample1.Length);

    var headers = new byte[16]; // 2 headers * 8 bytes
    // Header layout used by the descriptor: offset-in-data is packed at bits 7..33
    // (scale = 16 bytes). has_chunks flag = bit 0.
    // Sample 0 @ offset 0  → packed bits 7..33 = 0
    // Sample 1 @ offset 32 → packed bits 7..33 = 32/16 = 2
    ulong h0 = 0UL; // all zero
    ulong h1 = 2UL << 7;
    BinaryPrimitives.WriteUInt64LittleEndian(headers.AsSpan(0), h0);
    BinaryPrimitives.WriteUInt64LittleEndian(headers.AsSpan(8), h1);

    byte[] nameTable;
    if (includeNames) {
      // Layout: 2× uint32 offsets, then NUL-terminated strings.
      var nameA = Encoding.UTF8.GetBytes("hello");
      var nameB = Encoding.UTF8.GetBytes("world");
      // Offsets relative to name-table start.
      var offA = 8u;                                // right after the two uint32 offsets
      var offB = offA + (uint)nameA.Length + 1;     // after nameA + NUL
      nameTable = new byte[offB + nameB.Length + 1];
      BinaryPrimitives.WriteUInt32LittleEndian(nameTable.AsSpan(0), offA);
      BinaryPrimitives.WriteUInt32LittleEndian(nameTable.AsSpan(4), offB);
      Array.Copy(nameA, 0, nameTable, (int)offA, nameA.Length);
      Array.Copy(nameB, 0, nameTable, (int)offB, nameB.Length);
      nameTable[^1] = 0; // ensure final NUL
    } else {
      nameTable = [];
    }

    var total = HeaderSize + headers.Length + nameTable.Length + (int)dataSize;
    var file = new byte[total];
    var sp = file.AsSpan();

    Encoding.ASCII.GetBytes("FSB5").CopyTo(sp);
    BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(4), 1u);                         // version
    BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(8), 2u);                         // numSamples
    BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(12), (uint)headers.Length);      // sampleHeadersSize
    BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(16), (uint)nameTable.Length);    // nameTableSize
    BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(20), dataSize);                  // dataSize
    BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(24), mode);                      // mode
    // Bytes 28..60 stay zero (flags/hash).

    var cur = HeaderSize;
    headers.CopyTo(sp.Slice(cur, headers.Length));
    cur += headers.Length;
    nameTable.CopyTo(sp.Slice(cur, nameTable.Length));
    cur += nameTable.Length;
    sample0.CopyTo(sp.Slice(cur, sample0.Length));
    cur += sample0.Length;
    sample1.CopyTo(sp.Slice(cur, sample1.Length));

    return file;
  }

  [Test]
  public void List_ReturnsExpectedCanonicalEntries() {
    var data = MakeMinimalFsb();
    using var ms = new MemoryStream(data);
    var entries = new FmodFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.fsb"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "sample_headers.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "name_table.bin"), Is.True);
    // At least one sample blob should be emitted.
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/", StringComparison.Ordinal)), Is.True);
  }

  [Test]
  public void Extract_WritesFiles() {
    var data = MakeMinimalFsb();
    var tmp = Path.Combine(Path.GetTempPath(), "fsb_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new FmodFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.fsb")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(Directory.Exists(Path.Combine(tmp, "samples")), Is.True);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("mode_name=Vorbis"));
      Assert.That(ini, Does.Contain("num_samples=2"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void CorruptInput_DoesNotThrow() {
    // Magic only, then garbage.
    var garbage = new byte[128];
    "FSB5"u8.CopyTo(garbage.AsSpan());
    for (var i = 4; i < garbage.Length; ++i) garbage[i] = 0xFF;
    using var ms = new MemoryStream(garbage);
    var descriptor = new FmodFormatDescriptor();
    Assert.DoesNotThrow(() => descriptor.List(ms, null));
  }
}
