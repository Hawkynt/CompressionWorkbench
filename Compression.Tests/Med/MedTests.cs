#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Med;

namespace Compression.Tests.Med;

[TestFixture]
public class MedTests {

  // Layout: 28-byte header (magic at 0, smplarr pointer at offset 24) →
  // pointer array of two u32 instrument offsets → two InstrHdr blocks (u32 length, s16 type)
  // each followed by 8-bit signed data.
  private static byte[] MakeMmd0(out byte[] expectedPcm1) {
    var sample1 = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    expectedPcm1 = new byte[] { 128, 138, 118, 255 };

    var headerLen = 28;
    var smplArrOffset = headerLen;            // pointer array right after header
    var ptrArrLen = 3 * 4;                     // two pointers + a null terminator
    var instr1Off = smplArrOffset + ptrArrLen;
    var instr1Len = 6 + sample1.Length;        // hdr + data
    var instr2Off = instr1Off + instr1Len;
    var instr2DataLen = 4;
    var total = instr2Off + 6 + instr2DataLen;

    var buf = new byte[total];
    "MMD0"u8.ToArray().CopyTo(buf, 0);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), (uint)smplArrOffset);

    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(smplArrOffset + 0, 4), (uint)instr1Off);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(smplArrOffset + 4, 4), (uint)instr2Off);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(smplArrOffset + 8, 4), 0); // null terminator

    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(instr1Off, 4), (uint)sample1.Length);
    BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(instr1Off + 4, 2), 0); // type 0 = 8-bit
    sample1.CopyTo(buf, instr1Off + 6);

    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(instr2Off, 4), (uint)instr2DataLen);
    BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(instr2Off + 4, 2), 0); // type 0
    new byte[] { 1, 2, 3, 4 }.CopyTo(buf, instr2Off + 6);

    return buf;
  }

  [Test]
  public void List_SurfacesFullMetadataAndTwoSamples() {
    var blob = MakeMmd0(out _);
    using var ms = new MemoryStream(blob);
    var entries = new MedFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.mmd0").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Count(e => e.Kind == "Sample"), Is.EqualTo(2));
    Assert.That(entries.Any(e => e.Name == "samples/01_sample.wav"), Is.True);
  }

  [Test]
  public void Sample_DecodesToRebiasedUnsigned8WavAtAssumedRate() {
    var blob = MakeMmd0(out var expected);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new MedFormatDescriptor().ExtractEntry(ms, "samples/01_sample.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8363u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void Truncated_FallsBackToFullOnly() {
    var blob = "MMD0"u8.ToArray(); // header truncated before smplarr pointer
    using var ms = new MemoryStream(blob);
    var entries = new MedFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.mmd0"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False);
  }
}
