#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.BinkAudio;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Bik;

/// <summary>
/// Read-only walker for the Bink video container (<c>.bik</c>), porting the header layout
/// of FFmpeg's <c>libavformat/bink.c</c> (<c>read_header</c>/<c>read_packet</c>). All
/// multi-byte integers are little-endian. The container carries one video track plus up to
/// 256 audio tracks; each frame's payload begins with the per-track audio packets (each a
/// 4-byte length prefix + packet) followed by the video packet. This reader surfaces only
/// audio: video is exposed as a raw track blob, and each Bink Audio track is decoded to
/// per-channel WAVs (<see cref="BinkAudioCodec"/>) with a graceful fallback to the raw
/// concatenated packet blob. Bink 2 ('KB2') audio is not decoded — it is surfaced as a
/// blob only. Parsing degrades gracefully on truncation.
/// </summary>
internal static class BikReader {

  private const int BinkAud16Bits = 0x4000;
  private const int BinkAudStereo = 0x2000;
  private const int BinkAudUseDct = 0x1000;

  private sealed class AudioTrack {
    public int Index;
    public int SampleRate;
    public int Flags;
    public uint Id;
    public bool Stereo => (this.Flags & BinkAudStereo) != 0;
    public bool UseDct => (this.Flags & BinkAudUseDct) != 0;
    public bool Is16Bit => (this.Flags & BinkAud16Bits) != 0;
    public readonly List<byte[]> Packets = [];
  }

  public static void BuildEntries(byte[] b, List<AudioPseudoArchive.Entry> entries) {
    try {
      if (b.Length < 44)
        return;

      var sig = Encoding.ASCII.GetString(b, 0, 3);
      var revision = (char)b[3];
      var isBink2 = sig == "KB2";
      if (sig != "BIK" && !isBink2)
        return;

      var p = 4;
      var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)) + 8; p += 4;
      var numFrames = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
      p += 4; // largest frame size
      p += 4; // reserved
      var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
      var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
      var fpsNum = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
      var fpsDen = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
      p += 4; // video flags (extradata)

      var numAudioTracks = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
      if (numAudioTracks is < 0 or > 256)
        return;

      // BIK 'k' / KB2 'i','j','k' carry one extra unknown 32-bit field here.
      if ((sig == "BIK" && revision == 'k') ||
          (isBink2 && revision is 'i' or 'j' or 'k'))
        p += 4;

