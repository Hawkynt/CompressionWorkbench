#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Aea;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Sony ATRAC1 / MiniDisc (<c>.aea</c>) descriptor (<see cref="AeaFormatDescriptor"/>):
/// the 2048-byte header (LE 0x800 marker, 256-byte title, channel count at offset 264) is parsed,
/// the payload is decoded to per-channel WAVs via the ATRAC1 codec, and structural detection
/// (<see cref="AeaFormatDescriptor.LooksLikeAea"/>) accepts well-formed files and rejects garbage.
/// </summary>
[TestFixture]
public class AeaTests {

  private const int HeaderSize = 2048;
  private const int SoundUnitSize = 212;

  /// <summary>Builds a valid AEA file with the given channel count and frame count (silent units).</summary>
  private static byte[] BuildAea(int channels, int frames, string title = "") {
    var blockSize = channels * SoundUnitSize;
    var buf = new byte[HeaderSize + frames * blockSize];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, 0x0000_0800); // marker
    var titleBytes = Encoding.Latin1.GetBytes(title);
    Array.Copy(titleBytes, 0, buf, 4, Math.Min(titleBytes.Length, 255));
    buf[264] = (byte)channels;
    return buf;
  }

  // ── structural detection ────────────────────────────────────────────────────────

  [Test]
  public void LooksLikeAea_AcceptsWellFormedMonoAndStereo() {
    Assert.That(AeaFormatDescriptor.LooksLikeAea(BuildAea(1, 4)), Is.True);
    Assert.That(AeaFormatDescriptor.LooksLikeAea(BuildAea(2, 4)), Is.True);
  }

  [Test]
  public void LooksLikeAea_RejectsBadMarker() {
    var b = BuildAea(1, 4);
    b[1] = 0x00; // break the marker
    Assert.That(AeaFormatDescriptor.LooksLikeAea(b), Is.False);
  }

  [Test]
  public void LooksLikeAea_RejectsBadChannelCount() {
    var b = BuildAea(1, 4);
    b[264] = 3;
    Assert.That(AeaFormatDescriptor.LooksLikeAea(b), Is.False);
  }

  [Test]
  public void LooksLikeAea_RejectsRaggedPayload() {
    var b = BuildAea(2, 4);
    Array.Resize(ref b, b.Length - 7); // payload no longer a whole number of sound units
    Assert.That(AeaFormatDescriptor.LooksLikeAea(b), Is.False);
  }

  [Test]
  public void LooksLikeAea_RejectsTooShort() {
    Assert.That(AeaFormatDescriptor.LooksLikeAea(new byte[HeaderSize]), Is.False);
  }

  // ── pseudo-archive surfacing ──────────────────────────────────────────────────────

  [Test]
  public void MonoAea_SurfacesFullContainerAndMonoWav() {
    var aea = BuildAea(1, 3, "Track One");
    var entries = new AeaFormatDescriptor().List(new MemoryStream(aea), null);

    Assert.That(entries.Any(e => e.Name == "FULL.aea" && e.Kind == "Container"), Is.True);

    using var meta = new MemoryStream();
    new AeaFormatDescriptor().ExtractEntry(new MemoryStream(aea), "metadata.ini", meta, null);
    var metaText = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(metaText, Does.Contain("title = Track One"));
    Assert.That(metaText, Does.Contain("channels = 1"));

    var wavEntry = entries.FirstOrDefault(e => e.Name == "MONO.wav" && e.Kind == "Channel");
    Assert.That(wavEntry, Is.Not.Null);

    using var wavStream = new MemoryStream();
    new AeaFormatDescriptor().ExtractEntry(new MemoryStream(aea), "MONO.wav", wavStream, null);
    var wav = wavStream.ToArray();
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    // 3 frames × 512 samples × 2 bytes + 44-byte header.
    Assert.That(wav.Length, Is.EqualTo(44 + 3 * 512 * 2));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
  }

  [Test]
  public void StereoAea_SurfacesTwoChannelWavs() {
    var aea = BuildAea(2, 2);
    var entries = new AeaFormatDescriptor().List(new MemoryStream(aea), null);
    Assert.That(entries.Count(e => e.Kind == "Channel"), Is.EqualTo(2), "stereo AEA surfaces two channel WAVs");
  }

  [Test]
  public void SilentAea_DecodesToSilence() {
    var aea = BuildAea(1, 2);
    using var wavStream = new MemoryStream();
    new AeaFormatDescriptor().ExtractEntry(new MemoryStream(aea), "MONO.wav", wavStream, null);
    var wav = wavStream.ToArray();
    // Skip the 44-byte header; all PCM samples must be zero.
    var allZero = true;
    for (var i = 44; i < wav.Length; ++i)
      if (wav[i] != 0) { allZero = false; break; }
    Assert.That(allZero, Is.True);
  }

  [Test]
  public void HeaderOnly_DegradesGracefully() {
    // A 2048-byte header with no payload must still surface FULL.aea without throwing.
    var aea = new byte[HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(aea, 0x0000_0800);
    aea[264] = 1;
    System.Collections.Generic.List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new AeaFormatDescriptor().List(new MemoryStream(aea), null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.aea"), Is.True);
  }
}
