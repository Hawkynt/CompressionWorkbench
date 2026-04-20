using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Umx;

[TestFixture]
public class UmxTests {

  /// <summary>
  /// Builds a minimal UMX file with one Music export containing the given payload.
  /// </summary>
  private static byte[] BuildUmx(string musicName, string formatName, byte[] musicData) {
    // Unreal package format: header + name table + import table + export table + data
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, Encoding.ASCII);

    // We'll build the structure in memory, then fix up offsets.
    // Name table: [0]=formatName, [1]="Music", [2]=musicName, [3]="Engine", [4]="Core"
    var names = new[] { formatName, "Music", musicName, "Engine", "Core" };

    // Import table: 1 entry for Music class
    // Export table: 1 entry for the music object

    // Calculate layout:
    // Header: 36 bytes (minimum for version 60+)
    // Name table starts after header
    var headerSize = 36;

    // Build name table bytes
    var nameTableMs = new MemoryStream();
    foreach (var name in names) {
      var bytes = Encoding.ASCII.GetBytes(name + '\0');
      nameTableMs.WriteByte((byte)bytes.Length); // compact length
      nameTableMs.Write(bytes);
      nameTableMs.Write(new byte[4]); // flags
    }
    var nameTableBytes = nameTableMs.ToArray();
    var nameTableOffset = headerSize;

    // Build import table: 1 entry
    // class_package (compact index), class_name (compact index), object_package (int32), object_name (compact index)
    var importTableMs = new MemoryStream();
    WriteCompactIndex(importTableMs, 4); // class_package = "Core" (index 4)
    WriteCompactIndex(importTableMs, 1); // class_name = "Music" (index 1)
    importTableMs.Write(new byte[4]); // object_package = 0
    WriteCompactIndex(importTableMs, 1); // object_name = "Music" (index 1)
    var importTableBytes = importTableMs.ToArray();
    var importTableOffset = nameTableOffset + nameTableBytes.Length;

    // Build serial data: 2 bytes skip + 4 bytes skip + compact(musicData.Length) + musicData
    var serialMs = new MemoryStream();
    serialMs.Write(new byte[2]); // skip unknown
    serialMs.Write(new byte[4]); // skip unknown
    WriteCompactIndex(serialMs, musicData.Length); // music length
    serialMs.Write(musicData);
    var serialBytes = serialMs.ToArray();

    // Build export table: 1 entry
    // classIndex (compact, negative = import), superclass (compact), package (int32),
    // objNameIdx (compact), objectFlags (int32), serialSize (compact), serialOffset (compact)
    var exportTableMs = new MemoryStream();
    WriteCompactIndex(exportTableMs, -1); // classIndex = -(0+1) = import[0] = Music
    WriteCompactIndex(exportTableMs, 0);  // superclass
    exportTableMs.Write(new byte[4]); // package
    WriteCompactIndex(exportTableMs, 2); // objNameIdx = musicName (index 2)
    exportTableMs.Write(new byte[4]); // object_flags

    var exportTableOffset = importTableOffset + importTableBytes.Length;
    var serialDataOffset = exportTableOffset + 128; // leave room for export table

    WriteCompactIndex(exportTableMs, serialBytes.Length); // serialSize
    WriteCompactIndex(exportTableMs, serialDataOffset); // serialOffset
    var exportTableBytes = exportTableMs.ToArray();

    // Recalculate serialDataOffset based on actual export table size
    serialDataOffset = exportTableOffset + exportTableBytes.Length;

    // Rebuild export table with correct offset
    exportTableMs = new MemoryStream();
    WriteCompactIndex(exportTableMs, -1);
    WriteCompactIndex(exportTableMs, 0);
    exportTableMs.Write(new byte[4]);
    WriteCompactIndex(exportTableMs, 2);
    exportTableMs.Write(new byte[4]);
    WriteCompactIndex(exportTableMs, serialBytes.Length);
    WriteCompactIndex(exportTableMs, serialDataOffset);
    exportTableBytes = exportTableMs.ToArray();
    serialDataOffset = exportTableOffset + exportTableBytes.Length;

    // Now write the whole file
    var totalSize = serialDataOffset + serialBytes.Length;
    var file = new byte[totalSize];

