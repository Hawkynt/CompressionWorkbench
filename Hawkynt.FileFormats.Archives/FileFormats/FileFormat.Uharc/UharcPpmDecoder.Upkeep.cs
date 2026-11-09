namespace FileFormat.Uharc;

/// <summary>
/// PPM bookkeeping that every symbol runs through (docs/UHARC-ON-DISK.md, "PPM", Upkeep): the
/// last position of each byte, a stride detector for record-structured data, two marker
/// counters for interleaved 16-bit text, and the rank tables the stride drives.
/// </summary>
internal sealed partial class UharcPpmDecoder {

  private readonly uint[] _contextSlots = new uint[4];
  private readonly byte[] _contextBytes = new byte[0x40000];
  private readonly uint[] _lastSeenAt = new uint[256];
  private readonly uint[] _recentAt = new uint[256];
  private readonly uint[] _seenBefore = new uint[256];
  private readonly uint[] _distanceHits = new uint[256];
  private uint _strideMisses;
  private uint _strideCandidate;
  private uint _stride = 1;
  private uint _strideLast = 1;
  private readonly byte[] _markers = new byte[4];
  private int _markerState;

  private readonly uint[] _rankSmall = new uint[0x109];
  private readonly uint[] _rankLarge = new uint[0x187];
  private readonly uint[] _rankSmallBase = new uint[5];
  private readonly uint[] _rankLargeBase = new uint[17];
  private uint _rankSmallPointer;
  private uint _rankLargePointer;
  private uint _rankStep;
  private bool _rankBoost;

  private void InitializeRanks() {
    for (var i = 2; i <= 16; ++i)
      this._rankLargeBase[i] = (uint)(255 + i * (i - 1) / 2);
    for (var i = 0; i <= 4; ++i)
      this._rankSmallBase[i] = this._rankLargeBase[i];
  }

  /// <summary>Four hashed context slots that remember which byte followed them last.</summary>
  private void ComputeRecentSlots() {
    var c0 = this._roll0;
    var c1 = this._roll1;
    this._contextSlots[0] = ((c0 >> 16) & 0xFF00) + (c1 >> 24);
    this._contextSlots[1] = ((c0 >> 8) & 0xFFFF) + 0x10000;
    this._contextSlots[2] = ((c0 >> 8) & 0xFF00) + 0x20000 + (c0 & 0xFF);
    this._contextSlots[3] = ((c0 << 8) & 0xFF00) + 0x30000 + (c1 & 0xFF);
  }

  private void StoreRecentContexts(byte value) {
    foreach (var slot in this._contextSlots)
      this._contextBytes[slot] = value;
  }

  private void MarkRecentPredictions() {
    foreach (var slot in this._contextSlots)
      this._lastSeenAt[this._contextBytes[slot]] = this._symbols;
  }

  /// <summary>Counts runs of high bytes on even and odd positions to detect UTF-16-like text.</summary>
  private void TrackMarkers(byte value) {
    var lane = (int)(this._symbols & 1);
    var other = 1 - lane;
    this._markers[lane] = (byte)((this._markers[lane] * 2) & 0xF);
    this._markers[2 + lane] = (byte)((this._markers[2 + lane] * 2) & 0xF);
    this._markerState = 0;
    if (value < 0x80)
      return;

    if (value == 0xFF) {
      ++this._markers[2 + lane];
      if (this._markers[other] == 0xF && this._markers[other] == this._markers[2 + lane])
        this._markerState = 2;
      return;
    }

    if (value is <= 0xDF or (>= 0xF2 and <= 0xF6)) {
      var count = ++this._markers[lane];
      if (this._markers[2 + other] == 0xF && count == this._markers[2 + other])
        this._markerState = 1;
    }
  }

  /// <summary>Learns the distance at which bytes repeat (the stride) from where each byte was last seen.</summary>
  private void TrackRepetition(byte value) {
    var position = this._position;
    if (value is not (0 or 0xFF)) {
      var distance = (position - this._recentAt[value]) & this._mask;
      if (distance >= 0x100) {
        distance = (position - this._recentAt[(value - 1) & 0xFF]) & this._mask;
        if (distance >= 0x100)
          distance = (position - this._recentAt[(value + 1) & 0xFF]) & this._mask;
      }

      if (distance != 0 && distance < 0x100) {
        ++this._distanceHits[distance];
        var stride = this._stride;
        if (distance != stride) {
          if (value == this._window[(position - stride) & this._mask])
            ++this._distanceHits[stride];
          else
            this._strideMisses += 2;

          if (distance == this._strideCandidate) {
            if (this._distanceHits[this._strideCandidate] >= this._distanceHits[this._stride]) {
              this._strideCandidate = this._stride;
              this._stride = distance;
              if (distance > 1)
                this._strideLast = distance;
            }
          } else
            this._strideCandidate = distance;
        }
      }

      if (++this._strideMisses >= 0x28) {
        for (var i = 0; i < 256; ++i)
          this._distanceHits[i] >>= 1;
        ++this._distanceHits[this._stride];
        this._strideMisses = 0;
        if (this._stride == 1 && this._distanceHits[1] < 4)
          this._distanceHits[1] += 1;
      }

      this._recentAt[value] = position;
    }

    if (this._kind != 0)
      this._seenBefore[value] = position + 0x40;
  }

  /// <summary>Whether <paramref name="symbol"/> occurred within the last 64 window positions (binary streams record this).</summary>
  private bool Seen(byte symbol) => this._position <= this._seenBefore[symbol];

  private void PointRanksAtStride() {
    if (this._stride <= 1 && (this._strideLast & 0xFF) is < 2 or > 4)
      return;

    var k = this._strideLast;
    if (k == 0)
      return;

    this._rankSmallPointer = k;
    this._rankLargePointer = k;
    if (k <= 4)
      this._rankSmallPointer = this._rankSmallBase[k] + this._symbols % k;
    if (k <= 16)
      this._rankLargePointer = this._rankLargeBase[k] + this._symbols % k;
  }

  private bool StrideGate() =>
    this._kind != 0 && this._stride > 1 && this._rankSmallPointer < this._rankSmall.Length && this._rankSmall[this._rankSmallPointer] != 0;

  private void CountPrediction(int row, byte value) {
    if (value == this._predicted[row]) {
      if (this._confidence[row] < 0xF)
        ++this._confidence[row];
    } else {
      this._predicted[row] = value;
      this._confidence[row] = 0;
    }
  }
}
