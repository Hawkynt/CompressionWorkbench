// Implemented from docs/CTW-ON-DISK.md (a specification written from the CTW 0.1 reference
// software, which carries no licence). No reference source was copied; the tables in
// CtwTables.cs are the format's constants.
namespace FileFormat.Ctw;

/// <summary>The parameters a CTW 0.1 file records in its header.</summary>
public sealed record CtwSettings(
  int TreeDepth = 6,
  int Probes = 32,
  int NodeCountLog2 = 22,
  int FileBufferLog2 = 22,
  int MaxLogBetaLog2 = 10,
  bool StrictPruning = true,
  bool RootWeighting = false,
  bool ZeroRedundancy = true) {

  /// <summary>Throws when a value is outside what the format and the reference tool accept.</summary>
  public void Validate() {
    if (TreeDepth is < 1 or > 12)
      throw new InvalidDataException($"CTW: tree depth {TreeDepth} outside 1..12");
    if (TreeDepth == 12)
      throw new NotSupportedException("CTW: at depth 12 the reference writes past its context buffer, so its files depend on its memory layout; depth 12 is refused.");
    if (Probes is < 1 or > 32)
      throw new InvalidDataException($"CTW: probe count {Probes} outside 1..32");
    if (NodeCountLog2 is < 10 or > 24)
      throw new InvalidDataException($"CTW: node table of 2^{NodeCountLog2} outside 2^10..2^24");
    if (FileBufferLog2 is < 9 or > 24)
      throw new InvalidDataException($"CTW: file buffer of 2^{FileBufferLog2} outside 2^9..2^24");
    if (MaxLogBetaLog2 is < 0 or > 14)
      throw new InvalidDataException($"CTW: log-beta bound of 2^{MaxLogBetaLog2} outside 1..2^14");
  }
}

/// <summary>
/// The CTW 0.1 model: eight binary context trees (one per bit position) in one hashed node table,
/// pruned to unique contexts, weighted in the log domain, driving the log-domain arithmetic coder.
/// </summary>
internal sealed class CtwModel {
  private const uint EmptyTag = 0xFFFFFFFFu;
  private const int CountLimit = 256;
  private const int Accuracy = 128;
  private static readonly byte[] PrefixMask = [0x00, 0x80, 0xC0, 0xE0, 0xF0, 0xF8, 0xFC, 0xFE];

  private readonly CtwSettings _settings;
  private readonly int _depth;
  private readonly uint _nodeMask;
  private readonly int _maxLogBeta;
  private readonly int _bufferSize;

  private readonly uint[] _tags;
  private readonly Node[] _nodes;
  private readonly byte[] _buffer;
  private readonly uint[] _roots = new uint[8];

  // The context string: [0] the bits of the current byte so far, [1..] earlier bytes, latest first.
  private readonly byte[] _context;

  // The path found for the current bit: slots and copies of their records.
  private readonly uint[] _pathSlots;
  private readonly Node[] _path;
  private int _pathLength;

  // The two candidate updates. They live for the whole run: the root keeps whatever was last
  // computed at index 0, as the reference does.
  private readonly Node[] _ifZero;
  private readonly Node[] _ifOne;

  private int _coded;

  private struct Node {
    public byte Zeros;
    public byte Ones;
    public short LogBeta;
  }

  public CtwModel(CtwSettings settings, int length) {
    _settings = settings;
    _depth = settings.TreeDepth;
    _nodeMask = (1u << settings.NodeCountLog2) - 1;
    _maxLogBeta = 1 << settings.MaxLogBetaLog2;
    _bufferSize = (int)Math.Min(1L << settings.FileBufferLog2, length);
    _buffer = new byte[Math.Max(_bufferSize, _depth + 3)];
    _context = new byte[_depth + 3];
    _pathSlots = new uint[_depth + 2];
    _path = new Node[_depth + 2];
    _ifZero = new Node[_depth + 2];
    _ifOne = new Node[_depth + 2];
    _tags = new uint[1 << settings.NodeCountLog2];
    _nodes = new Node[1 << settings.NodeCountLog2];
  }

  /// <summary>Number of leading bytes coded at probability one half before the model starts.</summary>
  public int PrefixLength => _depth + 1;

  /// <summary>Records one of the leading bytes (they only fill the buffer).</summary>
  public void AddPrefixByte(int index, byte value) {
    if (index < _bufferSize)
      _buffer[index] = value;
  }

