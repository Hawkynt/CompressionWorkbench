using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Cdi;

namespace Compression.Tests.Cdi;

[TestFixture]
public sealed class CdiLegacyMultisessionMutationTests {
  private const uint V3 = 0x80000005;
  private const int Pregap = 150;
  private const int SecondSessionLba = 11700;

  private static ReadOnlySpan<byte> TrackMarker => [
    0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
  ];

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_V3SecondSessionAbsoluteIso_PreservesAbsoluteAddressing() {
    using var archive = new MemoryStream();
    archive.Write(BuildImage());
    archive.Position = 0;

    ((IArchiveModifiable)new CdiFormatDescriptor()).Add(
      archive,
      [ArchiveInputInfo.InMemory("NEW.BIN", "new-second-session-file"u8)]);

    archive.Position = 0;
    using var reader = new CdiReader(archive, leaveOpen: true);
    var active = reader.ActiveDataTrack;
    var oldFile = reader.Entries.Single(entry =>
      !entry.IsDirectory && entry.Name.Equals("README.TXT", StringComparison.OrdinalIgnoreCase));
    var newFile = reader.Entries.Single(entry =>
      !entry.IsDirectory && entry.Name.Equals("NEW.BIN", StringComparison.OrdinalIgnoreCase));

    Assert.Multiple(() => {
      Assert.That(reader.CdiVersion, Is.EqualTo(V3));
      Assert.That(reader.SessionCount, Is.EqualTo(2));
      Assert.That(reader.Tracks, Has.Count.EqualTo(2));
      Assert.That(active, Is.Not.Null);
      Assert.That(active!.SessionNumber, Is.EqualTo(2));
      Assert.That(active.StartLba, Is.EqualTo(SecondSessionLba));
      Assert.That(oldFile.StartLba, Is.GreaterThanOrEqualTo(SecondSessionLba));
      Assert.That(newFile.StartLba, Is.GreaterThanOrEqualTo(SecondSessionLba));
      Assert.That(reader.Extract(oldFile), Is.EqualTo("absolute-old"u8.ToArray()));
      Assert.That(reader.Extract(newFile), Is.EqualTo("new-second-session-file"u8.ToArray()));
    });
  }

  private static byte[] BuildImage() {
    const int audioDataSectors = 4;
    const int isoSectors = 64;

    var audioBody = new byte[(Pregap + audioDataSectors) * 2352];
    audioBody.AsSpan(Pregap * 2352).Fill(0x66);
    var iso = BuildAbsoluteIso(isoSectors, SecondSessionLba, "absolute-old"u8.ToArray());
    var dataBody = new byte[(Pregap + isoSectors) * 2048];
    iso.CopyTo(dataBody.AsSpan(Pregap * 2048));

    using var image = new MemoryStream();
    image.Write(audioBody);
    image.Write(dataBody);
    var descriptorOffset = checked((uint)image.Position);

    WriteUInt16(image, 2); // sessions

    WriteUInt16(image, 1);
    WriteLegacyTrack(image, CdiTrackMode.Audio, sectorSelector: 2,
      pregap: Pregap, dataSectors: audioDataSectors, startLba: 0);
    WriteZeros(image, 13);

    WriteUInt16(image, 1);
    WriteLegacyTrack(image, CdiTrackMode.Mode1, sectorSelector: 0,
      pregap: Pregap, dataSectors: isoSectors, startLba: SecondSessionLba);
    WriteZeros(image, 13);

    WriteUInt32(image, V3);
    WriteUInt32(image, descriptorOffset);
    return image.ToArray();
  }

  private static byte[] BuildAbsoluteIso(int sectors, int baseLba, byte[] payload) {
    const int rootRelativeLba = 18;
    const int fileRelativeLba = 19;
    var iso = new byte[sectors * 2048];

    var pvd = iso.AsSpan(16 * 2048, 2048);
    pvd[0] = 1;
    "CD001"u8.CopyTo(pvd[1..]);
    pvd[6] = 1;
    WriteBothEndianUInt32(pvd[80..88], checked((uint)(baseLba + sectors)));
    BinaryPrimitives.WriteUInt16LittleEndian(pvd[128..130], 2048);
    BinaryPrimitives.WriteUInt16BigEndian(pvd[130..132], 2048);
    WriteDirectoryRecord(pvd[156..], baseLba + rootRelativeLba, 2048, true, [0]);

    var terminator = iso.AsSpan(17 * 2048, 2048);
    terminator[0] = 0xFF;
    "CD001"u8.CopyTo(terminator[1..]);
    terminator[6] = 1;

    var directory = iso.AsSpan(rootRelativeLba * 2048, 2048);
    var position = WriteDirectoryRecord(directory, baseLba + rootRelativeLba, 2048, true, [0]);
    position += WriteDirectoryRecord(directory[position..], baseLba + rootRelativeLba, 2048, true, [1]);
    _ = WriteDirectoryRecord(
      directory[position..],
      baseLba + fileRelativeLba,
      payload.Length,
      false,
      "README.TXT;1"u8
    );
    payload.CopyTo(iso.AsSpan(fileRelativeLba * 2048));
    return iso;
  }

  private static int WriteDirectoryRecord(
    Span<byte> destination,
    int extentLba,
    int dataLength,
    bool directory,
    ReadOnlySpan<byte> identifier
  ) {
    var length = 33 + identifier.Length;
    if ((length & 1) != 0) ++length;
    var record = destination[..length];
    record.Clear();
    record[0] = checked((byte)length);
    WriteBothEndianUInt32(record[2..10], checked((uint)extentLba));
    WriteBothEndianUInt32(record[10..18], checked((uint)dataLength));
    record[25] = directory ? (byte)0x02 : (byte)0;
    BinaryPrimitives.WriteUInt16LittleEndian(record[28..30], 1);
    BinaryPrimitives.WriteUInt16BigEndian(record[30..32], 1);
    record[32] = checked((byte)identifier.Length);
    identifier.CopyTo(record[33..]);
    return length;
  }

  private static void WriteLegacyTrack(
    Stream output,
    CdiTrackMode mode,
    uint sectorSelector,
    int pregap,
    int dataSectors,
    int startLba
  ) {
    WriteUInt32(output, 0);
    output.Write(TrackMarker);
    output.Write(TrackMarker);
    WriteZeros(output, 4);
    output.WriteByte(0);
    WriteZeros(output, 11 + 4 + 4);
    WriteUInt32(output, 0);
    WriteZeros(output, 2);
    WriteUInt32(output, checked((uint)pregap));
    WriteUInt32(output, checked((uint)dataSectors));
    WriteZeros(output, 6);
    WriteUInt32(output, (uint)mode);
    WriteZeros(output, 12);
    WriteUInt32(output, checked((uint)startLba));
    WriteUInt32(output, checked((uint)(pregap + dataSectors)));
    WriteZeros(output, 16);
    WriteUInt32(output, sectorSelector);
    WriteZeros(output, 29);
    WriteZeros(output, 5);
    WriteUInt32(output, 0);
  }

  private static void WriteBothEndianUInt32(Span<byte> destination, uint value) {
    BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], value);
    BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], value);
  }

  private static void WriteUInt16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteZeros(Stream output, int count) {
    Span<byte> zeros = stackalloc byte[64];
    zeros.Clear();
    while (count > 0) {
      var chunk = Math.Min(count, zeros.Length);
      output.Write(zeros[..chunk]);
      count -= chunk;
    }
  }
}
