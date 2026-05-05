#pragma warning disable CS1591

namespace Codec.Vorbis;

/// <summary>
/// Vorbis-stream metadata returned by <see cref="VorbisCodec.ReadStreamInfo"/>.
/// </summary>
/// <param name="SampleRate">Sample rate in Hz, from the identification packet.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="NominalBitrate">Nominal bitrate in bits/second (0 if absent).</param>
/// <param name="Vendor">Encoder vendor string (from the comment packet).</param>
/// <param name="DurationSamples">Total duration in samples, or <c>null</c> if not derivable.</param>
public sealed record VorbisStreamInfo(
  int SampleRate,
  int Channels,
  int NominalBitrate,
  string? Vendor,
  long? DurationSamples
);

/// <summary>
/// Ogg Vorbis I decoder. Reads an Ogg-wrapped Vorbis bitstream and produces
/// interleaved little-endian 16-bit PCM. Clean-room port — uses the public
/// Vorbis I specification (xiph.org) and stb_vorbis.c v1.22 (public domain,
/// Sean Barrett, 2007–2021) as a structural reference.
/// <para>
/// Supported: Ogg page reassembly, identification + comment + setup packets,
/// codebook lookup types 0/1/2, floor 1, residue types 0/1/2, single-step
/// channel coupling (stereo and beyond), short/long-block IMDCT with the
/// Vorbis sine window and overlap-add, output clipping to int16.
/// </para>
/// <para>
/// Deferred / not implemented: floor 0 (very rare; throws
/// <see cref="NotSupportedException"/>), chained logical bitstreams beyond
/// the first one, low-latency seeking, 24-bit output. The IMDCT uses an
/// O(N²) direct form rather than a butterfly factorisation — correct but slow
/// for long blocks.
/// </para>
/// </summary>
public static class VorbisCodec {

