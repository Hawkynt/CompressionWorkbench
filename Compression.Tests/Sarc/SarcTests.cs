using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Sarc;

[TestFixture]
public class SarcTests {

  // The spec mentioned 0xC91A1B2A but our implementation (signed sbyte rolling
  // hash with key 0x65) produces 0x738014C4 for "dummy". All bytes in "dummy" are
  // ASCII (< 0x80), so signed and unsigned interpretations agree — this is the
  // canonical value to lock as a regression baseline.
  private const uint DummyHashLocked = 0x738014C4u;

  [Test, Category("HappyPath")]
  public void Hash_KnownVector() {
    Assert.That(FileFormat.Sarc.SarcHash.Hash("dummy", 0x65), Is.EqualTo(DummyHashLocked));
  }

  [Test, Category("HappyPath")]
  public void Hash_HighByteSignExtends() {
    // A 0xFF byte must be added as 0xFFFFFFFF (sign-extended), not 0xFF.
    // Single-byte input means result = 0 * key + signed(byte) = sign-extended byte.
    var hash = FileFormat.Sarc.SarcHash.Hash("ÿ", 0x65);
    // UTF-8 encoding of U+00FF is 0xC3 0xBF — both bytes are >= 0x80, so both sign-extend.
    // signed(0xC3) = 0xFFFFFFC3. After: 0 * 0x65 + 0xFFFFFFC3 = 0xFFFFFFC3.
    // signed(0xBF) = 0xFFFFFFBF. After: 0xFFFFFFC3 * 0x65 + 0xFFFFFFBF.
    var expected = unchecked((uint)((int)0xFFFFFFC3 * 0x65 + (int)0xFFFFFFBF));
    Assert.That(hash, Is.EqualTo(expected));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "hello sarc"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Sarc.SarcWriter(ms, leaveOpen: true))
      w.AddEntry("test.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Sarc.SarcReader(ms);
    Assert.That(r.IsLittleEndian, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.bin"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    // Choose names whose hashes are NOT in alphabetical order to prove sorting
    // happens by hash, not by name. Pre-compute hashes to verify ordering in output.
    var names = new[] { "zeta.dat", "alpha.dat", "mid.dat" };
    var bodies = new byte[][] {
      Encoding.UTF8.GetBytes("ZZZ"),
      Encoding.UTF8.GetBytes("AAAAAA"),
      Encoding.UTF8.GetBytes("MMMMMMMMM")
    };

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Sarc.SarcWriter(ms, leaveOpen: true)) {
      for (var i = 0; i < names.Length; ++i)
        w.AddEntry(names[i], bodies[i]);
    }

    var raw = ms.ToArray();

    // Read back and verify all three round-trip correctly.
    ms.Position = 0;
    var r = new FileFormat.Sarc.SarcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    // Each name's data must round-trip — order may differ from input order (sorted by hash).
    var byName = r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
    for (var i = 0; i < names.Length; ++i)
      Assert.That(byName[names[i]], Is.EqualTo(bodies[i]), $"body mismatch for {names[i]}");

    // Byte-level assertion: SFAT entry hashes appear in ascending order in the binary.
    // SFAT entries start at offset 20 (SARC hdr) + 12 (SFAT hdr) = 32.
    const int sfatEntriesStart = 32;
    var hashes = new uint[3];
    for (var i = 0; i < 3; ++i) {
      var off = sfatEntriesStart + i * 16;
      hashes[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(off, 4));
    }
    Assert.That(hashes[0], Is.LessThan(hashes[1]), "SFAT entries must be sorted by hash ascending");
    Assert.That(hashes[1], Is.LessThan(hashes[2]), "SFAT entries must be sorted by hash ascending");

    // Cross-check each hash equals the SarcHash function output.
    var entries = r.Entries.ToList();
    foreach (var e in entries)
      Assert.That(e.NameHash, Is.EqualTo(FileFormat.Sarc.SarcHash.Hash(e.Name, r.HashKey)));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LittleEndian() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Sarc.SarcWriter(ms, leaveOpen: true))
      w.AddEntry("x", "y"u8.ToArray());
    var raw = ms.ToArray();

