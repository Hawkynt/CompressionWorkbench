#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Dng;

namespace Compression.Tests.Dng;

/// <summary>
/// The owning <see cref="DngReader"/> and the zero-copy span path share one IFD
/// walker. These cases pin TIFF 6.0 strip semantics (StripOffsets/StripByteCounts
/// pairs joined in order, SHORT and LONG arrays, both byte orders) and the
/// boundaries a malformed file must not turn into an exception.
/// </summary>
[TestFixture]
public class DngStripLayoutTests {

  private const ushort TypeByte = 1, TypeShort = 3, TypeLong = 4;

  private sealed record Tag(ushort Id, ushort Type, uint Count, uint ValueOrOffset);

  /// <summary>Header + one IFD with the given entries + a data area at <paramref name="dataStart"/>.</summary>
  private static byte[] Tiff(bool bigEndian, IReadOnlyList<Tag> tags, int dataStart, byte[] data) {
    var image = new byte[Math.Max(dataStart + data.Length, 8 + 2 + tags.Count * 12 + 4)];
    void U16(int at, ushort v) { if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(at), v); else BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), v); }
    void U32(int at, uint v) { if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at), v); else BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), v); }
    image[0] = image[1] = (byte)(bigEndian ? 'M' : 'I');
    U16(2, 42);
    U32(4, 8);
    U16(8, (ushort)tags.Count);
    for (var i = 0; i < tags.Count; ++i) {
      var at = 10 + i * 12;
      U16(at, tags[i].Id);
      U16(at + 2, tags[i].Type);
      U32(at + 4, tags[i].Count);
      if (tags[i].Type == TypeShort && tags[i].Count == 1)
        U16(at + 8, (ushort)tags[i].ValueOrOffset); // left-justified inline SHORT
      else
        U32(at + 8, tags[i].ValueOrOffset);
    }
    data.CopyTo(image, dataStart);
    return image;
  }

  private static byte[] LongArray(bool bigEndian, params uint[] values) {
    var bytes = new byte[values.Length * 4];
    for (var i = 0; i < values.Length; ++i)
      if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
      else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
    return bytes;
  }

  // Data area layout: [0..8) offsets array, [8..16) counts array, strip bytes from 16.
  private static byte[] TwoStrips(bool bigEndian, uint firstOffset, uint firstLength, uint secondOffset, uint secondLength) {
    const int dataStart = 128;
    var payload = Enumerable.Range(0, 64).Select(static i => (byte)(0x30 + i)).ToArray();
    var data = new byte[16 + payload.Length];
    LongArray(bigEndian, firstOffset, secondOffset).CopyTo(data, 0);
    LongArray(bigEndian, firstLength, secondLength).CopyTo(data, 8);
    payload.CopyTo(data, 16);
    return Tiff(bigEndian, [
      new(DngReader.TagStripOffsets, TypeLong, 2, dataStart),
      new(DngReader.TagStripByteCounts, TypeLong, 2, dataStart + 8),
    ], dataStart, data);
  }

  private const int Payload = 128 + 16;

  [TestCase(false, TestName = "GivenLittleEndianTwoStrips_WhenRead_ThenBytesFollowStripOrder")]
  [TestCase(true, TestName = "GivenBigEndianTwoStrips_WhenRead_ThenBytesFollowStripOrder")]
  [Category("HappyPath")]
  public void GivenTwoStrips_WhenRead_ThenBytesFollowStripOrder(bool bigEndian) {
    var file = TwoStrips(bigEndian, Payload + 40, 6, Payload + 2, 5);
    var reader = new DngReader(file);

    var bytes = reader.ReadStripBytes(reader.TopLevelIfds[0]);

    Assert.That(bytes, Is.EqualTo(file[(Payload + 40)..(Payload + 46)].Concat(file[(Payload + 2)..(Payload + 7)]).ToArray()));
  }

  [Test, Category("Boundary")]
  public void GivenStripEndingExactlyAtEndOfFile_WhenRead_ThenItIsIncluded() {
    var file = TwoStrips(false, Payload, 1, Payload + 60, 4);
    var reader = new DngReader(file);

    Assert.That(reader.ReadStripBytes(reader.TopLevelIfds[0]), Is.EqualTo(file[Payload..(Payload + 1)].Concat(file[^4..]).ToArray()));
  }

  [Test, Category("Boundary")]
  public void GivenStripOneBytePastEndOfFile_WhenRead_ThenOnlyTheValidStripRemains() {
    var file = TwoStrips(false, Payload, 3, Payload + 61, 4);
    var reader = new DngReader(file);

    Assert.That(reader.ReadStripBytes(reader.TopLevelIfds[0]), Is.EqualTo(file[Payload..(Payload + 3)]));
  }

  [Test, Category("EdgeCase")]
  public void GivenZeroLengthStrip_WhenRead_ThenItContributesNothing() {
    var file = TwoStrips(false, Payload, 0, Payload + 8, 2);
    var reader = new DngReader(file);

    Assert.That(reader.ReadStripBytes(reader.TopLevelIfds[0]), Is.EqualTo(file[(Payload + 8)..(Payload + 10)]));
  }

  [Test, Category("ErrorHandling")]
  public void GivenOffsetAndCountArraysOfDifferentLength_WhenRead_ThenNoStripIsReturned() {
    var file = Tiff(false, [
      new(DngReader.TagStripOffsets, TypeLong, 2, 64),
      new(DngReader.TagStripByteCounts, TypeLong, 1, 4),
    ], 64, LongArray(false, 80, 84));
    var reader = new DngReader(file);

    Assert.That(reader.ReadStripBytes(reader.TopLevelIfds[0]), Is.Empty);
  }

  [TestCase(false, TestName = "GivenLittleEndianInlineShortStrip_WhenRead_ThenInlineValuesAreUsed")]
  [TestCase(true, TestName = "GivenBigEndianInlineShortStrip_WhenRead_ThenInlineValuesAreUsed")]
  [Category("HappyPath")]
  public void GivenInlineShortStrip_WhenRead_ThenInlineValuesAreUsed(bool bigEndian) {
    var data = Enumerable.Range(0, 16).Select(static i => (byte)(0x70 + i)).ToArray();
    var file = Tiff(bigEndian, [
      new(DngReader.TagStripOffsets, TypeShort, 1, 64 + 3),
      new(DngReader.TagStripByteCounts, TypeShort, 1, 5),
    ], 64, data);
    var reader = new DngReader(file);

    Assert.That(reader.ReadStripBytes(reader.TopLevelIfds[0]), Is.EqualTo(data[3..8]));
  }

  [Test, Category("ErrorHandling")]
  public void GivenLongArrayCountBeyondAddressableRange_WhenValuesRead_ThenEmptyWithoutThrowing() {
    var file = Tiff(false, [new(DngReader.TagStripOffsets, TypeLong, 0x4000_0000, 64)], 64, new byte[16]);
    var reader = new DngReader(file);

    IReadOnlyList<uint> values = [];
    Assert.DoesNotThrow(() => values = reader.ReadValuesAsUInt32(reader.TopLevelIfds[0].Entries[0]));
    Assert.That(values, Is.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void GivenDngVersionCountAboveIntMaxValue_WhenOpened_ThenLengthIsClampedWithoutThrowing() {
    var file = Tiff(false, [new(DngReader.TagDngVersion, TypeByte, uint.MaxValue, 0x0001_0401)], 64, new byte[4]);

    DngReader? reader = null;
    Assert.DoesNotThrow(() => reader = new DngReader(file));
    Assert.That(reader!.DngVersionLength, Is.EqualTo(int.MaxValue));
  }

  [Test, Category("HappyPath")]
  public void GivenEmbeddedJpegTags_WhenRead_ThenTheReferencedBytesAreReturned() {
    var data = Enumerable.Range(0, 16).Select(static i => (byte)(0x10 + i)).ToArray();
    var file = Tiff(false, [
      new(DngReader.TagJpegInterchangeFormat, TypeLong, 1, 64 + 2),
      new(DngReader.TagJpegInterchangeFormatLength, TypeLong, 1, 7),
    ], 64, data);
    var reader = new DngReader(file);

    Assert.That(reader.ReadEmbeddedJpeg(reader.TopLevelIfds[0]), Is.EqualTo(data[2..9]));
  }

  [Test, Category("ErrorHandling")]
  public void GivenEmbeddedJpegPastEndOfFile_WhenRead_ThenEmpty() {
    var file = Tiff(false, [
      new(DngReader.TagJpegInterchangeFormat, TypeLong, 1, 64 + 10),
      new(DngReader.TagJpegInterchangeFormatLength, TypeLong, 1, 7),
    ], 64, new byte[16]);
    var reader = new DngReader(file);

    Assert.That(reader.ReadEmbeddedJpeg(reader.TopLevelIfds[0]), Is.Empty);
  }
}
