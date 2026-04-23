using System.Globalization;
using System.Text;
using FileFormat.FirmwareHex;

namespace Compression.Tests.FirmwareHex;

[TestFixture]
public class FirmwareHexTests {

  // Test vector: 16 bytes at address 0x0000 containing 0x00..0x0F.
  private static readonly byte[] Expected = [
    0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
    0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
  ];

  // ── Intel HEX helpers ────────────────────────────────────────────

  private static string BuildIntelHex(byte[] data, uint baseAddr = 0) {
    var sb = new StringBuilder();
    var pos = 0;
    while (pos < data.Length) {
      var chunk = Math.Min(16, data.Length - pos);
      sb.Append(':');
      byte checksum = (byte)chunk;
      sb.Append(chunk.ToString("X2", CultureInfo.InvariantCulture));
      var addr = (ushort)(baseAddr + pos);
      checksum += (byte)(addr >> 8);
      checksum += (byte)addr;
      sb.Append(addr.ToString("X4", CultureInfo.InvariantCulture));
      sb.Append("00"); // data record type
      for (var i = 0; i < chunk; i++) {
        sb.Append(data[pos + i].ToString("X2", CultureInfo.InvariantCulture));
        checksum += data[pos + i];
      }
      sb.Append(((byte)(-checksum & 0xFF)).ToString("X2", CultureInfo.InvariantCulture));
      sb.AppendLine();
      pos += chunk;
    }
    sb.AppendLine(":00000001FF"); // EOF
    return sb.ToString();
  }

  private static string BuildSRecord(byte[] data, ushort baseAddr = 0) {
    var sb = new StringBuilder();
    // Always emit an S0 header record to satisfy S5/S9 convention.
    sb.AppendLine("S0030000FC");

    var pos = 0;
    while (pos < data.Length) {
      var chunk = Math.Min(16, data.Length - pos);
      var byteCount = (byte)(chunk + 3); // address (2) + data (chunk) + checksum (1)
      sb.Append("S1");
      sb.Append(byteCount.ToString("X2", CultureInfo.InvariantCulture));
      var addr = (ushort)(baseAddr + pos);
      sb.Append(addr.ToString("X4", CultureInfo.InvariantCulture));
      byte checksum = byteCount;
      checksum += (byte)(addr >> 8);
      checksum += (byte)addr;
      for (var i = 0; i < chunk; i++) {
        sb.Append(data[pos + i].ToString("X2", CultureInfo.InvariantCulture));
        checksum += data[pos + i];
      }
      sb.Append(((byte)(~checksum & 0xFF)).ToString("X2", CultureInfo.InvariantCulture));
      sb.AppendLine();
      pos += chunk;
    }
    // S9 termination, start addr = 0.
    sb.AppendLine("S9030000FC");
    return sb.ToString();
  }

  private static string BuildTiTxt(byte[] data, uint baseAddr = 0) {
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"@{baseAddr:X4}\n");
    for (var i = 0; i < data.Length; i++) {
      sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
      sb.Append(((i + 1) % 16 == 0 || i == data.Length - 1) ? '\n' : ' ');
    }
    sb.AppendLine("q");
    return sb.ToString();
  }

  // ── Intel HEX ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void IntelHex_DecodesToFlatBinary() {
    var image = IntelHexReader.Read(BuildIntelHex(Expected));
    Assert.That(image.ToFlatBinary(), Is.EqualTo(Expected).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void IntelHex_Descriptor_EmitsExpectedEntries() {
    var bytes = Encoding.ASCII.GetBytes(BuildIntelHex(Expected));
    using var ms = new MemoryStream(bytes);
    var entries = new IntelHexFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("firmware.bin"));
  }

  [Test, Category("EdgeCase")]
  public void IntelHex_RejectsMissingEof() {
    Assert.That(() => IntelHexReader.Read(":10000000000102030405060708090A0B0C0D0E0F68\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void IntelHex_RejectsBadChecksum() {
    Assert.That(() =>
      IntelHexReader.Read(":10000000000102030405060708090A0B0C0D0E0FFF\n:00000001FF\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── S-Record ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void SRecord_DecodesToFlatBinary() {
    var image = SRecordReader.Read(BuildSRecord(Expected));
    Assert.That(image.ToFlatBinary(), Is.EqualTo(Expected).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void SRecord_RejectsMissingTermination() {
    Assert.That(() => SRecordReader.Read("S0030000FC\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── TI-TXT ───────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void TiTxt_DecodesToFlatBinary() {
    var image = TiTxtReader.Read(BuildTiTxt(Expected));
    Assert.That(image.ToFlatBinary(), Is.EqualTo(Expected).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void TiTxt_RejectsMissingTerminator() {
    Assert.That(() => TiTxtReader.Read("@0000\n01 02 03 04\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Cross-format parity check ────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AllThreeFormats_ProduceIdenticalBinary() {
    var ihex = IntelHexReader.Read(BuildIntelHex(Expected)).ToFlatBinary();
    var srec = SRecordReader.Read(BuildSRecord(Expected)).ToFlatBinary();
    var titxt = TiTxtReader.Read(BuildTiTxt(Expected)).ToFlatBinary();
    Assert.Multiple(() => {
      Assert.That(ihex, Is.EqualTo(Expected).AsCollection);
      Assert.That(srec, Is.EqualTo(Expected).AsCollection);
      Assert.That(titxt, Is.EqualTo(Expected).AsCollection);
    });
  }
}