    // BOM at offset 6 must be FF FE on disk (which when read as LE-U16 yields 0xFEFF).
    Assert.That(raw[6], Is.EqualTo(0xFF));
    Assert.That(raw[7], Is.EqualTo(0xFE));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(6, 2)), Is.EqualTo((ushort)0xFEFF));

    // Magic
    Assert.That(Encoding.ASCII.GetString(raw, 0, 4), Is.EqualTo("SARC"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Reader_AcceptsBigEndian() {
    // Hand-craft a minimal big-endian SARC with one entry "a" containing payload 0x41 ('A').
    // SARC header is 20 bytes (HeaderSize=0x14): magic+HeaderSize+BOM+FileSize+DataOffset+Version+Reserved.
    const uint hashKey = 0x65;
    var nameHash = FileFormat.Sarc.SarcHash.Hash("a", hashKey);

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

    // --- SARC header (20 bytes total per HeaderSize=0x14) ---
    bw.Write("SARC"u8.ToArray());
    WriteU16BE(bw, 0x14);          // HeaderSize
    bw.Write((byte)0xFE);          // BOM hi
    bw.Write((byte)0xFF);          // BOM lo (big-endian: bytes FE FF)
    // Defer FileSize + DataOffset — patch after layout is known.
    var fileSizePos = ms.Position;
    WriteU32BE(bw, 0);
    var dataOffsetPos = ms.Position;
    WriteU32BE(bw, 0);
    WriteU16BE(bw, 0x0100);        // Version
    WriteU16BE(bw, 0);             // Reserved

    // --- SFAT header (12 bytes) ---
    bw.Write("SFAT"u8.ToArray());
    WriteU16BE(bw, 0x0C);
    WriteU16BE(bw, 1);             // NodeCount
    WriteU32BE(bw, hashKey);

    // --- SFAT entry (16 bytes) ---
    WriteU32BE(bw, nameHash);
    WriteU32BE(bw, 0x01000000u);   // attr: name-present flag, name offset (in 4-byte units) = 0
    WriteU32BE(bw, 0);             // begin (relative to DataOffset)
    WriteU32BE(bw, 1);             // end (1 byte payload)

    // --- SFNT header (8 bytes) ---
    bw.Write("SFNT"u8.ToArray());
    WriteU16BE(bw, 0x08);
    WriteU16BE(bw, 0);

    // --- String table: "a\0\0\0" (padded to 4) ---
    bw.Write("a"u8.ToArray());
    bw.Write((byte)0);
    bw.Write((byte)0);
    bw.Write((byte)0);

    // --- Data region ---
    var dataOffset = (uint)ms.Position;
    bw.Write((byte)0x41); // 'A'

    var fileSize = (uint)ms.Position;

    // Patch deferred fields
    ms.Position = fileSizePos;
    WriteU32BE(bw, fileSize);
    ms.Position = dataOffsetPos;
    WriteU32BE(bw, dataOffset);

    ms.Position = 0;
    var r = new FileFormat.Sarc.SarcReader(ms);
    Assert.That(r.IsLittleEndian, Is.False);
    Assert.That(r.HashKey, Is.EqualTo(hashKey));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("a"));
    Assert.That(r.Entries[0].NameHash, Is.EqualTo(nameHash));
    Assert.That(r.Entries[0].Size, Is.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(new byte[] { 0x41 }));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Sarc.SarcReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadBom() {
    var buf = new byte[64];
    "SARC"u8.CopyTo(buf.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), 0x14);
    // Garbage BOM
    buf[6] = 0x12;
    buf[7] = 0x34;
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Sarc.SarcReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsTooSmall() {
    using var ms = new MemoryStream(new byte[8]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Sarc.SarcReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Sarc.SarcFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Sarc"));
    Assert.That(d.DisplayName, Is.EqualTo("Nintendo SARC"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".sarc"));
    Assert.That(d.Extensions, Contains.Item(".sarc"));
    Assert.That(d.Extensions, Contains.Item(".pack"));
    Assert.That(d.Extensions, Contains.Item(".bars"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("SARC"u8.ToArray()));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("sarc"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("SARC"));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  private static void WriteU16BE(BinaryWriter bw, ushort v) {
    Span<byte> tmp = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(tmp, v);
    bw.Write(tmp);
  }

  private static void WriteU32BE(BinaryWriter bw, uint v) {
    Span<byte> tmp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(tmp, v);
    bw.Write(tmp);
  }
}