      var tracks = new AudioTrack[numAudioTracks];
      if (numAudioTracks > 0) {
        p += 4 * numAudioTracks; // per-track max decoded packet bytes

        for (var i = 0; i < numAudioTracks; ++i) {
          if (p + 4 > b.Length)
            return;
          var track = new AudioTrack {
            Index = i,
            SampleRate = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 2)),
          };
          p += 4;
          tracks[i] = track;
        }

        for (var i = 0; i < numAudioTracks; ++i) {
          if (p + 4 > b.Length)
            return;
          tracks[i].Id = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
          p += 4;
        }
      }

      // Frame index table: numFrames entries of next-frame offsets. The first offset is the
      // start of frame 0; each entry's low bit is the keyframe flag (masked off the offset).
      var frameOffsets = new long[numFrames + 1];
      var haveIndex = false;
      if (numFrames > 0 && p + 4 <= b.Length) {
        var next = (long)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        frameOffsets[0] = next & ~1L;
        for (var i = 0; i < numFrames; ++i) {
          long cur;
          if (i == numFrames - 1) {
            cur = fileSize;
          } else {
            if (p + 4 > b.Length) { cur = b.Length; }
            else { cur = (long)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; }
          }
          frameOffsets[i + 1] = cur & ~1L;
        }
        haveIndex = true;
      }

      // Walk each frame's payload to collect per-track audio packets.
      if (haveIndex)
        CollectAudioPackets(b, frameOffsets, numFrames, tracks);

      // Metadata.
      var sb = new StringBuilder();
      sb.AppendLine("[Bink]");
      sb.AppendLine($"signature = {sig}{revision}");
      sb.AppendLine($"bink2 = {isBink2}");
      sb.AppendLine($"frames = {numFrames}");
      sb.AppendLine($"width = {width}");
      sb.AppendLine($"height = {height}");
      sb.AppendLine($"fps = {fpsNum}/{fpsDen}");
      sb.AppendLine($"audio_tracks = {numAudioTracks}");
      for (var i = 0; i < numAudioTracks; ++i) {
        var t = tracks[i];
        sb.AppendLine($"[Track{i}]");
        sb.AppendLine($"sample_rate = {t.SampleRate}");
        sb.AppendLine($"channels = {(t.Stereo ? 2 : 1)}");
        sb.AppendLine($"bits = {(t.Is16Bit ? 16 : 8)}");
        sb.AppendLine($"codec = {(t.UseDct ? "binkaudio_dct" : "binkaudio_rdft")}");
        sb.AppendLine($"packets = {t.Packets.Count}");
      }
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

      // VIDEO track: surfaced as a raw, stored blob spanning the frame data region. The
      // workbench surfaces only audio, so the video stays in its coded container form.
      if (haveIndex && numFrames > 0) {
        var start = frameOffsets[0];
        var end = Math.Min(frameOffsets[numFrames], b.Length);
        if (start >= 0 && start < end)
          entries.Add(new("VIDEO.bin", "Track", b[(int)start..(int)end], Method: "Stored"));
      }

      // Audio tracks.
      for (var i = 0; i < numAudioTracks; ++i)
        AddAudioTrack(tracks[i], isBink2, revision, entries);
    } catch {
      // Graceful degradation — keep whatever parsed so far.
    }
  }

  private static void CollectAudioPackets(byte[] b, long[] frameOffsets, int numFrames, AudioTrack[] tracks) {
    for (var f = 0; f < numFrames; ++f) {
      var start = frameOffsets[f];
      var end = Math.Min(frameOffsets[f + 1], b.Length);
      if (start < 0 || start >= end)
        continue;
      var p = (int)start;
      var frameEnd = (int)end;

      // Per audio track: 4-byte packet length then the packet bytes.
      foreach (var track in tracks) {
        if (p + 4 > frameEnd)
          break;
        var audioSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
        p += 4;
        if (audioSize < 0 || p + audioSize > frameEnd)
          break;
        if (audioSize >= 4)
          track.Packets.Add(b[p..(p + audioSize)]);
        p += audioSize;
      }
      // Remaining bytes of the frame are the video packet — ignored for audio extraction.
    }
  }

  private static void AddAudioTrack(AudioTrack track, bool isBink2, char revision,
      List<AudioPseudoArchive.Entry> entries) {
    var baseName = $"TRACK{track.Index}";
    var channels = track.Stereo ? 2 : 1;

    // Raw concatenated packet blob (always available as a fallback view).
    using (var ms = new MemoryStream()) {
      foreach (var pkt in track.Packets)
        ms.Write(pkt);
      var raw = ms.ToArray();
      var method = isBink2 ? "binkaudio_unsupported" : (track.UseDct ? "binkaudio_dct" : "binkaudio_rdft");
      entries.Add(new($"{baseName}.bin", "Stream", raw, Method: method));
    }

    // Bink 2 audio is not decoded — surface the blob only.
    if (isBink2 || track.Packets.Count == 0)
      return;

    try {
      var versionB = revision == 'b';
      var codec = new BinkAudioCodec(track.SampleRate, channels, track.UseDct, versionB);
      var interleaved = codec.DecodeStream(track.Packets);
      if (interleaved.Length == 0)
        return;

      var le = new byte[interleaved.Length * 2];
      for (var i = 0; i < interleaved.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), interleaved[i]);

      var split = PcmCodec.SplitInterleavedPcm(le, channels, track.SampleRate, bitsPerSample: 16);
      foreach (var (name, wav) in split)
        entries.Add(new($"{baseName}_{name}.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Undecodable Bink Audio track — keep the raw blob only.
    }
  }
}
