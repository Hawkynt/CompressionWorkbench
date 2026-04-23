#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Text;

namespace Codec.Vorbis;

/// <summary>
/// Vorbis I identification, comment and setup packet parser. Holds every
/// codebook, floor, residue, mapping and mode the audio packets need to decode.
/// </summary>
internal sealed class VorbisSetup {
  public int Version;
  public int Channels;
  public int SampleRate;
  public int BitrateMaximum;
  public int BitrateNominal;
  public int BitrateMinimum;
  public int Blocksize0; // short block
  public int Blocksize1; // long block
  public string Vendor = "";
  public VorbisCodebook[] Codebooks = [];
  public Floor[] Floors = [];
  public Residue[] Residues = [];
  public Mapping[] Mappings = [];
  public Mode[] Modes = [];

  public static VorbisSetup ParseIdentification(byte[] packet) {
    if (packet.Length < 30 || packet[0] != 0x01
        || packet[1] != 'v' || packet[2] != 'o' || packet[3] != 'r' || packet[4] != 'b'
        || packet[5] != 'i' || packet[6] != 's')
      throw new InvalidDataException("Vorbis: identification packet missing '\\x01vorbis' header.");

    var s = new VorbisSetup {
      Version = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(7, 4)),
      Channels = packet[11],
      SampleRate = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12, 4)),
      BitrateMaximum = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(16, 4)),
      BitrateNominal = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(20, 4)),
      BitrateMinimum = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(24, 4)),
    };
    if (s.Version != 0)
      throw new InvalidDataException($"Vorbis: unsupported bitstream version {s.Version}.");
    if (s.Channels < 1 || s.SampleRate < 1)
      throw new InvalidDataException("Vorbis: invalid channel count or sample rate.");
    var blocksizes = packet[28];
    s.Blocksize0 = 1 << (blocksizes & 0x0F);
    s.Blocksize1 = 1 << ((blocksizes >> 4) & 0x0F);
    if (s.Blocksize0 > s.Blocksize1)
      throw new InvalidDataException("Vorbis: blocksize_0 must be <= blocksize_1.");
    var framing = packet[29];
    if ((framing & 1) == 0)
      throw new InvalidDataException("Vorbis: identification framing bit not set.");
    return s;
  }

  public void ParseComment(byte[] packet) {
    if (packet.Length < 7 || packet[0] != 0x03
        || packet[1] != 'v' || packet[2] != 'o' || packet[3] != 'r' || packet[4] != 'b'
        || packet[5] != 'i' || packet[6] != 's')
      throw new InvalidDataException("Vorbis: comment packet missing '\\x03vorbis' header.");
    var p = 7;
    if (p + 4 > packet.Length) return;
    var vlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(p, 4));
    p += 4;
    if (p + vlen > packet.Length) return;
    this.Vendor = Encoding.UTF8.GetString(packet, p, vlen);
    // We deliberately do not surface the user comment list; callers who need it
    // can reuse FileFormat.Ogg.VorbisCommentReader on the same buffer.
  }

  public void ParseSetup(byte[] packet) {
    if (packet.Length < 7 || packet[0] != 0x05
        || packet[1] != 'v' || packet[2] != 'o' || packet[3] != 'r' || packet[4] != 'b'
        || packet[5] != 'i' || packet[6] != 's')
      throw new InvalidDataException("Vorbis: setup packet missing '\\x05vorbis' header.");
    // Skip the 7-byte header prefix by pre-reading it.
    var body = new byte[packet.Length - 7];
    Buffer.BlockCopy(packet, 7, body, 0, body.Length);
    var br = new VorbisBitReader(body);

    // --- Codebooks ---
    var cbCount = (int)br.ReadBits(8) + 1;
    this.Codebooks = new VorbisCodebook[cbCount];
    for (var i = 0; i < cbCount; ++i)
      this.Codebooks[i] = VorbisCodebook.Read(br);

    // --- Time-domain transforms (placeholder, must be zero per spec) ---
    var tdCount = (int)br.ReadBits(6) + 1;
    for (var i = 0; i < tdCount; ++i) {
      var td = br.ReadBits(16);
      if (td != 0)
        throw new InvalidDataException($"Vorbis: time-domain transform type {td} is not zero.");
    }

    // --- Floors ---
    var floorCount = (int)br.ReadBits(6) + 1;
    this.Floors = new Floor[floorCount];
    for (var i = 0; i < floorCount; ++i) {
      var type = (int)br.ReadBits(16);
      if (type == 0)
        throw new NotSupportedException(
          "Vorbis: floor 0 decoding is not supported (floor 0 has not been produced " +
          "by mainstream Vorbis encoders since ~2004). Contributions welcome.");
      if (type != 1)
        throw new InvalidDataException($"Vorbis: unknown floor type {type}.");
      this.Floors[i] = ReadFloor1(br, cbCount);
    }

    // --- Residues ---
    var resCount = (int)br.ReadBits(6) + 1;
    this.Residues = new Residue[resCount];
    for (var i = 0; i < resCount; ++i) {
      var type = (int)br.ReadBits(16);
      if (type < 0 || type > 2)
        throw new InvalidDataException($"Vorbis: unknown residue type {type}.");
      this.Residues[i] = ReadResidue(br, type, cbCount);
    }

    // --- Mappings ---
    var mapCount = (int)br.ReadBits(6) + 1;
    this.Mappings = new Mapping[mapCount];
    for (var i = 0; i < mapCount; ++i) {
      var type = (int)br.ReadBits(16);
      if (type != 0)
        throw new InvalidDataException($"Vorbis: unknown mapping type {type}.");
      this.Mappings[i] = ReadMapping(br, this.Channels, floorCount, resCount);
    }

    // --- Modes ---
    var modeCount = (int)br.ReadBits(6) + 1;
    this.Modes = new Mode[modeCount];
    for (var i = 0; i < modeCount; ++i) {
      this.Modes[i] = new Mode {
        BlockFlag = br.ReadBits(1) != 0,
        WindowType = (int)br.ReadBits(16),
        TransformType = (int)br.ReadBits(16),
        Mapping = (int)br.ReadBits(8),
      };
      if (this.Modes[i].WindowType != 0 || this.Modes[i].TransformType != 0)
        throw new InvalidDataException("Vorbis: only window_type 0 + transform_type 0 are defined.");
    }

    var framing = br.ReadBits(1);
    if (framing != 1)
      throw new InvalidDataException("Vorbis: setup packet framing bit missing.");
  }

  // ── floor 1 ────────────────────────────────────────────────────────────
  internal sealed class Floor {
    public int[] PartitionClassList = [];
    public int[] ClassDimensions = [];
    public int[] ClassSubclasses = [];
    public int[] ClassMasterbooks = [];
    public int[][] SubclassBooks = [];
    public int Multiplier;
    public int RangeBits;
    public int[] XList = [];
  }

  private static Floor ReadFloor1(VorbisBitReader br, int codebookCount) {
    var partitions = (int)br.ReadBits(5);
    var f = new Floor { PartitionClassList = new int[partitions] };
    var maxClass = -1;
    for (var i = 0; i < partitions; ++i) {
      f.PartitionClassList[i] = (int)br.ReadBits(4);
      if (f.PartitionClassList[i] > maxClass) maxClass = f.PartitionClassList[i];
    }
    var classCount = maxClass + 1;
    f.ClassDimensions = new int[classCount];
    f.ClassSubclasses = new int[classCount];
    f.ClassMasterbooks = new int[classCount];
    f.SubclassBooks = new int[classCount][];
    for (var i = 0; i < classCount; ++i) {
      f.ClassDimensions[i] = (int)br.ReadBits(3) + 1;
      f.ClassSubclasses[i] = (int)br.ReadBits(2);
      if (f.ClassSubclasses[i] != 0)
        f.ClassMasterbooks[i] = (int)br.ReadBits(8);
      var subN = 1 << f.ClassSubclasses[i];
      f.SubclassBooks[i] = new int[subN];
      for (var j = 0; j < subN; ++j)
        f.SubclassBooks[i][j] = (int)br.ReadBits(8) - 1;
    }
    f.Multiplier = (int)br.ReadBits(2) + 1;
    f.RangeBits = (int)br.ReadBits(4);
    var count = 2;
    for (var i = 0; i < partitions; ++i)
      count += f.ClassDimensions[f.PartitionClassList[i]];
    f.XList = new int[count];
    f.XList[0] = 0;
    f.XList[1] = 1 << f.RangeBits;
    var idx = 2;
    for (var i = 0; i < partitions; ++i) {
      var c = f.PartitionClassList[i];
      for (var j = 0; j < f.ClassDimensions[c]; ++j)
        f.XList[idx++] = (int)br.ReadBits(f.RangeBits);
    }
    _ = codebookCount;
    return f;
  }

  // ── residue ────────────────────────────────────────────────────────────
  internal sealed class Residue {
    public int Type;
    public int Begin;
    public int End;
    public int PartitionSize;
    public int Classifications;
    public int Classbook;
    public int[] Cascade = [];
    public int[,] Books = new int[0, 0];
  }

  private static Residue ReadResidue(VorbisBitReader br, int type, int codebookCount) {
    var r = new Residue {
      Type = type,
      Begin = (int)br.ReadBits(24),
      End = (int)br.ReadBits(24),
      PartitionSize = (int)br.ReadBits(24) + 1,
      Classifications = (int)br.ReadBits(6) + 1,
      Classbook = (int)br.ReadBits(8),
    };
    r.Cascade = new int[r.Classifications];
    for (var i = 0; i < r.Classifications; ++i) {
      var lowBits = (int)br.ReadBits(3);
      var bitflag = br.ReadBits(1) != 0;
      var highBits = bitflag ? (int)br.ReadBits(5) : 0;
      r.Cascade[i] = (highBits << 3) | lowBits;
    }
    r.Books = new int[r.Classifications, 8];
    for (var i = 0; i < r.Classifications; ++i)
      for (var j = 0; j < 8; ++j)
        r.Books[i, j] = ((r.Cascade[i] >> j) & 1) != 0 ? (int)br.ReadBits(8) : -1;
    _ = codebookCount;
    return r;
  }

  // ── mapping ────────────────────────────────────────────────────────────
  internal sealed class Mapping {
    public int[] Mux = [];
    public int[] Submap_Floor = [];
    public int[] Submap_Residue = [];
    public int[] CouplingMagnitude = [];
    public int[] CouplingAngle = [];
  }

  private static Mapping ReadMapping(VorbisBitReader br, int channels, int floorCount, int residueCount) {
    var submaps = br.ReadBits(1) != 0 ? (int)br.ReadBits(4) + 1 : 1;
    var m = new Mapping();
    if (br.ReadBits(1) != 0) {
      var couplingSteps = (int)br.ReadBits(8) + 1;
      m.CouplingMagnitude = new int[couplingSteps];
      m.CouplingAngle = new int[couplingSteps];
      var bits = BitsFor(channels - 1);
      for (var i = 0; i < couplingSteps; ++i) {
        m.CouplingMagnitude[i] = (int)br.ReadBits(bits);
        m.CouplingAngle[i] = (int)br.ReadBits(bits);
        if (m.CouplingMagnitude[i] == m.CouplingAngle[i])
          throw new InvalidDataException("Vorbis: coupling magnitude == angle channel.");
      }
    }
    var reserved = br.ReadBits(2);
    if (reserved != 0)
      throw new InvalidDataException("Vorbis: mapping reserved bits non-zero.");

    m.Mux = new int[channels];
    if (submaps > 1) {
      for (var c = 0; c < channels; ++c) m.Mux[c] = (int)br.ReadBits(4);
    }
    m.Submap_Floor = new int[submaps];
    m.Submap_Residue = new int[submaps];
    for (var i = 0; i < submaps; ++i) {
      br.ReadBits(8); // discarded "time placeholder"
      m.Submap_Floor[i] = (int)br.ReadBits(8);
      m.Submap_Residue[i] = (int)br.ReadBits(8);
    }
    _ = floorCount; _ = residueCount;
    return m;
  }

  // ── mode ───────────────────────────────────────────────────────────────
  internal sealed class Mode {
    public bool BlockFlag;
    public int WindowType;
    public int TransformType;
    public int Mapping;
  }

  private static int BitsFor(int value) {
    var bits = 0;
    while (value > 0) { bits++; value >>= 1; }
    return bits;
  }
}
