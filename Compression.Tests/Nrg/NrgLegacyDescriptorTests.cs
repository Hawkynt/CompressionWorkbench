using System.Buffers.Binary;
using FileFormat.Nrg;

namespace Compression.Tests.Nrg;

[TestFixture]
public class NrgLegacyDescriptorTests {
  private const int SectorSize = 2048;

  [Test, Category("Regression")]
  public void Version1_CuesAndDaoi_DecodeMsfAddresses() {
    var payload = "legacy-v1"u8.ToArray();
    var iso = BuildIso("LEGACY.BIN", payload);
    var sectors = iso.Length / SectorSize;
    var image = BuildLegacyDaoImage(
      [iso],
      [new SessionVector(1, -150, 0, sectors)],
      reorderDescriptorChunks: false);

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);
    var session = reader.Sessions.Single();
    var track = reader.Tracks.Single();
    var entry = reader.Entries.Single(entry =>
      entry.Kind == NrgEntryKind.IsoFile && entry.Name.Equals("LEGACY.BIN", StringComparison.OrdinalIgnoreCase));

    Assert.Multiple(() => {
      Assert.That(reader.Version, Is.EqualTo(1));
      Assert.That(session.LeadInLba, Is.EqualTo(-150));
      Assert.That(session.LeadOutLba, Is.EqualTo(sectors));
      Assert.That(track.TrackNumber, Is.EqualTo(1));
      Assert.That(track.Index0Lba, Is.Zero);
      Assert.That(track.Index1Lba, Is.Zero);
      Assert.That(track.HasIso9660, Is.True);
      Assert.That(reader.Extract(entry), Is.EqualTo(payload));
    });
  }

  [Test, Category("Regression")]
  public void Version1_ReorderedCueAndDaoChunks_AreCorrelatedByOrdinal() {
    var firstPayload = "first-v1-session"u8.ToArray();
    var secondPayload = "second-v1-session"u8.ToArray();
    var firstIso = BuildIso("FIRST.BIN", firstPayload);
    var secondIso = BuildIso("SECOND.BIN", secondPayload);
    var firstSectors = firstIso.Length / SectorSize;
    var secondStartLba = 12_000;
    var secondSectors = secondIso.Length / SectorSize;

    var image = BuildLegacyDaoImage(
      [firstIso, secondIso],
      [
        new SessionVector(1, -150, 0, firstSectors),
        new SessionVector(2, 7_500, secondStartLba, secondStartLba + secondSectors),
      ],
      reorderDescriptorChunks: true);

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.Version, Is.EqualTo(1));
      Assert.That(reader.Sessions, Has.Count.EqualTo(2));
      Assert.That(reader.Tracks, Has.Count.EqualTo(2));
      Assert.That(reader.Sessions[0].LeadInLba, Is.EqualTo(-150));
      Assert.That(reader.Sessions[1].LeadInLba, Is.EqualTo(7_500));
      Assert.That(reader.Tracks[0].TrackNumber, Is.EqualTo(1));
      Assert.That(reader.Tracks[0].Index1Lba, Is.Zero);
      Assert.That(reader.Tracks[1].TrackNumber, Is.EqualTo(2));
      Assert.That(reader.Tracks[1].Index1Lba, Is.EqualTo(secondStartLba));
      Assert.That(reader.Tracks.All(static track => track.HasIso9660), Is.True);
    });

    var first = reader.Entries.Single(entry => entry.Name.Equals("FIRST.BIN", StringComparison.OrdinalIgnoreCase));
    var second = reader.Entries.Single(entry => entry.Name.Equals("SECOND.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.Multiple(() => {
      Assert.That(first.FullPath, Is.EqualTo("FIRST.BIN").IgnoreCase);
      Assert.That(second.FullPath, Is.EqualTo(".nrg/session-02/track-02/iso/SECOND.BIN").IgnoreCase);
      Assert.That(reader.Extract(first), Is.EqualTo(firstPayload));
      Assert.That(reader.Extract(second), Is.EqualTo(secondPayload));
    });
  }

  [Test, Category("Regression")]
  public void Version2_Cuex_PreservesSignedLbas() {
    var pregap = new byte[2 * 2352];
    var audio = new byte[2352];
    using var output = new MemoryStream();
    NrgWriter.Write(output, new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Audio, audio) {
          PregapData = pregap,
        },
      ]),
    ]));

    using var stream = new MemoryStream(output.ToArray(), writable: false);
    using var reader = new NrgReader(stream);
    var track = reader.Tracks.Single();

    Assert.Multiple(() => {
      Assert.That(reader.Version, Is.EqualTo(2));
      Assert.That(reader.Sessions.Single().LeadInLba, Is.EqualTo(-150));
      Assert.That(track.Index0Lba, Is.EqualTo(-2));
      Assert.That(track.Index1Lba, Is.Zero);
    });
  }

  private static byte[] BuildLegacyDaoImage(
      IReadOnlyList<byte[]> trackData,
      IReadOnlyList<SessionVector> sessions,
      bool reorderDescriptorChunks) {
    if (trackData.Count != sessions.Count)
      throw new ArgumentException("Each session vector must have one matching track payload.");

    using var output = new MemoryStream();
    var offsets = new (int Start, int End)[trackData.Count];
    for (var i = 0; i < trackData.Count; ++i) {
      var start = checked((int)output.Position);
      output.Write(trackData[i]);
      offsets[i] = (start, checked((int)output.Position));
    }

    var trailerOffset = checked((uint)output.Position);
    var cuePayloads = sessions.Select(BuildCues).ToArray();
    var daoPayloads = sessions.Select((session, index) => BuildDaoi(session.TrackNumber, offsets[index].Start, offsets[index].End)).ToArray();

    if (reorderDescriptorChunks) {
      foreach (var cue in cuePayloads)
        WriteChunk(output, "CUES"u8, cue);
      foreach (var dao in daoPayloads)
        WriteChunk(output, "DAOI"u8, dao);
    } else {
      for (var i = 0; i < sessions.Count; ++i) {
        WriteChunk(output, "CUES"u8, cuePayloads[i]);
        WriteChunk(output, "DAOI"u8, daoPayloads[i]);
      }
    }

    WriteChunk(output, "END!"u8, ReadOnlySpan<byte>.Empty);

    Span<byte> footer = stackalloc byte[8];
    "NERO"u8.CopyTo(footer);
    BinaryPrimitives.WriteUInt32BigEndian(footer[4..], trailerOffset);
    output.Write(footer);
    return output.ToArray();
  }

  private static byte[] BuildCues(SessionVector session) {
    var result = new byte[32];
    WriteOldCue(result.AsSpan(0, 8), 0x41, 0, 0, session.LeadInLba);
    WriteOldCue(result.AsSpan(8, 8), 0x41, session.TrackNumber, 0, session.Index1Lba);
    WriteOldCue(result.AsSpan(16, 8), 0x41, session.TrackNumber, 1, session.Index1Lba);
    WriteOldCue(result.AsSpan(24, 8), 0x41, -1, 1, session.LeadOutLba);
    return result;
  }

  private static byte[] BuildDaoi(int trackNumber, int startOffset, int endOffset) {
    var result = new byte[52];
    BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)result.Length));
    result[20] = checked((byte)trackNumber);
    result[21] = checked((byte)trackNumber);

    var track = result.AsSpan(22, 30);
    BinaryPrimitives.WriteUInt16BigEndian(track[12..], SectorSize);
    track[14] = (byte)NrgTrackMode.Mode1;
    BinaryPrimitives.WriteUInt16BigEndian(track[16..], 1);
    BinaryPrimitives.WriteInt32BigEndian(track[18..], startOffset);
    BinaryPrimitives.WriteInt32BigEndian(track[22..], startOffset);
    BinaryPrimitives.WriteInt32BigEndian(track[26..], endOffset);
    return result;
  }

  private static void WriteOldCue(Span<byte> destination, byte adrCtl, int trackNumber, int index, int lba) {
    destination.Clear();
    destination[0] = adrCtl;
    destination[1] = trackNumber < 0 ? (byte)0xAA : ToBcd(trackNumber);
    destination[2] = ToBcd(index);

    var absoluteFrame = checked(lba + 150);
    if (absoluteFrame < 0)
      throw new ArgumentOutOfRangeException(nameof(lba));
    var minute = absoluteFrame / (60 * 75);
    var remainder = absoluteFrame % (60 * 75);
    var second = remainder / 75;
    var frame = remainder % 75;
    if (minute > byte.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(lba));

    destination[4] = 0;
    destination[5] = checked((byte)minute);
    destination[6] = checked((byte)second);
    destination[7] = checked((byte)frame);
  }

  private static byte ToBcd(int value) {
    if (value is < 0 or > 99)
      throw new ArgumentOutOfRangeException(nameof(value));
    return checked((byte)(((value / 10) << 4) | value % 10));
  }

  private static void WriteChunk(Stream output, ReadOnlySpan<byte> id, ReadOnlySpan<byte> payload) {
    Span<byte> header = stackalloc byte[8];
    id.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)payload.Length));
    output.Write(header);
    output.Write(payload);
  }

  private static byte[] BuildIso(string name, byte[] data) {
    var writer = new FileSystem.Iso.IsoWriter();
    writer.AddFile(name, data);
    return writer.Build();
  }

  private sealed record SessionVector(int TrackNumber, int LeadInLba, int Index1Lba, int LeadOutLba);
}
