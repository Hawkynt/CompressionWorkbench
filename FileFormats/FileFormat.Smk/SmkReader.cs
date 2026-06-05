#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.SmackerAudio;
using Compression.Registry;

namespace FileFormat.Smk;

/// <summary>
/// Read-only walker for the Smacker container (<c>.smk</c>), porting the header and frame
/// layout of FFmpeg's <c>libavformat/smacker.c</c> (<c>smacker_read_header</c>/
/// <c>smacker_read_packet</c>). All multi-byte integers are little-endian. Smacker carries
/// one video track plus up to 7 audio tracks; each frame's payload is a palette block
/// (optional) followed by the per-track audio chunks (each a 4-byte length prefix + chunk)
/// and then the video block. This reader surfaces only audio: the video data region is a
/// raw track blob, and each compressed Smacker-audio (SMKA) track is decoded to per-channel
/// WAVs (<see cref="SmackerAudioCodec"/>) with a graceful fallback to the raw concatenated
/// chunk blob. PCM (uncompressed) tracks are also surfaced. Parsing degrades gracefully.
/// </summary>
internal static class SmkReader {

  private const int FlagRingFrame = 0x01;
  private const int SmkAudPacked = 0x80;
  private const int SmkAud16Bits = 0x20;
  private const int SmkAudStereo = 0x10;
  private const int SmkAudBinkAud = 0x08;
  private const int SmkAudUseDct = 0x04;

  private sealed class AudioTrack {
    public int Index;
    public int SampleRate;
    public int Flags;
    public bool Present;
    public bool Stereo => (this.Flags & SmkAudStereo) != 0;
    public bool Is16Bit => (this.Flags & SmkAud16Bits) != 0;
    public bool Packed => (this.Flags & SmkAudPacked) != 0;
    public bool BinkAudio => (this.Flags & SmkAudBinkAud) != 0;
    public bool UseDct => (this.Flags & SmkAudUseDct) != 0;
    public readonly List<byte[]> Chunks = [];
  }

  public static void BuildEntries(byte[] b, List<AudioPseudoArchive.Entry> entries) {
    try {
      if (b.Length < 104)
        return;

      var magic = Encoding.ASCII.GetString(b, 0, 4);
      if (magic != "SMK2" && magic != "SMK4")
        return;

      var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
      var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
      var frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
      var ptsInc = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20));
      if ((flags & FlagRingFrame) != 0)
        ++frames;
      if (frames is < 0 or > 0xFFFFFF)
        return;

