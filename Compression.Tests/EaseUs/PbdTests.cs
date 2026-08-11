using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FileFormat.EaseUs;

namespace Compression.Tests.EaseUs;

[TestFixture]
public class PbdTests {

  private const ushort FormatVersion = 0x0003;

  // Synthesise a .pbd container: 16-byte fixed header + optional embedded
  // raw-DEFLATE chunks prefixed with a zlib (CMF=0x78, FLG=0x9C) wrapper so
  // PbdChunkScanner has something to find. The block-allocation index is
  // intentionally left as random padding — we do not parse it.
  private static byte[] BuildSyntheticPbd(string magic, ushort flags, params byte[][] payloads) {
    using var ms = new MemoryStream();

    // Header.
    var hdr = new byte[16];
    Encoding.ASCII.GetBytes(magic).CopyTo(hdr.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(4, 2), FormatVersion);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(6, 2), flags);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8, 4), 64); // header_size
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12, 4), 0); // reserved
    ms.Write(hdr);

    // Padding up to header_size = 64 to mimic the opaque index region.
    var padding = new byte[64 - 16];
    new Random(1234).NextBytes(padding);
    ms.Write(padding);

    // Embed each payload as a zlib-wrapped DEFLATE stream so the scanner
    // produces a hit. We hand-write the zlib wrapper (CMF=0x78, FLG=0x9C
    // satisfies the FCHECK invariant) so the layout matches the real
    // on-disk format the scanner walks.
    foreach (var payload in payloads) {
      ms.WriteByte(0x78);
      ms.WriteByte(0x9C);
      using var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true);
      deflate.Write(payload, 0, payload.Length);
    }

    return ms.ToArray();
  }

  // ── header surfacing ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_ImgfVariant_SurfacesCanonicalEntries() {
    var data = BuildSyntheticPbd("IMGF", flags: 0, "hello world"u8.ToArray());
    using var ms = new MemoryStream(data);
    var entries = new PbdFormatDescriptor().List(ms, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.pbd"), Is.True, "FULL.pbd missing");
      Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True, "metadata.ini missing");
      Assert.That(entries.Any(e => e.Name == "header.bin"), Is.True, "header.bin missing");
      // chunk_00 has a deterministic name pattern.
      Assert.That(entries.Any(e => e.Name.StartsWith("chunks/chunk_00_at_", StringComparison.Ordinal)),
        Is.True, "no scanner-discovered chunk surfaced");
    });
  }

  [Test, Category("HappyPath")]
  public void List_FimgVariant_RecognisedAsFimg() {
    var data = BuildSyntheticPbd("FIMG", flags: 0, "abc"u8.ToArray());
    var tmp = Path.Combine(Path.GetTempPath(), "pbd_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PbdFormatDescriptor().Extract(ms, tmp, null, null);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("variant = FIMG"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFullPbdMetadataHeaderAndChunks() {
    var payload = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
    var data = BuildSyntheticPbd("IMGF", flags: 0, payload);
    var tmp = Path.Combine(Path.GetTempPath(), "pbd_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PbdFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(tmp, "FULL.pbd")), Is.True);
        Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
        Assert.That(File.Exists(Path.Combine(tmp, "header.bin")), Is.True);
      });

      var chunkDir = Path.Combine(tmp, "chunks");
      Assert.That(Directory.Exists(chunkDir), Is.True, "chunks directory missing");
      var chunkFiles = Directory.GetFiles(chunkDir);
      Assert.That(chunkFiles, Is.Not.Empty);

      // The first decompressed chunk must reproduce the original payload
      // byte-for-byte — that's the round-trip guarantee for stage-0
      // extraction.
      var firstChunk = File.ReadAllBytes(chunkFiles[0]);
      Assert.That(firstChunk, Is.EqualTo(payload));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  // ── metadata content ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Metadata_RecordsVariantVersionFlagsAndChunkCount() {
    var data = BuildSyntheticPbd("IMGF", flags: 0x0002, "a"u8.ToArray(), "b"u8.ToArray());
    var tmp = Path.Combine(Path.GetTempPath(), "pbd_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PbdFormatDescriptor().Extract(ms, tmp, null, null);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));

      Assert.Multiple(() => {
        Assert.That(ini, Does.Contain("variant = IMGF"));
        Assert.That(ini, Does.Contain("format_version = 3"));
        Assert.That(ini, Does.Contain("flag_encrypted = 0"));
        Assert.That(ini, Does.Contain("flag_incremental = 1"));
        Assert.That(ini, Does.Contain("zlib_chunks_found = 2"));
        Assert.That(ini, Does.Contain("encryption_hint = unencrypted"));
        Assert.That(ini, Does.Contain("block_allocation_index = opaque"));
        Assert.That(ini, Does.Contain("sector_reconstruction = unsupported"));
      });
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Metadata_EncryptedFlagWithNoChunks_HintsEncrypted() {
    // Synthesise an "encrypted" backup: flag set, body is random bytes that
    // can't be inflated as zlib.
    var rng = new Random(42);
    var data = new byte[16 + 4096];
    Encoding.ASCII.GetBytes("IMGF").CopyTo(data.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), FormatVersion);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6, 2), 0x0001); // encrypted
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 0);
    // Random body — make sure no byte happens to be 0x78 followed by a
    // FCHECK-valid byte to avoid a stray false-positive scanner hit.
    for (var i = 16; i < data.Length; i++) {
      byte b;
      do { b = (byte)rng.Next(256); } while (b == 0x78);
      data[i] = b;
    }

    var tmp = Path.Combine(Path.GetTempPath(), "pbd_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PbdFormatDescriptor().Extract(ms, tmp, null, null);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));

      Assert.Multiple(() => {
        Assert.That(ini, Does.Contain("flag_encrypted = 1"));
        Assert.That(ini, Does.Contain("zlib_chunks_found = 0"));
        Assert.That(ini, Does.Contain("encryption_hint = encrypted"));
        Assert.That(ini, Does.Contain("encrypted_payload_decryption = unsupported"));
      });
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  // ── descriptor surface ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesBothImgfAndFimgMagic() {
    var d = new PbdFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("EaseUsPbd"));
      Assert.That(d.Extensions, Does.Contain(".pbd"));
      Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
      // IMGF = 0x49 0x4D 0x47 0x46
      Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x49, 0x4D, 0x47, 0x46 }));
      // FIMG = 0x46 0x49 0x4D 0x47
      Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(new byte[] { 0x46, 0x49, 0x4D, 0x47 }));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DoesNotAdvertiseCreateOrModify() {
    // Honest stage-0: never advertise CanCreate/CanModify for a format whose
    // trailer index we cannot honestly produce. Round-tripping a sector
    // image would lie to readers.
    var d = new PbdFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.False);
      Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.False);
      Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
      Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    });
  }

  // ── edge cases ──────────────────────────────────────────────────────

  [Test, Category("EdgeCase")]
  public void List_NonPbdStream_ReturnsOnlyFullPbdPassthrough() {
    // Stream that doesn't start with IMGF/FIMG → BuildSynthetic returns
    // empty; only the FULL.pbd passthrough entry remains.
    var data = new byte[64];
    Encoding.ASCII.GetBytes("NOTAPBDFILE").CopyTo(data.AsSpan());
    using var ms = new MemoryStream(data);
    var entries = new PbdFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.pbd"));
  }

  [Test, Category("EdgeCase")]
  public void List_TooShortStream_ReturnsOnlyFullPbdPassthrough() {
    // Anything under 16 bytes can't even hold the fixed header.
    var data = new byte[8];
    using var ms = new MemoryStream(data);
    var entries = new PbdFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.pbd"));
  }

  [Test, Category("HappyPath")]
  public void List_FilteredExtract_OnlyEmitsRequestedFiles() {
    var data = BuildSyntheticPbd("IMGF", flags: 0, "xyz"u8.ToArray());
    var tmp = Path.Combine(Path.GetTempPath(), "pbd_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PbdFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);
      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
        Assert.That(File.Exists(Path.Combine(tmp, "FULL.pbd")), Is.False);
        Assert.That(File.Exists(Path.Combine(tmp, "header.bin")), Is.False);
      });
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }
}
