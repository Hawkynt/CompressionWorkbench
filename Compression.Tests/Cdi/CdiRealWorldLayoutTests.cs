using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Cdi;

namespace Compression.Tests.Cdi;

[TestFixture]
public sealed class CdiRealWorldLayoutTests {
  private const uint CdiV35 = 0x80000006;
  private const int Pregap = 150;
  private static ReadOnlySpan<byte> TrackMarker => [
    0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
  ];

  private sealed record FixtureTrack(
    CdiTrackMode Mode,
    CdiReadMode ReadMode,
    int PregapSectors,
    int DataSectors,
    int StartLba,
    byte[] Body,
    int Control = 4
  );

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Read_DreamcastStyleTwoSessionMixedDisc_UsesLatestMode2Iso() {
    var payload = "dreamcast-second-session"u8.ToArray();
    var session1 = new[] {
      AudioTrack(startLba: 0, dataSectors: 4, fill: 0x41),
      AudioTrack(startLba: 454, dataSectors: 5, fill: 0x42),
    };
    var session2 = new[] {
      Mode2IsoTrack(startLba: 11700, payload, CdiReadMode.Mode2_2336),
    };
    var image = BuildCdi([session1, session2]);

    using var stream = new MemoryStream(image);
    using var reader = new CdiReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.CdiVersion, Is.EqualTo(CdiV35));
      Assert.That(reader.SessionCount, Is.EqualTo(2));
      Assert.That(reader.Tracks, Has.Count.EqualTo(3));
    });

    var firstAudio = reader.Tracks[0];
    var secondAudio = reader.Tracks[1];
    var data = reader.Tracks[2];

    Assert.Multiple(() => {
      Assert.That(firstAudio.Mode, Is.EqualTo(CdiTrackMode.Audio));
      Assert.That(firstAudio.ReadMode, Is.EqualTo(CdiReadMode.Raw2352));
      Assert.That(firstAudio.PregapSectors, Is.EqualTo(Pregap));
      Assert.That(reader.ReadTrackSector(firstAudio, -1), Is.EqualTo(new byte[2352]));
      Assert.That(reader.ReadTrackSector(firstAudio, 0), Is.All.EqualTo(0x41));

      Assert.That(secondAudio.SessionNumber, Is.EqualTo(1));
      Assert.That(secondAudio.TrackNumber, Is.EqualTo(2));
      Assert.That(reader.ReadTrackSector(secondAudio, 0), Is.All.EqualTo(0x42));

      Assert.That(data.SessionNumber, Is.EqualTo(2));
      Assert.That(data.TrackNumber, Is.EqualTo(3));
      Assert.That(data.Mode, Is.EqualTo(CdiTrackMode.Mode2));
      Assert.That(data.ReadMode, Is.EqualTo(CdiReadMode.Mode2_2336));
      Assert.That(data.StoredSectorSize, Is.EqualTo(2336));
      Assert.That(data.PregapSectors, Is.EqualTo(Pregap));
      Assert.That(data.StartLba, Is.EqualTo(11700));
      Assert.That(data.DataOffset, Is.EqualTo(data.FileOffset + Pregap * 2336L));
      Assert.That(reader.ActiveDataTrack, Is.EqualTo(data));
    });

    // The ISO was authored the Dreamcast way: its directory extents are disc-
    // absolute LBAs (11718/11719), not track-relative 18/19.
    var file = reader.Entries.Single(entry =>
      !entry.IsDirectory && entry.Name.Equals("README.TXT", StringComparison.OrdinalIgnoreCase));
    Assert.Multiple(() => {
      Assert.That(file.StartLba, Is.EqualTo(11719));
      Assert.That(reader.Extract(file), Is.EqualTo(payload));
    });
  }

  [TestCase(CdiReadMode.Raw2352, 2352)]
  [TestCase(CdiReadMode.Raw2352_Q16, 2368)]
  [TestCase(CdiReadMode.Raw2352_Pw96, 2448)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void Read_RawMode2Form1_ExtractsUserDataAndHonorsStoredStride(CdiReadMode readMode, int stride) {
    var payload = "mode2-form1"u8.ToArray();
    var image = BuildCdi([[Mode2IsoTrack(startLba: 0, payload, readMode)]]);

    using var stream = new MemoryStream(image);
    using var reader = new CdiReader(stream);
    var track = reader.Tracks.Single();
    var pvdSector = reader.ReadTrackSector(track, 16);
    var file = reader.Entries.Single(entry => !entry.IsDirectory);

    Assert.Multiple(() => {
      Assert.That(track.Mode, Is.EqualTo(CdiTrackMode.Mode2));
      Assert.That(track.ReadMode, Is.EqualTo(readMode));
      Assert.That(track.StoredSectorSize, Is.EqualTo(stride));
      Assert.That(pvdSector.Length, Is.EqualTo(stride));
      Assert.That(pvdSector[24], Is.EqualTo(1));
      Assert.That(Encoding.ASCII.GetString(pvdSector, 25, 5), Is.EqualTo("CD001"));
      Assert.That(reader.Extract(file), Is.EqualTo(payload));
    });
  }

  [Test, Category("ErrorPath")]
  public void MixedLayout_MutationAndMaintenanceFailClosed_WithoutChangingImage() {
    var image = BuildCdi([
      [AudioTrack(startLba: 0, dataSectors: 3, fill: 0x5A)],
      [Mode2IsoTrack(startLba: 11700, "keep-me"u8.ToArray(), CdiReadMode.Mode2_2336)],
    ]);
    var descriptor = new CdiFormatDescriptor();

    using var archive = new MemoryStream();
    archive.Write(image);
    archive.Position = 0;
    var original = archive.ToArray();

    Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)descriptor).Add(archive, [ArchiveInputInfo.InMemory("NEW.BIN", "new"u8)]));
    Assert.That(archive.ToArray(), Is.EqualTo(original), "Add changed the refused mixed-layout image");

    archive.Position = 0;
    Assert.Throws<NotSupportedException>(() => ((IArchivePurgeable)descriptor).Purge(archive));
    Assert.That(archive.ToArray(), Is.EqualTo(original), "Purge changed the refused mixed-layout image");

    archive.Position = 0;
    Assert.Throws<NotSupportedException>(() => ((IArchiveDefragmentable)descriptor).Defragment(archive));
    Assert.That(archive.ToArray(), Is.EqualTo(original), "Defrag changed the refused mixed-layout image");

    archive.Position = 0;
    using var shrunk = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => ((IArchiveShrinkable)descriptor).Shrink(archive, shrunk));
    Assert.That(archive.ToArray(), Is.EqualTo(original), "Shrink changed the refused mixed-layout image");
    Assert.That(shrunk.Length, Is.Zero, "Shrink wrote output before refusing the mixed layout");
  }

  private static FixtureTrack AudioTrack(int startLba, int dataSectors, byte fill) {
    const int stride = 2352;
    var body = new byte[(Pregap + dataSectors) * stride];
    body.AsSpan(Pregap * stride).Fill(fill);
    return new(CdiTrackMode.Audio, CdiReadMode.Raw2352, Pregap, dataSectors, startLba, body, Control: 0);
  }

  private static FixtureTrack Mode2IsoTrack(int startLba, byte[] payload, CdiReadMode readMode) {
    const int dataSectors = 24;
    var iso = BuildMinimalIso(payload, startLba);
    var stride = readMode switch {
      CdiReadMode.Mode2_2336 => 2336,
      CdiReadMode.Raw2352 => 2352,
      CdiReadMode.Raw2352_Q16 => 2368,
      CdiReadMode.Raw2352_Pw96 => 2448,
      _ => throw new ArgumentOutOfRangeException(nameof(readMode)),
    };
    var userOffset = readMode == CdiReadMode.Mode2_2336 ? 8 : 24;
    var body = new byte[(Pregap + dataSectors) * stride];

    for (var sector = 0; sector < dataSectors; ++sector) {
      var at = (Pregap + sector) * stride;
      if (readMode != CdiReadMode.Mode2_2336) {
        body[at] = 0x00;
        body.AsSpan(at + 1, 10).Fill(0xFF);
        body[at + 11] = 0x00;
        body[at + 15] = 0x02;
      }
      iso.AsSpan(sector * 2048, 2048).CopyTo(body.AsSpan(at + userOffset, 2048));
      if (stride > (readMode == CdiReadMode.Mode2_2336 ? 2336 : 2352))
        body.AsSpan(at + (readMode == CdiReadMode.Mode2_2336 ? 2336 : 2352), stride - 2352).Fill(0xCC);
    }

    return new(CdiTrackMode.Mode2, readMode, Pregap, dataSectors, startLba, body);
  }

  private static byte[] BuildMinimalIso(byte[] payload, int extentBaseLba) {
    const int sectors = 24;
    const int rootRelativeLba = 18;
    const int fileRelativeLba = 19;
    var rootLba = checked(extentBaseLba + rootRelativeLba);
    var fileLba = checked(extentBaseLba + fileRelativeLba);
    var iso = new byte[sectors * 2048];

    var pvd = iso.AsSpan(16 * 2048, 2048);
    pvd[0] = 1;
    Encoding.ASCII.GetBytes("CD001").CopyTo(pvd[1..]);
    pvd[6] = 1;
    WriteDirectoryRecord(pvd[156..], rootLba, 2048, isDirectory: true, [0]);

    var terminator = iso.AsSpan(17 * 2048, 2048);
    terminator[0] = 255;
    Encoding.ASCII.GetBytes("CD001").CopyTo(terminator[1..]);
    terminator[6] = 1;

    var directory = iso.AsSpan(rootRelativeLba * 2048, 2048);
    var offset = WriteDirectoryRecord(directory, rootLba, 2048, isDirectory: true, [0]);
    offset += WriteDirectoryRecord(directory[offset..], rootLba, 2048, isDirectory: true, [1]);
    _ = WriteDirectoryRecord(
      directory[offset..],
      fileLba,
      payload.Length,
      isDirectory: false,
      Encoding.ASCII.GetBytes("README.TXT;1")
    );

    payload.CopyTo(iso.AsSpan(fileRelativeLba * 2048));
    return iso;
  }

  private static int WriteDirectoryRecord(
    Span<byte> destination,
    int extentLba,
    int dataLength,
    bool isDirectory,
    ReadOnlySpan<byte> identifier
  ) {
    var length = 33 + identifier.Length;
    if ((length & 1) != 0)
      ++length;
    var record = destination[..length];
    record.Clear();
    record[0] = checked((byte)length);
    WriteBothEndianUInt32(record[2..10], checked((uint)extentLba));
    WriteBothEndianUInt32(record[10..18], checked((uint)dataLength));
    record[25] = isDirectory ? (byte)0x02 : (byte)0;
    BinaryPrimitives.WriteUInt16LittleEndian(record[28..30], 1);
    BinaryPrimitives.WriteUInt16BigEndian(record[30..32], 1);
    record[32] = checked((byte)identifier.Length);
    identifier.CopyTo(record[33..]);
    return length;
  }

  private static void WriteBothEndianUInt32(Span<byte> destination, uint value) {
    BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], value);
    BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], value);
  }

  private static byte[] BuildCdi(IReadOnlyList<IReadOnlyList<FixtureTrack>> sessions) {
    using var image = new MemoryStream();
    foreach (var session in sessions)
      foreach (var track in session)
        image.Write(track.Body);

    var descriptorStart = image.Position;
    image.WriteByte(checked((byte)sessions.Count));
    var totalTracks = checked((byte)sessions.Sum(static session => session.Count));
    var globalTrack = 0;

    for (var sessionIndex = 0; sessionIndex < sessions.Count; ++sessionIndex) {
      var session = sessions[sessionIndex];
      WriteSessionPreamble(image, checked((ushort)session.Count));
      for (var trackIndex = 0; trackIndex < session.Count; ++trackIndex) {
        var track = session[trackIndex];
        var hasNext = trackIndex + 1 < session.Count;
        WritePhysicalTrackHeader(image, totalTracks);
        WriteTrackData(image, track, sessionIndex, trackIndex, hasNext);
        ++globalTrack;
      }
    }

    WriteSessionPreamble(image, 0);
    WritePhysicalTrackHeader(image, totalTracks);
    WriteDiscInfo(image, sessions.SelectMany(static session => session).Max(static track => track.StartLba + track.DataSectors));

    var descriptorLength = checked((uint)(image.Position - descriptorStart + 4));
    WriteUInt32(image, descriptorLength);
    return image.ToArray();
  }

  private static void WriteSessionPreamble(Stream output, ushort trackCount) {
    output.WriteByte(0);
    WriteUInt16(output, trackCount);
    WriteUInt32(output, 0);
  }

  private static void WritePhysicalTrackHeader(Stream output, byte totalTracks) {
    output.Write(TrackMarker);
    output.Write(TrackMarker);
    output.Write([0xAB, 0x00, 0x10]);
    output.WriteByte(totalTracks);
    output.WriteByte(0); // filename length
    WriteZeros(output, 11);
    WriteUInt32(output, 2);
    WriteUInt32(output, 0);
    WriteUInt32(output, 0x80000000);
    WriteUInt32(output, 360000);
    WriteUInt32(output, 0x00980000);
  }

  private static void WriteTrackData(
    Stream output,
    FixtureTrack track,
    int sessionIndex,
    int trackIndex,
    bool hasNext
  ) {
    using var data = new MemoryStream();
    WriteUInt16(data, 2);
    WriteUInt32(data, checked((uint)track.PregapSectors));
    WriteUInt32(data, checked((uint)track.DataSectors));
    WriteZeros(data, 6); // zero CD-Text count + two unknown bytes
    WriteUInt32(data, (uint)track.Mode);
    WriteUInt32(data, 0);
    WriteUInt32(data, checked((uint)sessionIndex));
    WriteUInt32(data, checked((uint)trackIndex));
    WriteUInt32(data, checked((uint)track.StartLba));
    var totalLength = checked((uint)(track.PregapSectors + track.DataSectors));
    WriteUInt32(data, totalLength);
    WriteZeros(data, 16);
    WriteUInt32(data, (uint)track.ReadMode);
    WriteUInt32(data, checked((uint)track.Control));
    data.WriteByte(0);
    WriteUInt32(data, totalLength);
    WriteUInt32(data, 0);
    WriteZeros(data, 12);
    WriteUInt32(data, 0);
    data.WriteByte(0);
    WriteFill(data, 8, 0xFF);
    WriteUInt32(data, 1);
    WriteUInt32(data, 0x80);
    WriteUInt32(data, 2);
    WriteUInt32(data, 0x10);
    WriteUInt32(data, 44100);
    WriteZeros(data, 42);
    WriteUInt32(data, uint.MaxValue);
    WriteZeros(data, 12);
    data.WriteByte(hasNext ? (byte)0 : (byte)track.Mode);
    WriteZeros(data, 5);
    data.WriteByte(hasNext ? (byte)1 : (byte)0);
    data.WriteByte(0);
    WriteUInt32(data, checked((uint)track.StartLba));

    var bytes = data.ToArray();
    output.Write(hasNext ? bytes.AsSpan(0, bytes.Length - 8) : bytes);
  }

  private static void WriteDiscInfo(Stream output, int endLba) {
    WriteUInt32(output, checked((uint)endLba));
    output.WriteByte(0);
    output.WriteByte(0);
    WriteUInt32(output, 1);
    WriteUInt32(output, 1);
    WriteZeros(output, 13);
    WriteUInt32(output, 0);
    WriteUInt32(output, 0);
    WriteZeros(output, 8);
    WriteUInt32(output, CdiV35);
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

  private static void WriteZeros(Stream output, int count) => WriteFill(output, count, 0);

  private static void WriteFill(Stream output, int count, byte value) {
    Span<byte> buffer = stackalloc byte[64];
    buffer.Fill(value);
    while (count > 0) {
      var chunk = Math.Min(count, buffer.Length);
      output.Write(buffer[..chunk]);
      count -= chunk;
    }
  }
}