  /// <summary>Starts modelling after the leading bytes; <paramref name="last"/> is the last of them.</summary>
  public void Start(byte last) {
    Array.Fill(_tags, EmptyTag);
    for (var phase = 0; phase < 8; ++phase) {
      var slot = CtwTables.NodeOffsets[phase] & _nodeMask;
      _roots[phase] = slot;
      _tags[slot] = Tag((uint)(_depth + 2), 1, phase);
      _nodes[slot] = ((last >> (7 - phase)) & 1) == 0 ? new Node { Zeros = 1 } : new Node { Ones = 1 };
    }

    // The reference sets context position p to the byte at offset D + 2 - p: one place out of
    // step, with position 1 the not yet coded byte at offset D + 1, which reads as zero.
    for (var p = 1; p <= _depth + 1; ++p) {
      var offset = _depth + 2 - p;
      _context[p] = offset < _buffer.Length ? _buffer[offset] : (byte)0;
    }

    _coded = _depth + 1;
  }

  private bool Frozen => _coded >= _bufferSize;

  private static uint Tag(uint position, int probe, int phase) => (uint)(((probe - 1) << 3 | phase) << 24) | position;

  private static int TagPosition(uint tag) => (int)(tag & 0x00FFFFFF);

  /// <summary>Predicts bit <paramref name="phase"/> of the current byte, whose earlier bits are in <paramref name="partial"/>.</summary>
  public (int Step, int LikelyBit) Predict(int phase, byte partial) {
    _context[0] = (byte)(partial & PrefixMask[phase]);
    FindPath(phase);
    var (logProbability, likely) = Weigh();
    var step = -logProbability * (4096 / Accuracy);
    return (Math.Max(step, 3), likely);
  }

  /// <summary>Writes the update for the coded bit back to the path.</summary>
  public void Update(int bit) {
    var source = bit == 0 ? _ifZero : _ifOne;
    for (var i = _pathLength - 1; i >= 0; --i)
      _nodes[_pathSlots[i]] = source[i];
  }

  /// <summary>Finishes a byte: shifts the context and stores the byte in the buffer.</summary>
  public void EndByte(byte value) {
    for (var p = _depth + 1; p >= 1; --p)
      _context[p + 1] = _context[p];
    _context[1] = value;
    if (!Frozen)
      _buffer[_coded] = value;
    ++_coded;
  }

  private byte Symbol(int position, int depth, int phase)
    => depth == 1 ? (byte)(_buffer[position] & PrefixMask[phase]) : _buffer[position];

  // Searches the child of the node in slot `from` for the context symbol at depth `depth`.
  // Returns (0, slot) when found, (probe, slot) when an empty slot is free, (-1, 0) on failure.
  private (int Result, uint Slot) FindChild(int phase, uint from, int depth) {
    var symbol = _context[depth - 1];
    var step = (CtwTables.NodeOffsets[symbol] & _nodeMask) ^ (uint)((phase + 1) << 1);
    var slot = from;
    for (var probe = 1; probe <= _settings.Probes; ++probe) {
      slot = (slot + step) & _nodeMask;
      var tag = _tags[slot];
      if (tag == EmptyTag)
        return Frozen ? (-1, 0u) : (probe, slot);
      if ((tag >> 24) == (uint)((probe - 1) << 3 | phase) && Symbol(TagPosition(tag), depth, phase) == symbol)
        return (0, slot);
    }

    return (-1, 0u);
  }

  private void AddOldLeaf(int phase, uint parent, int depth, int position) {
    var (result, slot) = FindChild(phase, parent, depth);
    if (result <= 0)
      return;
    _tags[slot] = Tag((uint)position, result, phase);
    _nodes[slot] = _nodes[parent];
  }

  private void Record(int depth, uint slot) {
    _pathSlots[depth] = slot;
    _path[depth] = _nodes[slot];
  }

  private void FindPath(int phase) {
    var depth = 0;
    var current = _roots[phase];
    var cursor = _coded;

    for (;;) {
      Record(depth, current);
      if (depth == _depth + 1) {
        _pathLength = _depth + 2;
        return;
      }

      ++depth;
      --cursor;
      var (result, slot) = FindChild(phase, current, depth);
      if (result < 0) {
        _pathLength = depth;
        return;
      }

      if (result == 0) {
        current = slot;
        continue;
      }

      // The context below `current` was pruned: it stands for one earlier occurrence.
      for (;;) {
        var earlier = TagPosition(_tags[current]) - 1;

        if (_settings.StrictPruning && earlier != cursor + 1 && SameRemainingContext(earlier, depth, phase)) {
          _pathLength = depth;
          return;
        }

        _tags[slot] = Tag((uint)(cursor + 1), result, phase);
        _nodes[slot] = default;
        _pathSlots[depth] = slot;
        _path[depth] = default;

        var newSymbol = _context[depth - 1];
        var oldSymbol = Symbol(earlier, depth, phase);
        if (oldSymbol != newSymbol) {
          _context[depth - 1] = oldSymbol;
          AddOldLeaf(phase, current, depth, earlier);
          _context[depth - 1] = newSymbol;
          _pathLength = depth + 1;
          return;
        }

        // Same symbol: the new node continues the old context.
        _tags[slot] = Tag((uint)earlier, result, phase);
        _nodes[slot] = _nodes[current];
        current = slot;
        _path[depth] = _nodes[current];

        if (depth == _depth + 1) {
          _pathLength = _depth + 2;
          return;
        }

        ++depth;
        --cursor;
        (result, slot) = FindChild(phase, current, depth);
        if (result < 0) {
          var symbol = _context[depth - 1];
          var previous = TagPosition(_tags[current]) - 1;
          var old = _buffer[previous];
          if (old != symbol) {
            _context[depth - 1] = old;
            AddOldLeaf(phase, current, depth, previous);
            _context[depth - 1] = symbol;
          }

          _pathLength = depth;
          return;
        }

        if (result == 0) {
          // A fresh node cannot have a child; the reference reports an internal error and stops here.
          _pathLength = depth;
          return;
        }
      }
    }
  }

