#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Sid;

namespace Compression.Tests.Sid;

[TestFixture]
[Category("Slow")]
public class MultiSidTests {

  // Builds a renderable PSID with init writing a tone to one or more SID windows. The program
  // loads at $1000 (embedded LE load address), init at $1000, play (RTS) at $1040.
  private static byte[] BuildRenderable(ushort version, ushort flags,
      (ushort SidBase, int FreqReg, byte Wave)[] tones, byte secondSid = 0, byte thirdSid = 0) {
    var program = new List<byte> { 0x00, 0x10 }; // embedded load addr $1000
    void Set(ushort sidBase, byte reg, byte value) {
      var addr = (ushort)(sidBase + reg);
      program.AddRange([0xA9, value, 0x8D, (byte)(addr & 0xFF), (byte)(addr >> 8)]);
    }
    foreach (var (sidBase, freqReg, wave) in tones) {
      Set(sidBase, 0x00, (byte)(freqReg & 0xFF));
      Set(sidBase, 0x01, (byte)(freqReg >> 8));
      Set(sidBase, 0x06, 0xF0);
      Set(sidBase, 0x18, 0x0F);
      Set(sidBase, 0x04, wave);
    }
    program.Add(0x60);                              // init RTS
    while (program.Count < 2 + 0x40) program.Add(0xEA);
    program.Add(0x60);                              // play RTS at $1040

    const int header = 0x7C;
    var blob = new byte[header + program.Count];
    Encoding.ASCII.GetBytes("PSID").CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), version);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), header);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), 0);      // loadAddr 0 → embedded
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), 0x1000);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), 0x1040);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    blob[0x7A] = secondSid;
    blob[0x7B] = thirdSid;
    program.CopyTo(blob, header);
    return blob;
  }

  // A header-only PSID (no renderable program) for metadata-parsing assertions.
  private static byte[] BuildHeader(ushort version, ushort flags, byte secondSid = 0, byte thirdSid = 0) {
    const int header = 0x7C;
    var blob = new byte[header + 3];
    Encoding.ASCII.GetBytes("PSID").CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), version);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), header);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), 0x1000);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), 0x1003);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), 0x1006);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    blob[0x7A] = secondSid;
    blob[0x7B] = thirdSid;
    return blob;
  }

  private static List<string> Names(byte[] blob) {
    using var ms = new MemoryStream(blob);
    return new SidFormatDescriptor().List(ms, null).Select(e => e.Name).ToList();
  }

  private static string Meta(byte[] blob) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SidFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    return Encoding.UTF8.GetString(output.ToArray());
  }

  private static byte[] Wav(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SidFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  private static int WavPeak(byte[] wav) {
    var peak = 0;
    for (var i = 44; i + 1 < wav.Length; i += 2)
      peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i))));
    return peak;
  }

  private const int Freq440 = (int)(440.0 * 16777216.0 / 985248.0);
  private const int Freq660 = (int)(660.0 * 16777216.0 / 985248.0);

  // ---- model-flag parsing matrix ----

  [Test]
  public void Sid1ModelFlags_DecodeAllCombos() {
    Assert.That(Meta(BuildHeader(2, 0x0004 | 0x00 << 4)), Does.Contain("sid_model=unknown"));
    Assert.That(Meta(BuildHeader(2, 0x0004 | 0x01 << 4)), Does.Contain("sid_model=MOS6581"));
    Assert.That(Meta(BuildHeader(2, 0x0004 | 0x02 << 4)), Does.Contain("sid_model=MOS8580"));
    Assert.That(Meta(BuildHeader(2, 0x0004 | 0x03 << 4)), Does.Contain("sid_model=MOS6581/8580 (both)"));
  }

  [Test]
  public void Sid2And3ModelFlags_DecodeIncludingLikeSid1() {
    // v3: SID2 model bits 6-7. v4: SID3 bits 8-9. 00 = "same as SID1".
    var v3 = Meta(BuildHeader(3, 0x0004 | 0x01 << 4 | 0x02 << 6, secondSid: 0x42));
    Assert.That(v3, Does.Contain("sid2_model=MOS8580"));

    var v3same = Meta(BuildHeader(3, 0x0004 | 0x01 << 4 | 0x00 << 6, secondSid: 0x42));
    Assert.That(v3same, Does.Contain("sid2_model=same as SID1"));

    var v4 = Meta(BuildHeader(4, 0x0004 | 0x01 << 4 | 0x02 << 6 | 0x01 << 8,
      secondSid: 0x42, thirdSid: 0x44));
    Assert.That(v4, Does.Contain("sid3_model=MOS6581"));
  }

  // ---- address validation ----

  [Test]
  public void SecondSidAddress_ValidEvenInRange_IsAccepted() {
    var meta = Meta(BuildHeader(3, 0x0014, secondSid: 0x42)); // $D420
    Assert.That(meta, Does.Contain("second_sid_addr=0xD420"));
    Assert.That(meta, Does.Not.Contain("second_sid_addr_invalid"));
  }

  [Test]
  public void SecondSidAddress_OddByte_IsRejected() {
    var meta = Meta(BuildHeader(3, 0x0014, secondSid: 0x43)); // odd → invalid
    Assert.That(meta, Does.Contain("second_sid_addr_invalid=true"));
  }

  [Test]
  public void SecondSidAddress_OutOfRange_IsRejected() {
    var meta = Meta(BuildHeader(3, 0x0014, secondSid: 0x20)); // below $42 → invalid
    Assert.That(meta, Does.Contain("second_sid_addr_invalid=true"));
  }

  [Test]
  public void ThirdSidAddress_HighRangeEven_IsAccepted() {
    var meta = Meta(BuildHeader(4, 0x0014, secondSid: 0x42, thirdSid: 0xE0)); // $DE00
    Assert.That(meta, Does.Contain("third_sid_addr=0xDE00"));
    Assert.That(meta, Does.Not.Contain("third_sid_addr_invalid"));
  }

  // ---- end-to-end stereo naming + routing ----

  [Test]
  public void TwoSid_ToneOnSid1Only_LeftLoudRightSilent() {
    // SID2 at $D420, model 8580. Tone written to SID1 only.
    var flags = (ushort)(0x0004 | 0x01 << 4 | 0x02 << 6); // PAL, SID1=6581, SID2=8580
    var blob = BuildRenderable(3, flags, [(0xD400, Freq440, 0x21)], secondSid: 0x42);

    var names = Names(blob);
    Assert.That(names, Does.Contain("LEFT.wav"));
    Assert.That(names, Does.Contain("RIGHT.wav"));
    Assert.That(names, Does.Not.Contain("MONO.wav"));

    Assert.That(WavPeak(Wav(blob, "LEFT.wav")), Is.GreaterThan(1000), "LEFT (SID1) has the tone");
    Assert.That(WavPeak(Wav(blob, "RIGHT.wav")), Is.LessThan(50), "RIGHT (SID2) silent");
  }

  [Test]
  public void TwoSid_DifferentPerChipModels_BothSidesNonSilentAndSpectrallyDifferentFromControl() {
    // SID1=6581 + SID2=8580; write a filtered tone to BOTH so the model's filter curve matters.
    // Then a same-model control (SID1=8580 + SID2=8580) — the LEFT channel must differ.
    var diff = BuildFilteredStereo(sid1Is8580: false);   // 6581 + 8580
    var ctrl = BuildFilteredStereo(sid1Is8580: true);    // 8580 + 8580

    var diffLeft = Wav(diff, "LEFT.wav");
    var diffRight = Wav(diff, "RIGHT.wav");
    var ctrlLeft = Wav(ctrl, "LEFT.wav");

    Assert.That(WavPeak(diffLeft), Is.GreaterThan(500), "LEFT non-silent");
    Assert.That(WavPeak(diffRight), Is.GreaterThan(500), "RIGHT non-silent");

    // Spectral/energy difference between a 6581-left render and an 8580-left render.
    var eDiff = Energy(diffLeft);
    var eCtrl = Energy(ctrlLeft);
    var rel = Math.Abs(eDiff - eCtrl) / Math.Max(1.0, Math.Max(eDiff, eCtrl));
    Assert.That(rel, Is.GreaterThan(0.02),
      $"6581-left energy={eDiff:0} vs 8580-left energy={eCtrl:0} should differ");
  }

  // Builds a 2SID renderable where BOTH chips get a filtered (low-pass routed) saw, so the
  // model's cutoff curve audibly shapes the output. SID2 at $D420.
  private static byte[] BuildFilteredStereo(bool sid1Is8580) {
    var program = new List<byte> { 0x00, 0x10 };
    void Set(ushort sidBase, byte reg, byte value) {
      var addr = (ushort)(sidBase + reg);
      program.AddRange([0xA9, value, 0x8D, (byte)(addr & 0xFF), (byte)(addr >> 8)]);
    }
    void Setup(ushort baseAddr) {
      var fr = Freq440;
      Set(baseAddr, 0x00, (byte)(fr & 0xFF));
      Set(baseAddr, 0x01, (byte)(fr >> 8));
      Set(baseAddr, 0x06, 0xF0);
      Set(baseAddr, 0x15, 0x00);   // FC lo
      Set(baseAddr, 0x16, 0x40);   // FC hi (mid cutoff)
      Set(baseAddr, 0x17, 0x01);   // route voice 1 through filter
      Set(baseAddr, 0x18, 0x1F);   // low-pass + full volume
      Set(baseAddr, 0x04, 0x21);   // saw + gate
    }
    Setup(0xD400);
    Setup(0xD420);
    program.Add(0x60);
    while (program.Count < 2 + 0x40) program.Add(0xEA);
    program.Add(0x60);

    const int header = 0x7C;
    var blob = new byte[header + program.Count];
    Encoding.ASCII.GetBytes("PSID").CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), 3);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), header);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), 0x1000);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), 0x1040);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);
    // SID1 model 6581 or 8580; SID2 always 8580.
    var sid1 = sid1Is8580 ? 0x02 : 0x01;
    var flags = (ushort)(0x0004 | sid1 << 4 | 0x02 << 6);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    blob[0x7A] = 0x42; // SID2 at $D420
    program.CopyTo(blob, header);
    return blob;
  }

  private static double Energy(byte[] wav) {
    var sum = 0.0;
    var n = 0;
    for (var i = 44; i + 1 < wav.Length; i += 2) {
      var s = (double)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i));
      sum += s * s; ++n;
    }
    return n == 0 ? 0 : sum / n;
  }

  [Test]
  public void ThreeSid_SurfacesLeftRightCenter() {
    var flags = (ushort)(0x0004 | 0x01 << 4); // PAL, SID1=6581
    var blob = BuildRenderable(4, flags,
      [(0xD400, Freq440, 0x21), (0xD420, Freq660, 0x21), (0xD440, Freq440, 0x21)],
      secondSid: 0x42, thirdSid: 0x44);

    var names = Names(blob);
    Assert.That(names, Does.Contain("LEFT.wav"));
    Assert.That(names, Does.Contain("RIGHT.wav"));
    Assert.That(names, Does.Contain("CENTER.wav"));
    Assert.That(WavPeak(Wav(blob, "CENTER.wav")), Is.GreaterThan(1000));
  }

  // ---- dual-model render for unknown/either ----

  [Test]
  public void UnknownModelMono_RendersBoth6581And8580_AndTheyDiffer() {
    // SID1 model flag 00 (unknown) → render both. Use a filtered tone so the models differ.
    var program = new List<byte> { 0x00, 0x10 };
    void Set(byte reg, byte value) => program.AddRange([0xA9, value, 0x8D, reg, 0xD4]);
    Set(0x00, (byte)(Freq440 & 0xFF));
    Set(0x01, (byte)(Freq440 >> 8));
    Set(0x06, 0xF0);
    Set(0x15, 0x00); Set(0x16, 0x40); Set(0x17, 0x01); // filter mid cutoff, route v1
    Set(0x18, 0x1F);                                    // LP + full vol
    Set(0x04, 0x21);
    program.Add(0x60);
    while (program.Count < 2 + 0x40) program.Add(0xEA);
    program.Add(0x60);

    const int header = 0x7C;
    var blob = new byte[header + program.Count];
    Encoding.ASCII.GetBytes("PSID").CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), 2);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), header);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), 0x1000);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), 0x1040);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), 0x0004); // PAL, SID1 model = 00 unknown
    program.CopyTo(blob, header);

    var names = Names(blob);
    Assert.That(names, Does.Contain("MONO_6581.wav"));
    Assert.That(names, Does.Contain("MONO_8580.wav"));
    Assert.That(names, Does.Not.Contain("MONO.wav"));

    var e6581 = Energy(Wav(blob, "MONO_6581.wav"));
    var e8580 = Energy(Wav(blob, "MONO_8580.wav"));
    var rel = Math.Abs(e6581 - e8580) / Math.Max(1.0, Math.Max(e6581, e8580));
    Assert.That(rel, Is.GreaterThan(0.02), $"6581 energy={e6581:0} vs 8580 energy={e8580:0} should differ");

    Assert.That(Meta(blob), Does.Contain("rendered_model=both"));
  }
}
