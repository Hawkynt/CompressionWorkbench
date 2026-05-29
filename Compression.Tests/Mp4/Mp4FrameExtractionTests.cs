#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mp4;

namespace Compression.Tests.Mp4;

[TestFixture]
public class Mp4FrameExtractionTests {

  /// <summary>
  /// Builds a minimal MP4 with a video track containing N samples in mdat.
  /// The codec is configurable (defaults to avc1).
  /// </summary>
  private static byte[] MakeMp4WithFrames(int frameCount, string codec = "avc1", int sampleSize = 10) {
    var ftyp = BuildAtom("ftyp", [
      .."isom"u8,
      ..new byte[4],
      .."isom"u8,
    ]);

    // Build mdat: N samples each of sampleSize bytes
    var mdatPayload = new byte[frameCount * sampleSize];
    for (var i = 0; i < frameCount; ++i) {
      var offset = i * sampleSize;
      Array.Fill(mdatPayload, (byte)(0x10 + i), offset, sampleSize);
    }
    var mdat = BuildAtom("mdat", mdatPayload);

    // stsd: version(1)+flags(3)+entry_count(4)+one entry (size+type only)
    var entryBody = new byte[78]; // minimal visual sample entry (78 bytes for base)
    var stsdEntry = BuildAtom(codec, entryBody);
    var stsdBody = new byte[8 + stsdEntry.Length];
    BinaryPrimitives.WriteUInt32BigEndian(stsdBody.AsSpan(4), 1); // entry count
    stsdEntry.CopyTo(stsdBody, 8);
    var stsd = BuildAtom("stsd", stsdBody);

    // stsz: version(1)+flags(3)+sample_size(4)+sample_count(4)+per-sample sizes
    var stszBody = new byte[12 + frameCount * 4];
    // sample_size = 0 (variable) → table follows
    BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(8), (uint)frameCount);
    for (var i = 0; i < frameCount; ++i)
      BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(12 + i * 4), (uint)sampleSize);
    var stsz = BuildAtom("stsz", stszBody);

    // stsc: one record — all samples in one chunk
    var stscBody = new byte[4 + 4 + 12];
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(8), 1);               // first_chunk (1-based)
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(12), (uint)frameCount); // samples_per_chunk
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(16), 1);               // sample_description_index
    var stsc = BuildAtom("stsc", stscBody);

    // stco: one chunk offset pointing to mdat body
    var mdatBodyOffset = (uint)(ftyp.Length + 8); // ftyp + mdat header
    var stcoBody = new byte[4 + 4 + 4];
    BinaryPrimitives.WriteUInt32BigEndian(stcoBody.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stcoBody.AsSpan(8), mdatBodyOffset);
    var stco = BuildAtom("stco", stcoBody);

    var stbl = BuildContainerAtom("stbl", [stsd, stsc, stsz, stco]);
    var dinf = BuildContainerAtom("dinf", []);
    var minf = BuildContainerAtom("minf", [dinf, stbl]);

    var hdlrBody = new byte[4 + 4 + 4 + 12 + 5];
    "vide"u8.CopyTo(hdlrBody.AsSpan(8));
    "vide\0"u8.CopyTo(hdlrBody.AsSpan(24));
    var hdlr = BuildAtom("hdlr", hdlrBody);

    var mdhdBody = new byte[24];
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(12), 1000);
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(16), (uint)(frameCount * 33));
    var mdhd = BuildAtom("mdhd", mdhdBody);
    var mdia = BuildContainerAtom("mdia", [mdhd, hdlr, minf]);

    var tkhdBody = new byte[84];
    tkhdBody[3] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(20), (uint)(frameCount * 33));
    var tkhd = BuildAtom("tkhd", tkhdBody);
    var trak = BuildContainerAtom("trak", [tkhd, mdia]);

    var mvhdBody = new byte[108];
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(12), 1000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(16), (uint)(frameCount * 33));
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(20), 0x00010000);
    BinaryPrimitives.WriteUInt16BigEndian(mvhdBody.AsSpan(24), 0x0100);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(36), 0x00010000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(52), 0x00010000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(68), 0x40000000);
    BinaryPrimitives.WriteUInt32BigEndian(mvhdBody.AsSpan(104), 2);
    var mvhd = BuildAtom("mvhd", mvhdBody);
    var moov = BuildContainerAtom("moov", [mvhd, trak]);

    // Assemble: ftyp + mdat + moov
    var file = new byte[ftyp.Length + mdat.Length + moov.Length];
    ftyp.CopyTo(file, 0);
    mdat.CopyTo(file, ftyp.Length);
    moov.CopyTo(file, ftyp.Length + mdat.Length);
    return file;
  }

  private static byte[] BuildAtom(string type, byte[] body) {
    var size = 8 + body.Length;
    var atom = new byte[size];
    BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)size);
    Encoding.ASCII.GetBytes(type, 0, 4, atom, 4);
    body.CopyTo(atom, 8);
    return atom;
  }

  private static byte[] BuildContainerAtom(string type, byte[][] children) {
    var totalChildSize = children.Sum(c => c.Length);
    var size = 8 + totalChildSize;
    var atom = new byte[size];
    BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)size);
    Encoding.ASCII.GetBytes(type, 0, 4, atom, 4);
    var offset = 8;
    foreach (var child in children) {
      child.CopyTo(atom, offset);
      offset += child.Length;
    }
    return atom;
  }

  // ── Mp4Demuxer per-sample tests ────────────────────────────

  [Test]
  public void Mp4Demuxer_PreservesPerSampleData() {
    var file = MakeMp4WithFrames(4, "avc1", 10);
    var tracks = new Mp4Demuxer().Demux(file);
    Assert.That(tracks.Count, Is.EqualTo(1));
    var track = tracks[0];
    Assert.That(track.Samples.Count, Is.EqualTo(4));
    for (var i = 0; i < 4; ++i) {
      Assert.That(track.Samples[i].Data.Length, Is.EqualTo(10));
      Assert.That(track.Samples[i].Data[0], Is.EqualTo(0x10 + i));
    }
  }

  [Test]
  public void Mp4Demuxer_SingleSample_Works() {
    var file = MakeMp4WithFrames(1, "avc1", 20);
    var tracks = new Mp4Demuxer().Demux(file);
    Assert.That(tracks[0].Samples.Count, Is.EqualTo(1));
    Assert.That(tracks[0].Samples[0].Data.Length, Is.EqualTo(20));
  }

  // ── Descriptor frame entry tests ───────────────────────────

  [Test]
  public void Descriptor_ListsVideoFrameEntries() {
    var file = MakeMp4WithFrames(3, "avc1", 8);
    using var ms = new MemoryStream(file);
    var entries = new Mp4FormatDescriptor().List(ms, null);
    var frameEntries = entries.Where(e => e.Kind == "Frame").ToList();
    Assert.That(frameEntries.Count, Is.EqualTo(3));
    Assert.That(frameEntries[0].Name, Does.StartWith("frames/track_01/frame_000001"));
    Assert.That(frameEntries[0].Name, Does.EndWith(".h264"));
  }

  [Test]
  public void Descriptor_AvcFrames_HaveH264Extension() {
    var file = MakeMp4WithFrames(1, "avc1", 8);
    using var ms = new MemoryStream(file);
    var entries = new Mp4FormatDescriptor().List(ms, null);
    var frame = entries.First(e => e.Kind == "Frame");
    Assert.That(frame.Name, Does.EndWith(".h264"));
  }

  [Test]
  public void Descriptor_HevcFrames_HaveHevcExtension() {
    var file = MakeMp4WithFrames(1, "hvc1", 8);
    using var ms = new MemoryStream(file);
    var entries = new Mp4FormatDescriptor().List(ms, null);
    var frame = entries.First(e => e.Kind == "Frame");
    Assert.That(frame.Name, Does.EndWith(".hevc"));
  }

  [Test]
  public void Descriptor_ExtractFrame_ReturnsCorrectPayload() {
    var file = MakeMp4WithFrames(3, "avc1", 10);
    using var ms = new MemoryStream(file);
    using var output = new MemoryStream();
    var desc = new Mp4FormatDescriptor();
    var entries = desc.List(ms, null);
    var secondFrame = entries.Where(e => e.Kind == "Frame").Skip(1).First();

    ms.Position = 0;
    desc.ExtractEntry(ms, secondFrame.Name, output, null);
    var data = output.ToArray();
    Assert.That(data.Length, Is.EqualTo(10));
    Assert.That(data[0], Is.EqualTo(0x11)); // second frame fill byte
  }

  [Test]
  public void Descriptor_TrackEntryStillPresent() {
    var file = MakeMp4WithFrames(2, "avc1", 8);
    using var ms = new MemoryStream(file);
    var entries = new Mp4FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Kind == "Track"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Frame"), Is.True);
  }

  [Test]
  public void Descriptor_FrameSizeMatchesSampleSize() {
    var file = MakeMp4WithFrames(5, "avc1", 16);
    using var ms = new MemoryStream(file);
    var entries = new Mp4FormatDescriptor().List(ms, null);
    var frames = entries.Where(e => e.Kind == "Frame").ToList();
    Assert.That(frames.Count, Is.EqualTo(5));
    foreach (var f in frames)
      Assert.That(f.OriginalSize, Is.EqualTo(16));
  }
}
