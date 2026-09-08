#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.SmackerAudio;
using Compression.Registry;

namespace FileFormat.Smk;

/// <summary>
/// Walker for the Smacker container (<c>.smk</c>), following the header and frame layout used
/// by the FFmpeg demuxer and the public reverse-engineered format description. All multi-byte
/// integers are little-endian. Smacker carries one video track plus up to seven audio tracks;
/// each frame's payload is an optional palette block, the per-track audio chunks, and then the
/// encoded video data.
/// </summary>
internal static class SmkReader {

  private const int HeaderSize = 104;
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
    public readonly List<(int FrameIndex, byte[] Payload)> Packets = [];
  }

  public static void BuildEntries(byte[] b, List<AudioPseudoArchive.Entry> entries) {
    try {
      if (b.Length < HeaderSize)
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

      // AudioSize[7] occupies bytes 24..51, followed by the global Huffman tree blob size.
      var treeSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(52));
      if (treeSizeRaw > int.MaxValue)
        return;
      var treeSize = (int)treeSizeRaw;

      // The four video-tree allocation sizes occupy bytes 56..71. Audio descriptors are
      // seven packed u24 sample rates plus one flag byte, followed by a dummy u32.
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
      ap += 4;

      // Frame sizes (u32 × physical frames) then frame types (u8 × physical frames).
      var frameSizeBytes = checked(4 * frames);
      if (ap > b.Length - frameSizeBytes || ap + frameSizeBytes > b.Length - frames)
        return;
      var frameSizesStart = ap;
      var frmSize = new int[frames];
      for (var i = 0; i < frames; ++i) {
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(ap));
        if ((rawSize & ~3u) > int.MaxValue)
          return;
        frmSize[i] = (int)rawSize;
        ap += 4;
      }
      var frameTypesStart = ap;
      var frmFlags = new byte[frames];
      for (var i = 0; i < frames; ++i)
        frmFlags[i] = b[ap++];

      var treeStart = ap;
      var dataStart = checked(treeStart + treeSize);
      if (dataStart > b.Length)
        return;

      long totalFrameBytes = 0;
      foreach (var size in frmSize) {
        totalFrameBytes += (uint)size & ~3u;
        if (totalFrameBytes > int.MaxValue)
          return;
      }
      var dataEnd = checked(dataStart + (int)totalFrameBytes);
      if (dataEnd > b.Length)
        return;

      CollectAudio(b, dataStart, frmSize, frmFlags, tracks);

      // Exact structural pieces make the demux surface lossless enough to rebuild a container
      // without needing a Smacker video encoder. VIDEO.bin intentionally names the complete
      // physical frame-data region for compatibility with the older surface: it therefore
      // contains palette/audio/video bytes, not just the encoded video remainder.
      entries.Add(new("HEADER.bin", "Structure", b[..HeaderSize], Method: "Stored"));
      entries.Add(new("FRAME_SIZES.bin", "Structure", b.AsSpan(frameSizesStart, frameSizeBytes).ToArray(), Method: "Stored"));
      entries.Add(new("FRAME_TYPES.bin", "Structure", b.AsSpan(frameTypesStart, frames).ToArray(), Method: "Stored"));
      entries.Add(new("HUFFMAN.bin", "Structure", b.AsSpan(treeStart, treeSize).ToArray(), Method: "Stored"));

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
        sb.AppendLine($"chunks = {t.Packets.Count}");
      }
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

      if (dataStart < dataEnd)
        entries.Add(new("VIDEO.bin", "Track", b[dataStart..dataEnd], Method: "Stored"));
      else
        entries.Add(new("VIDEO.bin", "Track", [], Method: "Stored"));

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
      var frameEnd = checked(pos + frameSize);
      var p = pos;

      var trackFlags = frmFlags[f] >> 1;
      var paletteChange = (frmFlags[f] & FlagRingFrame) != 0;

      if (paletteChange && p < frameEnd) {
        var paletteSize = b[p] * 4;
        if (paletteSize <= 0 || paletteSize > frameEnd - p)
          return;
        p += paletteSize;
      }

      for (var i = 0; i < 7; ++i) {
        if ((trackFlags & (1 << i)) == 0)
          continue;
        if (p > frameEnd - 4)
          return;
        var sizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
        if (sizeRaw < 4 || sizeRaw > int.MaxValue || sizeRaw > (uint)(frameEnd - p))
          return;
        var size = (int)sizeRaw;
        tracks[i].Packets.Add((f, b[(p + 4)..(p + size)]));
        p += size;
      }

      pos = frameEnd;
    }
  }

  private static void AddAudioTrack(AudioTrack track, List<AudioPseudoArchive.Entry> entries) {
    var baseName = $"TRACK{track.Index}";
    var channels = track.Stereo ? 2 : 1;
    var bits = track.Is16Bit ? 16 : 8;

    entries.Add(new(
      $"{baseName}.packets",
      "PacketStream",
      SmkWriter.EncodeAudioPacketBundle(track.Packets),
      Method: DescribeCodec(track)
    ));

    using (var ms = new MemoryStream()) {
      foreach (var (_, payload) in track.Packets)
        ms.Write(payload);
      entries.Add(new($"{baseName}.bin", "Stream", ms.ToArray(), Method: DescribeCodec(track)));
    }

    if (track.Packets.Count == 0)
      return;

    if (track.Packed && !track.BinkAudio && !track.UseDct) {
      try {
        var codec = new SmackerAudioCodec(track.SampleRate, channels, bits);
        var interleaved = codec.DecodeStream(track.Packets.Select(static packet => packet.Payload));
        if (interleaved.Length == 0)
          return;
        var split = SplitNative(interleaved, channels, track.SampleRate, bits);
        foreach (var (name, wav) in split)
          entries.Add(new($"{baseName}_{name}.wav", "Channel", wav, Method: "pcm"));
      } catch {
        // Undecodable SMKA track — keep the packet/raw surfaces.
      }
      return;
    }

    if (!track.Packed && !track.BinkAudio && !track.UseDct) {
      try {
        using var ms = new MemoryStream();
        foreach (var (_, payload) in track.Packets)
          if (payload.Length > 4)
            ms.Write(payload.AsSpan(4));
        var raw = ms.ToArray();
        if (raw.Length == 0)
          return;
        var split = SplitNative(raw, channels, track.SampleRate, bits);
        foreach (var (name, wav) in split)
          entries.Add(new($"{baseName}_{name}.wav", "Channel", wav, Method: "pcm"));
      } catch {
        // Keep the packet/raw surfaces.
      }
    }
  }

  private static IReadOnlyList<(string Name, byte[] Wav)> SplitNative(byte[] interleaved, int channels, int sampleRate, int bits) {
    var split = PcmCodec.SplitInterleavedPcm(interleaved, channels, sampleRate, bits);
    return split.Select(static s => (s.Name, s.WavBlob)).ToList();
  }
}
