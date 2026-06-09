#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using CompressionWorkbench.FileFormat.Ani;
using CompressionWorkbench.FileFormat.Ico;

namespace Compression.Tests.Ani;

[TestFixture]
public class AniWriterTests {

  // Minimal 8×8 32-bit BMP, used to synthesise CUR test inputs.
  private static byte[] MinimalBmp() {
    const int fileHeader = 14, infoHeader = 40, pixelBytes = 8 * 8 * 4;
    var fileLen = fileHeader + infoHeader + pixelBytes;
    var data = new byte[fileLen];
    data[0] = (byte)'B'; data[1] = (byte)'M';
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2, 4), (uint)fileLen);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10, 4), fileHeader + infoHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14, 4), infoHeader);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18, 4), 8);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22, 4), 8);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28, 2), 32);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34, 4), pixelBytes);
    for (var i = fileHeader + infoHeader; i < fileLen; i++) data[i] = 0x77;
    return data;
  }

  private static byte[] MakeCur() => IcoWriter.BuildCur([new IcoWriter.Image(MinimalBmp())]);

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new AniFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Write_EmitsRiffAconWrapper() {
    using var ms = new MemoryStream();
    AniWriter.Write(ms, [MakeCur()]);
    var blob = ms.ToArray();
    Assert.That(blob[0..4], Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(blob[8..12], Is.EqualTo("ACON"u8.ToArray()));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_TwoFrames_ReadsBackThroughAniReader() {
    var cur1 = MakeCur();
    var cur2 = MakeCur();
    using var ms = new MemoryStream();
    AniWriter.Write(ms, [cur1, cur2], defaultJiffies: 12);

    var ani = AniReader.Read(ms.ToArray());
    Assert.That(ani.Header.NumFrames, Is.EqualTo(2u));
    Assert.That(ani.Header.NumSteps, Is.EqualTo(2u));
    Assert.That(ani.Header.DefaultJiffiesPerStep, Is.EqualTo(12u));
    Assert.That(ani.Frames, Has.Count.EqualTo(2));
    Assert.That(ani.Frames[0], Is.EqualTo(cur1));
    Assert.That(ani.Frames[1], Is.EqualTo(cur2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Write_OptionalInfo_RoundTripsTitleArtist() {
    using var ms = new MemoryStream();
    AniWriter.Write(ms, [MakeCur()], title: "Demo", artist: "Workbench");
    var ani = AniReader.Read(ms.ToArray());
    Assert.That(ani.Title, Is.EqualTo("Demo"));
    Assert.That(ani.Artist, Is.EqualTo("Workbench"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughDescriptor() {
    var d = new AniFormatDescriptor();
    var cur = MakeCur();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("frame_000.cur", cur),
      ArchiveInputInfo.InMemory("frame_001.cur", cur),
    };
    using var outStream = new MemoryStream();
    d.Create(outStream, inputs, new FormatCreateOptions());

    var ani = AniReader.Read(outStream.ToArray());
    Assert.That(ani.Frames, Has.Count.EqualTo(2));
  }

  // Boundary: non-CUR/ICO inputs are rejected so the writer never emits broken frames.
  [Test, Category("Exception")]
  public void Descriptor_Create_NonIcoInput_Throws() {
    using var outStream = new MemoryStream();
    Assert.That(
      () => new AniFormatDescriptor().Create(
        outStream,
        [ArchiveInputInfo.InMemory("not_a_cursor.txt", "plain text"u8.ToArray())],
        new FormatCreateOptions()),
      Throws.ArgumentException);
  }

  // Equivalence: rates chunk emitted when supplied.
  [Test, Category("HappyPath")]
  public void Write_WithRates_EmitsRateChunkRoundTrip() {
    using var ms = new MemoryStream();
    AniWriter.Write(ms, [MakeCur(), MakeCur()], rates: [10u, 20u]);
    var ani = AniReader.Read(ms.ToArray());
    Assert.That(ani.Rates, Is.EqualTo(new[] { 10u, 20u }));
  }
}