      // 28 bytes of (skipped) audio-size data, then the Huffman tree blob size.
      var treesize = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(52));

      // The 16-byte extradata (mmap/mclr/full/type tree sizes) at offset 56 is video-only.
      // Audio track descriptors: 7 × (u24 rate + u8 flags) starting at offset 72.
      var tracks = new AudioTrack[7];
      var ap = 72;
      for (var i = 0; i < 7; ++i) {
        var rate = (int)(b[ap] | (uint)b[ap + 1] << 8 | (uint)b[ap + 2] << 16);
        var aflag = b[ap + 3];
        ap += 4;
        tracks[i] = new AudioTrack {
          Index = i,
          SampleRate = rate,
          Flags = aflag,
          Present = rate != 0,
        };
      }
      ap += 4; // padding u32 → frame-size table

      // Frame sizes (u32 × frames) then frame flags (u8 × frames).
      if (ap + 4 * frames + frames > b.Length)
        return;
      var frmSize = new int[frames];
      for (var i = 0; i < frames; ++i) {
        frmSize[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(ap));
        ap += 4;
      }
      var frmFlags = new byte[frames];
      for (var i = 0; i < frames; ++i)
        frmFlags[i] = b[ap++];

      // The remaining tree blob (treesize bytes) precedes the frame data.
      var dataStart = ap + treesize;
      if (dataStart > b.Length)
        return;

      CollectAudio(b, dataStart, frmSize, frmFlags, tracks);

      // Metadata.
      var sb = new StringBuilder();
      sb.AppendLine("[Smacker]");
      sb.AppendLine($"magic = {magic}");
      sb.AppendLine($"frames = {frames}");
      sb.AppendLine($"width = {width}");
      sb.AppendLine($"height = {height}");
      sb.AppendLine($"pts_inc = {ptsInc}");
      for (var i = 0; i < 7; ++i) {
        var t = tracks[i];
        if (!t.Present)
          continue;
        sb.AppendLine($"[Track{i}]");
        sb.AppendLine($"sample_rate = {t.SampleRate}");
        sb.AppendLine($"channels = {(t.Stereo ? 2 : 1)}");
        sb.AppendLine($"bits = {(t.Is16Bit ? 16 : 8)}");
        sb.AppendLine($"codec = {DescribeCodec(t)}");
        sb.AppendLine($"chunks = {t.Chunks.Count}");
      }
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

      // VIDEO track: raw stored blob over the frame data region.
      var end = Math.Min(dataStart + frmSize.Sum(s => s & ~3), b.Length);
      if (dataStart < end)
        entries.Add(new("VIDEO.bin", "Track", b[dataStart..end], Method: "Stored"));

      for (var i = 0; i < 7; ++i)
        if (tracks[i].Present)
          AddAudioTrack(tracks[i], entries);
    } catch {
      // Graceful degradation — keep whatever parsed so far.
    }
  }

  private static string DescribeCodec(AudioTrack t) {
    if (t.BinkAudio) return "binkaudio_rdft";
    if (t.UseDct) return "binkaudio_dct";
    if (t.Packed) return "smackaud";
    return t.Is16Bit ? "pcm_s16le" : "pcm_u8";
  }

  private static void CollectAudio(byte[] b, int dataStart, int[] frmSize, byte[] frmFlags, AudioTrack[] tracks) {
    var pos = dataStart;
    for (var f = 0; f < frmSize.Length; ++f) {
      var frameSize = frmSize[f] & ~3;
      var frameEnd = Math.Min(pos + frameSize, b.Length);
      var p = pos;

      var trackFlags = frmFlags[f] >> 1; // bits 1-7 of frm_flags → audio tracks 0-6
      var paletteChange = (frmFlags[f] & FlagRingFrame) != 0;

      // Palette block: 1-byte size-in-dwords; skip size*4 - 1 trailing bytes.
      if (paletteChange && p < frameEnd) {
        var palSize = b[p] * 4;
        p += palSize;
        if (p > frameEnd)
          p = frameEnd;
      }

      // Per audio track present this frame: 4-byte total chunk size (incl. the length).
      for (var i = 0; i < 7; ++i) {
        if ((trackFlags & (1 << i)) == 0)
          continue;
        if (p + 4 > frameEnd)
          break;
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
        if (size < 4 || p + size > frameEnd)
          break;
        // The chunk payload (size - 4 bytes after the length) is the audio data, which for
        // compressed tracks itself begins with a 4-byte unpacked-length prefix. Surface the
        // payload including that inner prefix so the decoder can consume it directly.
        tracks[i].Chunks.Add(b[(p + 4)..(p + size)]);
        p += size;
      }
      // Remaining bytes are the video block — ignored for audio extraction.

      pos = frameEnd;
    }
  }

  private static void AddAudioTrack(AudioTrack track, List<AudioPseudoArchive.Entry> entries) {
    var baseName = $"TRACK{track.Index}";
    var channels = track.Stereo ? 2 : 1;
    var bits = track.Is16Bit ? 16 : 8;

    using (var ms = new MemoryStream()) {
      foreach (var c in track.Chunks)
        ms.Write(c);
      var raw = ms.ToArray();
      entries.Add(new($"{baseName}.bin", "Stream", raw, Method: DescribeCodec(track)));
    }

    if (track.Chunks.Count == 0)
      return;

    // Compressed Smacker audio (SMKA): decode to per-channel WAVs.
    if (track.Packed && !track.BinkAudio && !track.UseDct) {
      try {
        var codec = new SmackerAudioCodec(track.SampleRate, channels, bits);
        var interleaved = codec.DecodeStream(track.Chunks);
        if (interleaved.Length == 0)
          return;
        var split = SplitNative(interleaved, channels, track.SampleRate, bits);
        foreach (var (name, wav) in split)
          entries.Add(new($"{baseName}_{name}.wav", "Channel", wav, Method: "pcm"));
      } catch {
        // Undecodable SMKA track — keep the raw blob only.
      }
      return;
    }

    // Uncompressed PCM track: each chunk's payload (after its own 4-byte length prefix that
    // the demuxer keeps) is raw PCM. For PCM the inner prefix is the byte count, so strip it.
    if (!track.Packed && !track.BinkAudio && !track.UseDct) {
      try {
        using var ms = new MemoryStream();
        foreach (var c in track.Chunks)
          if (c.Length > 4)
            ms.Write(c.AsSpan(4)); // drop the 4-byte unpacked-size prefix
        var raw = ms.ToArray();
        if (raw.Length == 0)
          return;
        var split = SplitNative(raw, channels, track.SampleRate, bits);
        foreach (var (name, wav) in split)
          entries.Add(new($"{baseName}_{name}.wav", "Channel", wav, Method: "pcm"));
      } catch {
        // Keep the raw blob only.
      }
    }
    // Bink-audio-in-Smacker tracks remain blob-only here (handled by FileFormat.Bik).
  }

  private static IReadOnlyList<(string Name, byte[] Wav)> SplitNative(byte[] interleaved, int channels, int sampleRate, int bits) {
    // 8-bit Smacker PCM is unsigned; the WAV PCM format code 1 with 8-bit is unsigned, so
    // the bytes pass through unchanged. 16-bit is signed little-endian.
    var split = PcmCodec.SplitInterleavedPcm(interleaved, channels, sampleRate, bits);
    return split.Select(s => (s.Name, s.WavBlob)).ToList();
  }
}