  private bool SameRemainingContext(int earlier, int depth, int phase) {
    for (var d = depth; d <= _depth + 1; ++d)
      if (Symbol(earlier + depth - d, d, phase) != _context[d - 1])
        return false;
    return true;
  }

  private static int Jacobian(int x) => -x < CtwTables.Jacobian.Length ? CtwTables.Jacobian[-x] : 0;

  private (int Zero, int One) Estimates(int zeros, int ones, bool leafQuotient) {
    if (_settings.ZeroRedundancy && (zeros == 0 || ones == 0)) {
      if (ones == 0)
        return (CtwTables.ZeroRedundancyMax[zeros], CtwTables.ZeroRedundancyMin[zeros]);
      return (CtwTables.ZeroRedundancyMin[ones], CtwTables.ZeroRedundancyMax[ones]);
    }

    var sum = leafQuotient ? 0 : CtwTables.Log2[zeros + ones + 1] + Accuracy;
    return (CtwTables.Log2[2 * zeros + 1] - sum, CtwTables.Log2[2 * ones + 1] - sum);
  }

  private static Node Counted(Node node, int bit) {
    if (bit == 0)
      return node.Zeros == CountLimit - 1
        ? new Node { Zeros = CountLimit / 2, Ones = (byte)((node.Ones + 1) / 2), LogBeta = node.LogBeta }
        : new Node { Zeros = (byte)(node.Zeros + 1), Ones = node.Ones, LogBeta = node.LogBeta };
    return node.Ones == CountLimit - 1
      ? new Node { Zeros = (byte)((node.Zeros + 1) / 2), Ones = CountLimit / 2, LogBeta = node.LogBeta }
      : new Node { Zeros = node.Zeros, Ones = (byte)(node.Ones + 1), LogBeta = node.LogBeta };
  }

  private short ClampBeta(int logBeta) => (short)Math.Clamp(logBeta, -_maxLogBeta, _maxLogBeta);

  // Log-domain weighting from the deepest node up; fills both candidate updates.
  private (int LogProbability, int Likely) Weigh() {
    var index = _pathLength - 1;
    var leaf = _path[index];
    var (leafZero, leafOne) = Estimates(leaf.Zeros, leaf.Ones, leafQuotient: true);
    var gamma = leafZero - leafOne;
    _ifZero[index] = Counted(leaf, 0);
    _ifOne[index] = Counted(leaf, 1);

    var lowest = _settings.RootWeighting ? 0 : 1;
    while (index-- > lowest) {
      var node = _path[index];
      int w0, w1;
      if (gamma >= 0) {
        w0 = -Jacobian(-gamma);
        w1 = w0 - gamma;
      } else {
        w1 = -Jacobian(gamma);
        w0 = w1 + gamma;
      }

      var (e0, e1) = Estimates(node.Zeros, node.Ones, leafQuotient: false);
      var u0 = node.LogBeta + e0;
      var u1 = node.LogBeta + e1;
      var d0 = u0 - w0;
      var numerator = d0 >= 0 ? u0 + Jacobian(-d0) : w0 + Jacobian(d0);
      var d1 = u1 - w1;
      var denominator = d1 >= 0 ? u1 + Jacobian(-d1) : w1 + Jacobian(d1);
      gamma = numerator - denominator;

      var zero = Counted(node, 0);
      zero.LogBeta = ClampBeta(u0 - w0);
      _ifZero[index] = zero;
      var one = Counted(node, 1);
      one.LogBeta = ClampBeta(u1 - w1);
      _ifOne[index] = one;
    }

    return gamma >= 0 ? (-Jacobian(-gamma), 0) : (-Jacobian(gamma), 1);
  }
}