  /// <summary>
  /// Reads the identification + comment packets and returns metadata without
  /// decoding any audio frames.
  /// </summary>
  public static VorbisStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAllBytes(input);
    var setup = new VorbisSetup();
    var packetCount = 0;
    string? vendor = null;
    foreach (var packet in OggPageReader.ReadPackets(data)) {
      switch (packetCount) {
        case 0:
          var ident = VorbisSetup.ParseIdentification(packet.Data);
          setup = ident;
          break;
        case 1:
          setup.ParseComment(packet.Data);
          vendor = setup.Vendor;
          break;
      }
      packetCount++;
      if (packetCount >= 2) break;
    }
    if (packetCount < 1)
      throw new InvalidDataException("Vorbis: no Ogg packets found.");
    return new VorbisStreamInfo(setup.SampleRate, setup.Channels, setup.BitrateNominal, vendor, null);
  }

  /// <summary>
  /// Decompresses an Ogg-Vorbis stream to interleaved little-endian 16-bit PCM
  /// on <paramref name="output"/>.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var data = ReadAllBytes(input);
    var setup = new VorbisSetup();
    var packetIdx = 0;
    var prevBlockLong = false;
    var hasPrev = false;
    float[][]? overlap = null; // overlap-add carry-over per channel

    foreach (var packet in OggPageReader.ReadPackets(data)) {
      if (packet.Data.Length == 0) continue;
      switch (packetIdx) {
        case 0:
          setup = VorbisSetup.ParseIdentification(packet.Data);
          break;
        case 1:
          setup.ParseComment(packet.Data);
          break;
        case 2:
          setup.ParseSetup(packet.Data);
          overlap = new float[setup.Channels][];
          for (var c = 0; c < setup.Channels; ++c) overlap[c] = new float[setup.Blocksize1 / 2];
          break;
        default:
          DecodeAudioPacket(packet.Data, setup, output, overlap!, ref prevBlockLong, ref hasPrev);
          break;
      }
      packetIdx++;
    }
  }

  private static void DecodeAudioPacket(
    byte[] packet,
    VorbisSetup setup,
    Stream output,
    float[][] overlap,
    ref bool prevBlockLong,
    ref bool hasPrev
  ) {
    var br = new VorbisBitReader(packet);
    if (br.ReadBits(1) != 0) return; // packet type bit must be 0 for audio

    var modeBits = IntegerBitsFor(setup.Modes.Length - 1);
    var modeIdx = (int)br.ReadBits(modeBits);
    if (modeIdx < 0 || modeIdx >= setup.Modes.Length) return;
    var mode = setup.Modes[modeIdx];
    var blockLong = mode.BlockFlag;
    bool prevWindowLong = blockLong, nextWindowLong = blockLong;
    if (blockLong) {
      prevWindowLong = br.ReadBits(1) != 0;
      nextWindowLong = br.ReadBits(1) != 0;
    }
    var n = blockLong ? setup.Blocksize1 : setup.Blocksize0;
    var half = n / 2;

    var mapping = setup.Mappings[mode.Mapping];

    // ── floors per channel ──
    var floors = new float[setup.Channels][];
    var noResidue = new bool[setup.Channels];
    for (var c = 0; c < setup.Channels; ++c) {
      var submap = mapping.Mux[c];
      var floorIdx = mapping.Submap_Floor[submap];
      floors[c] = new float[half];
      if (!VorbisFloor.DecodePacket(br, setup.Floors[floorIdx], setup.Codebooks, floors[c]))
        noResidue[c] = true;
    }

    // ── coupling propagation: if either side of a coupled pair has data, both must decode ──
    foreach (var step in Enumerable.Range(0, mapping.CouplingMagnitude.Length)) {
      var m = mapping.CouplingMagnitude[step];
      var a = mapping.CouplingAngle[step];
      if (!noResidue[m] || !noResidue[a]) { noResidue[m] = false; noResidue[a] = false; }
    }

    // ── residue per submap ──
    var residueVecs = new float[setup.Channels][];
    for (var c = 0; c < setup.Channels; ++c) residueVecs[c] = new float[half];
    var submaps = mapping.Submap_Floor.Length;
    for (var sm = 0; sm < submaps; ++sm) {
      var chList = new List<int>();
      for (var c = 0; c < setup.Channels; ++c) if (mapping.Mux[c] == sm) chList.Add(c);
      if (chList.Count == 0) continue;
      var vecSubset = new float[chList.Count][];
      var dndSubset = new bool[chList.Count];
      for (var i = 0; i < chList.Count; ++i) {
        vecSubset[i] = residueVecs[chList[i]];
        dndSubset[i] = noResidue[chList[i]];
      }
      var residueIdx = mapping.Submap_Residue[sm];
      VorbisResidue.Decode(br, setup.Residues[residueIdx], setup.Codebooks, vecSubset, dndSubset, half);
    }

    // ── inverse coupling ──
    VorbisMapping.DecouplePolar(mapping, residueVecs, half);

    // ── dot product floor * residue ──
    for (var c = 0; c < setup.Channels; ++c) {
      var f = floors[c]; var r = residueVecs[c];
      for (var i = 0; i < half; ++i) r[i] *= f[i];
    }

    // ── IMDCT, window, overlap-add, emit ──
    var window = VorbisImdct.BuildWindow(n, prevWindowLong, nextWindowLong, setup.Blocksize0, setup.Blocksize1);
    var time = new float[n];
    var pcmOut = new float[setup.Channels][];
    for (var c = 0; c < setup.Channels; ++c) {
      VorbisImdct.Inverse(residueVecs[c], time, n);
      for (var i = 0; i < n; ++i) time[i] *= window[i];
      var carry = overlap[c];
      // Emit half-block: previous-tail + first half of current block.
      pcmOut[c] = new float[half];
      if (hasPrev) {
        for (var i = 0; i < half; ++i) pcmOut[c][i] = carry[i] + time[i];
      }
      // Save second half of current block to carry into next packet.
      var newCarry = new float[half];
      Array.Copy(time, half, newCarry, 0, half);
      overlap[c] = newCarry;
    }

    if (hasPrev) {
      // Interleave + clip to int16 LE.
      var buf = new byte[setup.Channels * half * 2];
      var bp = 0;
      for (var i = 0; i < half; ++i) {
        for (var c = 0; c < setup.Channels; ++c) {
          var v = pcmOut[c][i] * 32768f;
          var s = (int)Math.Round(v);
          if (s > short.MaxValue) s = short.MaxValue;
          if (s < short.MinValue) s = short.MinValue;
          buf[bp++] = (byte)(s & 0xFF);
          buf[bp++] = (byte)((s >> 8) & 0xFF);
        }
      }
      output.Write(buf, 0, buf.Length);
    }
    hasPrev = true;
    prevBlockLong = blockLong;
    _ = prevBlockLong;
  }

  private static byte[] ReadAllBytes(Stream s) {
    if (s is MemoryStream ms) return ms.ToArray();
    using var copy = new MemoryStream();
    s.CopyTo(copy);
    return copy.ToArray();
  }

  private static int IntegerBitsFor(int value) {
    if (value <= 0) return 0;
    var bits = 0;
    while (value > 0) { bits++; value >>= 1; }
    return bits;
  }
}
