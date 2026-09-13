#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.BinkAudio;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Bik;

/// <summary>
/// Packet-aware walker for the Bink video container (<c>.bik</c>). Header/index semantics
/// follow the public Bink container description and FFmpeg's LGPL Bink demuxer: all integers
/// are little-endian, frame-index offsets are absolute with bit zero carrying keyframe state,
/// and every frame stores one length-prefixed packet per audio track before its video packet.
/// The pseudo-archive keeps those packet boundaries in <c>metadata.ini</c> so
/// <see cref="BikWriter"/> can remux the elementary streams without codec re-encoding.
/// </summary>
internal static class BikReader {

  private const int BinkAud16Bits = 0x4000;
  private const int BinkAudStereo = 0x2000;
  private const int BinkAudUseDct = 0x1000;
  private const int MaxFrames = 1_000_000;

  private sealed class AudioTrack {
    public int Index;
    public uint MaxDecodedSize;
    public int SampleRate;
    public int Flags;
    public uint Id;
    public bool Stereo => (this.Flags & BinkAudStereo) != 0;
    public bool UseDct => (this.Flags & BinkAudUseDct) != 0;
    public bool Is16Bit => (this.Flags & BinkAud16Bits) != 0;
    public readonly List<byte[]> Packets = [];
  }

  private sealed record Frame(bool KeyFrame, byte[] VideoPacket, int[] AudioSizes);

