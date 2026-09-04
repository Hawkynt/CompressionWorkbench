#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.FirmwareHex;

/// <summary>
/// The three firmware containers write what they read: a payload handed to
/// <see cref="IArchiveCreatable.Create"/> comes back byte for byte out of the
/// image, and the addresses and header fields the reader renders survive the
/// round trip.
/// </summary>
/// <remarks>
/// Intel HEX, TI-TXT and the legacy uImage are all fully specified and all three
/// were read-only, which made three tracked gaps in the support matrix that were
/// no harder to close than transcribing the specification. Each is a single
/// payload under a header or a base address, so they are WORM rather than R/W —
/// there is no second file to add to one.
/// </remarks>
[TestFixture]
[Category("HappyPath"), Category("RoundTrip")]
public sealed class FirmwareContainerWriteTests {

  private string _scratch = "";

  [OneTimeSetUp]
  public void CreateScratch() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    this._scratch = Path.Combine(Path.GetTempPath(), "cwb_fw_write_" + Guid.NewGuid().ToString("N")[..12]);
    Directory.CreateDirectory(this._scratch);
  }

  [OneTimeTearDown]
  public void RemoveScratch() {
    try { if (Directory.Exists(this._scratch)) Directory.Delete(this._scratch, recursive: true); } catch { /* best effort */ }
  }

  [TestCase("IntelHex", "firmware.bin")]
  [TestCase("TiTxt", "firmware.bin")]
  [TestCase("UImage", "payload.bin")]
  public void APayloadWrittenIn_ComesBackOut(string formatId, string payloadEntry) {
    var payload = new byte[1021];
    new Random(7).NextBytes(payload);

    var image = Create(formatId, [ArchiveInputInfo.InMemory(payloadEntry, payload)]);
    var back = this.Extract(formatId, image);

    Assert.That(back.ContainsKey(payloadEntry), Is.True,
      $"{formatId}: no '{payloadEntry}' in {string.Join(", ", back.Keys)}");
    Assert.That(back[payloadEntry], Is.EqualTo(payload), $"{formatId}: the payload changed.");
  }

  [TestCase("IntelHex")]
  [TestCase("TiTxt")]
  [TestCase("UImage")]
  public void AnEmptyPayload_StillWritesAValidImage(string formatId) {
    // Every one of the three has a terminator or a header that has to be there
    // whether or not any data is: the EOF record, the 'q' line, the 64 bytes.
    var image = Create(formatId, []);
    Assert.That(image, Is.Not.Empty, $"{formatId}: an empty payload produced no image.");
    Assert.That(() => this.Extract(formatId, image), Throws.Nothing,
      $"{formatId}: its own reader rejects the empty image it just wrote.");
  }

  [Test]
  public void IntelHex_PutsThePayloadAtTheBaseAddressTheMetadataNames() {
    var payload = "base-addressed"u8.ToArray();
    var image = Create("IntelHex", [
      ArchiveInputInfo.InMemory("metadata.ini", "[firmware_hex]\nbase_address = 0x08004000\nstart_address = 0x08004101\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("firmware.bin", payload),
    ]);

    var text = System.Text.Encoding.ASCII.GetString(image);
    Assert.Multiple(() => {
      // 0x0800 in the high half needs its own extended-linear-address record,
      // and the data record then carries the low half as its address field.
      Assert.That(text, Does.Contain(":020000040800F2"), "no extended-linear-address record for 0x0800xxxx");
      Assert.That(text, Does.Contain(":0400000508004101AD"), "no start-linear-address record for 0x08004101");
      Assert.That(text.TrimEnd(), Does.EndWith(":00000001FF"), "no end-of-file record");
    });

    var back = this.Extract("IntelHex", image);
    Assert.That(back["firmware.bin"], Is.EqualTo(payload));
    var metadata = System.Text.Encoding.UTF8.GetString(back["metadata.ini"]);
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("base_address = 0x08004000"));
      Assert.That(metadata, Does.Contain("start_address = 0x08004101"));
    });
  }

  [Test]
  public void TiTxt_PutsThePayloadUnderTheAddressLineTheMetadataNames() {
    var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    var image = Create("TiTxt", [
      ArchiveInputInfo.InMemory("metadata.ini", "[firmware_hex]\nbase_address = 0x0000C000\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("firmware.bin", payload),
    ]);

    var text = System.Text.Encoding.ASCII.GetString(image);
    Assert.Multiple(() => {
      Assert.That(text, Does.StartWith("@C000\n"));
      Assert.That(text, Does.Contain("DE AD BE EF"));
      Assert.That(text.TrimEnd(), Does.EndWith("q"));
    });
    Assert.That(this.Extract("TiTxt", image)["firmware.bin"], Is.EqualTo(payload));
  }

  [Test]
  public void UImage_ChecksumsWhatItWroteAndKeepsTheHeaderFields() {
    var payload = new byte[4096];
    new Random(11).NextBytes(payload);
    var image = Create("UImage", [
      ArchiveInputInfo.InMemory("metadata.ini",
        ("[uimage]\nname = probe-kernel\ntimestamp = 1700000000\nload_address = 0x80008000\n"
         + "entry_point = 0x80008040\nos = 5 (LINUX)\narch = 22 (ARM64)\ntype = 2 (KERNEL)\ncomp = 0 (none)\n"
        ).Select(c => (byte)c).ToArray()),
      ArchiveInputInfo.InMemory("payload.bin", payload),
    ]);

    var back = this.Extract("UImage", image);
    Assert.That(back["payload.bin"], Is.EqualTo(payload));

    var metadata = System.Text.Encoding.UTF8.GetString(back["metadata.ini"]);
    Assert.Multiple(() => {
      // Both CRCs are the point of the header; the reader recomputes them and
      // says whether they match what was stored.
      Assert.That(metadata, Does.Contain("header_crc_ok = true"));
      Assert.That(metadata, Does.Contain("data_crc_ok = true"));
      Assert.That(metadata, Does.Contain("name = probe-kernel"));
      Assert.That(metadata, Does.Contain("timestamp = 1700000000"));
      Assert.That(metadata, Does.Contain("load_address = 0x80008000"));
      Assert.That(metadata, Does.Contain("entry_point = 0x80008040"));
      Assert.That(metadata, Does.Contain("arch = 22 (ARM64)"));
      Assert.That(metadata, Does.Contain("comp = 0 (none)"));
      Assert.That(metadata, Does.Contain("data_size = 4096"));
    });
  }

  [Test]
  public void IntelHex_SplitsARecordThatWouldRunPastA64KBoundary() {
    // The address field of a data record is 16 bits wide, so a payload straddling
    // 0x1_0000 has to re-base rather than wrap round to zero.
    var payload = new byte[64];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i + 1);
    var image = Create("IntelHex", [
      ArchiveInputInfo.InMemory("metadata.ini", "[firmware_hex]\nbase_address = 0x0000FFE0\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("firmware.bin", payload),
    ]);

    var back = this.Extract("IntelHex", image);
    Assert.That(back["firmware.bin"], Is.EqualTo(payload),
      "the bytes either side of the 64 KiB boundary did not come back contiguous");
  }

  private static byte[] Create(string formatId, IReadOnlyList<ArchiveInputInfo> inputs) {
    var creator = FormatRegistry.GetArchiveOps(formatId) as IArchiveCreatable;
    Assert.That(creator, Is.Not.Null, $"{formatId} is not creatable.");
    using var image = new MemoryStream();
    creator!.Create(image, inputs, new FormatCreateOptions());
    return image.ToArray();
  }

  private Dictionary<string, byte[]> Extract(string formatId, byte[] image) {
    var ops = (IArchiveFormatOperations)FormatRegistry.GetArchiveOps(formatId)!;
    var outDir = Path.Combine(this._scratch, formatId + "_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using (var stream = new MemoryStream(image, writable: false))
        ops.Extract(stream, outDir, null, null);
      return Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }
}
