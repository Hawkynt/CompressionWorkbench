using Compression.Registry;
using FileFormat.Nrg;

namespace Compression.Tests.Nrg;

[TestFixture]
public class NrgReaderTopologyTests {
  private const int AudioSectorSize = 2352;
  private const int SubchannelSectorSize = 2448;

  [Test, Category("RoundTrip")]
  public void MixedMode_ExposesTopologyAndRawAudioEntry() {
    var audio = Pattern(AudioSectorSize * 3, 17);
    var cdText = Pattern(36, 31);
    var image = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Audio, audio) {
          PregapSectors = 2,
          PregapData = Pattern(AudioSectorSize * 2, 7),
          Isrc = "USABC2600001",
        },
        new NrgTrackDefinition(NrgTrackMode.Mode1, BuildIso("DATA.BIN", "payload"u8.ToArray())),
      ]) { Mcn = "1234567890123" },
    ]) { CdText = cdText });

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.Sessions, Has.Count.EqualTo(1));
      Assert.That(reader.Tracks, Has.Count.EqualTo(2));
      Assert.That(reader.Sessions[0].Mcn, Is.EqualTo("1234567890123"));
      Assert.That(reader.CdText.ToArray(), Is.EqualTo(cdText));
      Assert.That(reader.Tracks[0].IsAudio, Is.True);
      Assert.That(reader.Tracks[0].Isrc, Is.EqualTo("USABC2600001"));
      Assert.That(reader.Tracks[0].PregapLength, Is.EqualTo(2L * AudioSectorSize));
      Assert.That(reader.Tracks[1].HasIso9660, Is.True);
    });

    var audioEntry = reader.Entries.Single(static entry => entry.Kind == NrgEntryKind.RawTrack);
    Assert.Multiple(() => {
      Assert.That(audioEntry.FullPath, Is.EqualTo(".nrg/session-01/tracks/track-01-audio.bin"));
      Assert.That(audioEntry.Size, Is.EqualTo(audio.Length));
      Assert.That(reader.Extract(audioEntry), Is.EqualTo(audio));
    });

    var dataEntry = reader.Entries.Single(entry =>
      entry.Kind == NrgEntryKind.IsoFile && entry.Name.Equals("DATA.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.Multiple(() => {
      Assert.That(dataEntry.FullPath, Is.EqualTo("DATA.BIN").IgnoreCase);
      Assert.That(reader.Extract(dataEntry), Is.EqualTo("payload"u8.ToArray()));
    });
  }

  [Test, Category("RoundTrip")]
  public void MultipleSessions_ExposeEveryIsoFilesystemWithoutMovingTheFirst() {
    var image = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, BuildIso("FIRST.BIN", "first"u8.ToArray())),
      ]),
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, BuildIso("SECOND.BIN", "second"u8.ToArray())),
      ]),
    ]));

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.Sessions, Has.Count.EqualTo(2));
      Assert.That(reader.Tracks, Has.Count.EqualTo(2));
      Assert.That(reader.Tracks.All(static track => track.HasIso9660), Is.True);
      Assert.That(reader.Tracks[0].TrackNumber, Is.EqualTo(1));
      Assert.That(reader.Tracks[1].TrackNumber, Is.EqualTo(2));
      Assert.That(reader.Tracks[1].Index1Lba, Is.GreaterThan(reader.Tracks[0].Index1Lba));
    });

    var first = reader.Entries.Single(entry => entry.Name.Equals("FIRST.BIN", StringComparison.OrdinalIgnoreCase));
    var second = reader.Entries.Single(entry => entry.Name.Equals("SECOND.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.Multiple(() => {
      Assert.That(first.FullPath, Is.EqualTo("FIRST.BIN").IgnoreCase);
      Assert.That(second.FullPath, Is.EqualTo(".nrg/session-02/track-02/iso/SECOND.BIN").IgnoreCase);
      Assert.That(reader.Extract(first), Is.EqualTo("first"u8.ToArray()));
      Assert.That(reader.Extract(second), Is.EqualTo("second"u8.ToArray()));
    });
  }

  [Test, Category("RoundTrip")]
  public void AudioWithSubchannel_ExtractionPreservesAllStoredBytes() {
    var audio = Pattern(SubchannelSectorSize * 2, 41);
    var image = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.AudioWithSubchannel, audio),
      ]),
    ]));

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);
    var track = reader.Tracks.Single();
    var entry = reader.Entries.Single();

    Assert.Multiple(() => {
      Assert.That(track.IsAudio, Is.True);
      Assert.That(track.HasSubchannel, Is.True);
      Assert.That(track.SectorSize, Is.EqualTo(SubchannelSectorSize));
      Assert.That(reader.Extract(entry), Is.EqualTo(audio));
      Assert.That(reader.ExtractTrack(track), Is.EqualTo(audio));
    });
  }

  [Test, Category("RoundTrip")]
  public void CopyTrackTo_CanIncludeStoredPregap() {
    var pregap = Pattern(AudioSectorSize * 2, 5);
    var audio = Pattern(AudioSectorSize, 13);
    var image = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Audio, audio) {
          PregapData = pregap,
        },
      ]),
    ]));

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);
    using var output = new MemoryStream();
    reader.CopyTrackTo(reader.Tracks.Single(), output, includePregap: true);

    Assert.That(output.ToArray(), Is.EqualTo(pregap.Concat(audio).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void NonIsoDataTrack_IsExposedAsRawTrack() {
    var raw = Pattern(2048 * 2, 23);
    var image = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, raw),
      ]),
    ]));

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);
    var track = reader.Tracks.Single();
    var entry = reader.Entries.Single();

    Assert.Multiple(() => {
      Assert.That(track.HasIso9660, Is.False);
      Assert.That(entry.Kind, Is.EqualTo(NrgEntryKind.RawTrack));
      Assert.That(entry.FullPath, Is.EqualTo(".nrg/session-01/tracks/track-01-data.bin"));
      Assert.That(reader.Extract(entry), Is.EqualTo(raw));
    });
  }

  [Test, Category("Regression")]
  public void SingleNonIsoDataTrack_FileMutationIsRefusedWithoutChangingImage() {
    var descriptor = new NrgFormatDescriptor();
    var source = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, Pattern(2048 * 2, 29)),
      ]),
    ]));
    using var image = new MemoryStream();
    image.Write(source);
    image.Position = 0;

    Assert.Throws<NotSupportedException>(() => ((IArchiveModifiable)descriptor).Add(image, [
      ArchiveInputInfo.InMemory("NOPE.BIN", "nope"u8.ToArray()),
    ]));
    Assert.That(image.ToArray(), Is.EqualTo(source));
  }

  private static byte[] BuildIso(string name, byte[] data) {
    var writer = new FileSystem.Iso.IsoWriter();
    writer.AddFile(name, data);
    return writer.Build();
  }

  private static byte[] Write(NrgDiscDefinition disc) {
    using var output = new MemoryStream();
    NrgWriter.Write(output, disc);
    return output.ToArray();
  }

  private static byte[] Pattern(int length, int multiplier)
    => Enumerable.Range(0, length).Select(i => unchecked((byte)(i * multiplier + 3))).ToArray();
}