  public static void BuildEntries(byte[] b, List<AudioPseudoArchive.Entry> entries) {
    try {
      if (b.Length < 44)
        return;

      var sig = Encoding.ASCII.GetString(b, 0, 3);
      var revision = (char)b[3];
      var signature = $"{sig}{revision}";
      var isBink2 = sig == "KB2";
      if (sig != "BIK" && !isBink2)
        return;

      var p = 4;
      var storedFileSize = ReadUInt32(b, ref p);
      var fileSize = (long)storedFileSize + 8;
      var frameCountRaw = ReadUInt32(b, ref p);
      if (frameCountRaw is 0 or > MaxFrames)
        return;
      var numFrames = (int)frameCountRaw;
      var largestFrameSize = ReadUInt32(b, ref p);
      var reserved = ReadUInt32(b, ref p);
      var width = ReadUInt32(b, ref p);
      var height = ReadUInt32(b, ref p);
      var fpsNum = ReadUInt32(b, ref p);
      var fpsDen = ReadUInt32(b, ref p);
      var videoFlags = ReadUInt32(b, ref p);

      var numAudioTracksRaw = ReadUInt32(b, ref p);
      if (numAudioTracksRaw > 256)
        return;
      var numAudioTracks = (int)numAudioTracksRaw;

      uint? extraHeader = null;
      if (HasExtraHeader(signature))
        extraHeader = ReadUInt32(b, ref p);

      var tracks = new AudioTrack[numAudioTracks];
      var maxDecodedSizes = new uint[numAudioTracks];
      for (var i = 0; i < numAudioTracks; ++i)
        maxDecodedSizes[i] = ReadUInt32(b, ref p);

      for (var i = 0; i < numAudioTracks; ++i) {
        EnsureAvailable(b, p, 4);
        tracks[i] = new AudioTrack {
          Index = i,
          MaxDecodedSize = maxDecodedSizes[i],
          SampleRate = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2)),
          Flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 2, 2)),
        };
        p += 4;
      }

      for (var i = 0; i < numAudioTracks; ++i)
        tracks[i].Id = ReadUInt32(b, ref p);

      // FFmpeg stores exactly numFrames index words: frame 0's offset first, then one word
      // for every later frame. The end of the last frame is the file-size field. Frame 0 is
      // intrinsically a keyframe; for later frames bit zero of their own offset is the flag.
      var frameOffsets = new long[numFrames + 1];
      var keyFrames = new bool[numFrames];
      var firstOffset = ReadUInt32(b, ref p);
      frameOffsets[0] = firstOffset & ~1L;
      keyFrames[0] = true;
      for (var frameIndex = 1; frameIndex < numFrames; ++frameIndex) {
        var indexedOffset = ReadUInt32(b, ref p);
        frameOffsets[frameIndex] = indexedOffset & ~1L;
        keyFrames[frameIndex] = (indexedOffset & 1) != 0;
      }
      frameOffsets[numFrames] = Math.Min(fileSize, b.LongLength) & ~1L;

      for (var frameIndex = 0; frameIndex < numFrames; ++frameIndex)
        if (frameOffsets[frameIndex] < 0 || frameOffsets[frameIndex + 1] <= frameOffsets[frameIndex])
          return;

      var frames = CollectPackets(b, frameOffsets, keyFrames, tracks);
      if (frames is null)
        return;

      var sb = new StringBuilder();
      sb.AppendLine("[Bink]");
      sb.AppendLine($"signature = {signature}");
      sb.AppendLine($"bink2 = {isBink2}");
      sb.AppendLine($"frames = {numFrames}");
      sb.AppendLine($"largest_frame_size = {largestFrameSize}");
      sb.AppendLine($"reserved = {reserved}");
      sb.AppendLine($"width = {width}");
      sb.AppendLine($"height = {height}");
      sb.AppendLine($"fps = {fpsNum}/{fpsDen}");
      sb.AppendLine($"fps_num = {fpsNum}");
      sb.AppendLine($"fps_den = {fpsDen}");
      sb.AppendLine($"video_flags = {videoFlags}");
      sb.AppendLine($"audio_tracks = {numAudioTracks}");
      if (extraHeader.HasValue)
        sb.AppendLine($"extra_header = {extraHeader.Value}");

      for (var i = 0; i < numAudioTracks; ++i) {
        var track = tracks[i];
        sb.AppendLine($"[Track{i}]");
        sb.AppendLine($"max_decoded_size = {track.MaxDecodedSize}");
        sb.AppendLine($"sample_rate = {track.SampleRate}");
        sb.AppendLine($"flags = {track.Flags}");
        sb.AppendLine($"id = {track.Id}");
        sb.AppendLine($"channels = {(track.Stereo ? 2 : 1)}");
        sb.AppendLine($"bits = {(track.Is16Bit ? 16 : 8)}");
        sb.AppendLine($"codec = {(track.UseDct ? "binkaudio_dct" : "binkaudio_rdft")}");
        sb.AppendLine($"packets = {track.Packets.Count(static packet => packet.Length >= 4)}");
      }

      for (var frameIndex = 0; frameIndex < frames.Count; ++frameIndex) {
        var frame = frames[frameIndex];
        sb.AppendLine($"[Frame{frameIndex}]");
        sb.AppendLine($"keyframe = {frame.KeyFrame.ToString().ToLowerInvariant()}");
        sb.AppendLine($"video_size = {frame.VideoPacket.Length}");
        for (var trackIndex = 0; trackIndex < frame.AudioSizes.Length; ++trackIndex)
          sb.AppendLine($"audio{trackIndex}_size = {frame.AudioSizes[trackIndex]}");
      }
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

      // Elementary coded video stream. Frame boundaries live in metadata.ini; keeping only
      // video bytes here is what makes extract -> create a true packet-preserving remux.
      using (var video = new MemoryStream()) {
        foreach (var frame in frames)
          video.Write(frame.VideoPacket);
        entries.Add(new("VIDEO.bin", "Track", video.ToArray(), Method: "Stored"));
      }

      for (var i = 0; i < numAudioTracks; ++i)
        AddAudioTrack(tracks[i], isBink2, revision, entries);
    } catch {
      // Graceful degradation — FULL.bik, which the descriptor adds before invoking us,
      // remains available even when a damaged secondary view cannot be constructed.
    }
  }

  private static List<Frame>? CollectPackets(
      byte[] b, long[] frameOffsets, bool[] keyFrames, AudioTrack[] tracks) {
    var frames = new List<Frame>(keyFrames.Length);

    for (var frameIndex = 0; frameIndex < keyFrames.Length; ++frameIndex) {
      var start = frameOffsets[frameIndex];
      var end = Math.Min(frameOffsets[frameIndex + 1], b.LongLength);
      if (start < 0 || start >= end || start > int.MaxValue || end > int.MaxValue)
        return null;

      var p = (int)start;
      var frameEnd = (int)end;
      var audioSizes = new int[tracks.Length];

      for (var trackIndex = 0; trackIndex < tracks.Length; ++trackIndex) {
        if (p > frameEnd - 4)
          return null;
        var audioSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
        p += 4;
        if (audioSizeRaw > int.MaxValue)
          return null;
        var audioSize = (int)audioSizeRaw;
        if (audioSize > frameEnd - p)
          return null;

        audioSizes[trackIndex] = audioSize;
        var packet = b[p..(p + audioSize)];
        tracks[trackIndex].Packets.Add(packet);
        p += audioSize;
      }

      frames.Add(new Frame(keyFrames[frameIndex], b[p..frameEnd], audioSizes));
    }

    return frames;
  }

  private static void AddAudioTrack(AudioTrack track, bool isBink2, char revision,
      List<AudioPseudoArchive.Entry> entries) {
    var baseName = $"TRACK{track.Index}";
    var channels = track.Stereo ? 2 : 1;

    using (var ms = new MemoryStream()) {
      foreach (var packet in track.Packets)
        ms.Write(packet);
      var raw = ms.ToArray();
      var method = isBink2 ? "binkaudio_unsupported" : (track.UseDct ? "binkaudio_dct" : "binkaudio_rdft");
      entries.Add(new($"{baseName}.bin", "Stream", raw, Method: method));
    }

    if (isBink2)
      return;

    var decodablePackets = track.Packets.Where(static packet => packet.Length >= 4).ToArray();
    if (decodablePackets.Length == 0)
      return;

    try {
      var versionB = revision == 'b';
      var codec = new BinkAudioCodec(track.SampleRate, channels, track.UseDct, versionB);
      var interleaved = codec.DecodeStream(decodablePackets);
      if (interleaved.Length == 0)
        return;

      var le = new byte[interleaved.Length * 2];
      for (var i = 0; i < interleaved.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), interleaved[i]);

      var split = PcmCodec.SplitInterleavedPcm(le, channels, track.SampleRate, bitsPerSample: 16);
      foreach (var (name, wav) in split)
        entries.Add(new($"{baseName}_{name}.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Undecodable Bink Audio track — keep the raw coded stream only.
    }
  }

  private static uint ReadUInt32(byte[] bytes, ref int offset) {
    EnsureAvailable(bytes, offset, 4);
    var result = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    offset += 4;
    return result;
  }

  private static void EnsureAvailable(byte[] bytes, int offset, int count) {
    if (offset < 0 || count < 0 || offset > bytes.Length - count)
      throw new EndOfStreamException();
  }

  private static bool HasExtraHeader(string signature)
    => signature is "BIKk" or "KB2i" or "KB2j" or "KB2k";
}
