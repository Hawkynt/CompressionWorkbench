using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Aomei;

[TestFixture]
public class AomeiTests {

  /// <summary>Synthesises a minimal AOMEI image with the BIFH\ magic at offset 0.</summary>
  private static byte[] BuildMinimal(uint postMagic = 0xDEADBEEF, int totalSize = 512) {
    var image = new byte[totalSize];
    // BIFH\ = 0x42 0x49 0x46 0x48 0x5C
    image[0] = 0x42; image[1] = 0x49; image[2] = 0x46; image[3] = 0x48; image[4] = 0x5C;
    if (totalSize >= 9)
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(5, 4), postMagic);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Aomei.AomeiFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Aomei"));
    Assert.That(d.DisplayName, Does.Contain("AOMEI"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".adi"));
    Assert.That(d.Extensions, Does.Contain(".adi"));
    Assert.That(d.Extensions, Does.Contain(".afi"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x42, 0x49, 0x46, 0x48, 0x5C }));
  }

  [Test, Category("HappyPath")]
  public void Reader_Magic_Constant_IsBifhBackslash() {
    Assert.That(FileFormat.Aomei.AomeiReader.Magic,
                Is.EqualTo(new byte[] { 0x42, 0x49, 0x46, 0x48, 0x5C }));
    // ASCII sanity — "BIFH\"
    Assert.That((char)FileFormat.Aomei.AomeiReader.Magic[0], Is.EqualTo('B'));
    Assert.That((char)FileFormat.Aomei.AomeiReader.Magic[1], Is.EqualTo('I'));
    Assert.That((char)FileFormat.Aomei.AomeiReader.Magic[2], Is.EqualTo('F'));
    Assert.That((char)FileFormat.Aomei.AomeiReader.Magic[3], Is.EqualTo('H'));
    Assert.That((char)FileFormat.Aomei.AomeiReader.Magic[4], Is.EqualTo('\\'));
    Assert.That(FileFormat.Aomei.AomeiReader.HeaderCaptureSize, Is.EqualTo(64));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsHeaderSurface_OnValidMagic() {
    using var ms = new MemoryStream(BuildMinimal());
    var d = new FileFormat.Aomei.AomeiFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.bifh"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("header.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader_OnValidMagic() {
    using var ms = new MemoryStream(BuildMinimal(postMagic: 0x01020304));
    var d = new FileFormat.Aomei.AomeiFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "aomei_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.bifh")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "header.bin")), Is.True);

      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      // 512-byte sample is shorter than the spec's 0x65C BIFH head, so the
      // reader stays at the bare-magic surface and reports header_short.
      Assert.That(meta, Does.Contain("parse_status=header_short"));
      Assert.That(meta, Does.Contain("magic=BIFH\\"));
      Assert.That(meta, Does.Contain("post_magic_u32_le=0x01020304"));
      Assert.That(meta, Does.Contain("head_body_layout=undocumented_past_first_12_bytes"));
      Assert.That(meta, Does.Contain("tail_body_layout=carries_DataOffInSet_u64"));
      Assert.That(meta, Does.Contain("index_body_layout=BR_IMAGE_INDEX_header_pinned"));

      var headerRaw = File.ReadAllBytes(Path.Combine(outDir, "header.bin"));
      Assert.That(headerRaw.Length, Is.EqualTo(FileFormat.Aomei.AomeiReader.HeaderCaptureSize));
      Assert.That(headerRaw[..5],
                  Is.EqualTo(new byte[] { 0x42, 0x49, 0x46, 0x48, 0x5C }));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow_AndOmitsHeaderBin() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileFormat.Aomei.AomeiFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.bifh"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("header.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    // Random non-magic bytes — first 5 bytes are not BIFH\.
    var buf = new byte[256];
    for (var i = 0; i < buf.Length; i++) buf[i] = (byte)(i ^ 0xA5);
    // Belt-and-braces: stomp the magic region with zeros so no accidental match.
    for (var i = 0; i < 5; i++) buf[i] = 0;
    using var ms = new MemoryStream(buf);
    var d = new FileFormat.Aomei.AomeiFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.bifh"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("header.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_GarbageInput_WritesPartialMetadata() {
    var buf = new byte[64];
    // Clear the magic region.
    for (var i = 0; i < 5; i++) buf[i] = 0;
    using var ms = new MemoryStream(buf);
    var d = new FileFormat.Aomei.AomeiFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "aomei_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "header.bin")), Is.False);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("Boundary")]
  public void Reader_ShortHeader_OnlyMagic_StillValid() {
    // Exactly 5 bytes — magic only, no post-magic word.
    var buf = new byte[] { 0x42, 0x49, 0x46, 0x48, 0x5C };
    using var ms = new MemoryStream(buf);
    var r = new FileFormat.Aomei.AomeiReader(ms);
    // Bare magic surface still detects the family — the parse status reports
    // header_short because the input is much shorter than the spec's 0x65C
    // BIFH head, which is the honest description.
    Assert.That(r.Valid, Is.True);
    Assert.That(r.ParseStatus, Is.EqualTo("header_short"));
    Assert.That(r.PostMagicWord, Is.EqualTo(0u));
  }

  [Test, Category("Boundary")]
  public void Reader_FourByteHeader_TooShort_IsPartial() {
    // 4 bytes — short of the 5-byte magic.
    var buf = new byte[] { 0x42, 0x49, 0x46, 0x48 };
    using var ms = new MemoryStream(buf);
    var r = new FileFormat.Aomei.AomeiReader(ms);
    Assert.That(r.Valid, Is.False);
    Assert.That(r.ParseStatus, Is.EqualTo("partial"));
  }

  [Test, Category("EquivalenceClass")]
  public void Descriptor_BothAdiAndAfi_SameMagicAccepted() {
    // Same magic, same format — verify both extensions are registered and the
    // descriptor doesn't care which extension the file uses.
    var d = new FileFormat.Aomei.AomeiFormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".adi"));
    Assert.That(d.Extensions, Does.Contain(".afi"));
    // List() is extension-agnostic — only the bytes matter.
    using var ms = new MemoryStream(BuildMinimal());
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "header.bin"), Is.True);
  }
}