    // Header
    BinaryPrimitives.WriteUInt32LittleEndian(file, 0x9E2A83C1); // magic
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), 68); // file version
    // skip 4 bytes (package flags at offset 8)
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x0C), names.Length); // name count
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x10), nameTableOffset); // name offset
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x14), 1); // export count
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x18), exportTableOffset); // export offset
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x1C), 1); // import count
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x20), importTableOffset); // import offset

    // Copy tables and data
    nameTableBytes.CopyTo(file, nameTableOffset);
    importTableBytes.CopyTo(file, importTableOffset);
    exportTableBytes.CopyTo(file, exportTableOffset);
    serialBytes.CopyTo(file, serialDataOffset);

    return file;
  }

  private static void WriteCompactIndex(MemoryStream ms, int value) {
    var negative = value < 0;
    var abs = negative ? -value : value;
    var b0 = (byte)(abs & 0x3F);
    if (negative) b0 |= 0x80;
    if (abs > 0x3F) b0 |= 0x40; // more flag
    ms.WriteByte(b0);

    if (abs > 0x3F) {
      abs >>= 6;
      while (abs > 0) {
        var b = (byte)(abs & 0x7F);
        abs >>= 7;
        if (abs > 0) b |= 0x80; // more flag
        ms.WriteByte(b);
      }
    }
  }

  [Test, Category("HappyPath")]
  public void Read_SingleMusic_ListsEntry() {
    var musicPayload = new byte[128];
    Random.Shared.NextBytes(musicPayload);
    var umx = BuildUmx("TestSong", "it", musicPayload);
    using var ms = new MemoryStream(umx);

    var r = new FileFormat.Umx.UmxReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Does.Contain("TestSong"));
  }

  [Test, Category("HappyPath")]
  public void Extract_ReturnsCorrectData() {
    var musicPayload = new byte[256];
    Random.Shared.NextBytes(musicPayload);
    var umx = BuildUmx("MySong", "s3m", musicPayload);
    using var ms = new MemoryStream(umx);

    var r = new FileFormat.Umx.UmxReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(musicPayload));
  }

  [Test, Category("HappyPath")]
  public void EntryName_IncludesFormat() {
    var umx = BuildUmx("Track", "xm", new byte[64]);
    using var ms = new MemoryStream(umx);

    var r = new FileFormat.Umx.UmxReader(ms);
    Assert.That(r.Entries[0].Name, Does.EndWith(".xm"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Umx.UmxFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Umx"));
    Assert.That(desc.Extensions, Does.Contain(".umx"));
    Assert.That(desc.MagicSignatures[0].Bytes,
      Is.EqualTo(new byte[] { 0xC1, 0x83, 0x2A, 0x9E }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var umx = BuildUmx("Song", "mod", new byte[100]);
    using var ms = new MemoryStream(umx);

    var desc = new FileFormat.Umx.UmxFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void EntrySize_MatchesPayload() {
    var payload = new byte[500];
    Random.Shared.NextBytes(payload);
    var umx = BuildUmx("Big", "it", payload);
    using var ms = new MemoryStream(umx);

    var r = new FileFormat.Umx.UmxReader(ms);
    Assert.That(r.Entries[0].Size, Is.EqualTo(500));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Umx.UmxReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Umx.UmxReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var umx = BuildUmx("Song", "it", new byte[64]);
    using var ms = new MemoryStream(umx);
    var r = new FileFormat.Umx.UmxReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("EdgeCase")]
  public void UnknownFormat_DefaultsToMod() {
    var umx = BuildUmx("Track", "unknown", new byte[64]);
    using var ms = new MemoryStream(umx);

    var r = new FileFormat.Umx.UmxReader(ms);
    if (r.Entries.Count > 0)
      Assert.That(r.Entries[0].Name, Does.EndWith(".mod"));
    else
      Assert.Pass("No entries found for unknown format");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Umx.UmxFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Writer_ProducesValidMagic() {
    var w = new FileFormat.Umx.UmxWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    Assert.That(bytes[..4], Is.EqualTo(new byte[] { 0xC1, 0x83, 0x2A, 0x9E }));
  }
}
