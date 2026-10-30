namespace FileFormat.SevenZip;

/// <summary>
/// A folder (solid block) in a 7z archive: a graph of coders and the bind pairs connecting
/// them. Its packed-side inputs that no bind pair feeds are read from packed streams, in the
/// order <see cref="PackedStreams"/> lists them.
/// </summary>
internal sealed class SevenZipFolder {
  /// <summary>Gets the list of coders in this folder.</summary>
  public List<SevenZipCoder> Coders { get; } = [];

  /// <summary>
  /// Gets the list of bind pairs connecting coder streams: <c>InIndex</c> is a coder input
  /// (counted over all coders' inputs), <c>OutIndex</c> the coder output that feeds it (each
  /// coder has one output, so this is a coder index).
  /// </summary>
  public List<(int InIndex, int OutIndex)> BindPairs { get; } = [];

  /// <summary>
  /// The coder inputs that read packed streams, in packed-stream order; empty means "the one
  /// unbound input", which is how a folder with a single packed stream is written.
  /// </summary>
  public int[] PackedStreams { get; set; } = [];

  /// <summary>Gets or sets the unpack sizes for each coder output stream (one per coder).</summary>
  public long[] UnpackSizes { get; set; } = [];

  /// <summary>Gets or sets the CRC-32 of the final uncompressed data.</summary>
  public uint? UnpackCrc { get; set; }

  /// <summary>Total number of coder inputs.</summary>
  public int NumInStreams => this.Coders.Sum(static c => c.NumInStreams);

  /// <summary>Number of packed streams the folder reads.</summary>
  public int NumPackStreams => this.NumInStreams - this.BindPairs.Count;

  /// <summary>The coder whose output no bind pair consumes: the folder's output.</summary>
  public int MainCoderIndex {
    get {
      for (var i = 0; i < this.Coders.Count; ++i)
        if (!this.BindPairs.Exists(b => b.OutIndex == i))
          return i;
      throw new InvalidDataException("7z: every coder output of a folder is bound; the folder has no output.");
    }
  }

  /// <summary>The size of the folder's output.</summary>
  public long MainUnpackSize => this.UnpackSizes.Length > 0 ? this.UnpackSizes[this.MainCoderIndex] : 0;

  /// <summary>Whether a coder of this folder is the 7z AES coder.</summary>
  public bool IsEncrypted => this.Coders.Exists(static c => c.Is(SevenZipConstants.CodecAes));

  /// <summary>
  /// Fills in the implied packed-stream listing and checks the graph is one 7-Zip can decode:
  /// single-output coders, every bind pair in range, every input fed exactly once, no cycles.
  /// </summary>
  public void Validate() {
    if (this.Coders.Count == 0)
      throw new InvalidDataException("7z: a folder without coders.");
    if (this.Coders.Exists(static c => c.NumOutStreams != 1 || c.NumInStreams < 1 || c.NumInStreams > 64))
      throw new NotSupportedException("7z: coders with several outputs (the pre-9.x coder model) are not supported.");
    var numIn = this.NumInStreams;
    if (this.BindPairs.Count != this.Coders.Count - 1)
      throw new InvalidDataException("7z: a folder needs one bind pair per coder but one.");

    var fed = new bool[numIn];
    foreach (var (inIndex, outIndex) in this.BindPairs) {
      if (inIndex < 0 || inIndex >= numIn || outIndex < 0 || outIndex >= this.Coders.Count || fed[inIndex])
        throw new InvalidDataException("7z: a folder's bind pairs are out of range.");
      fed[inIndex] = true;
    }

    if (this.PackedStreams.Length == 0 && this.NumPackStreams == 1)
      this.PackedStreams = [Array.IndexOf(fed, false)];
    if (this.PackedStreams.Length != this.NumPackStreams)
      throw new InvalidDataException("7z: a folder's packed-stream listing does not match its coders.");
    foreach (var p in this.PackedStreams) {
      if (p < 0 || p >= numIn || fed[p])
        throw new InvalidDataException("7z: a folder's packed streams are out of range.");
      fed[p] = true;
    }
    if (Array.IndexOf(fed, false) >= 0)
      throw new InvalidDataException("7z: a folder leaves a coder input unfed.");
    if (this.UnpackSizes.Length != this.Coders.Count)
      throw new InvalidDataException("7z: a folder's unpack sizes do not match its coders.");

    // Every coder must be reachable from the output without passing itself again.
    var main = this.MainCoderIndex;
    var visiting = new bool[this.Coders.Count];
    var seen = 0;
    void Walk(int coder) {
      if (visiting[coder])
        throw new InvalidDataException("7z: a folder's coder graph has a cycle.");
      visiting[coder] = true;
      ++seen;
      var inBase = 0;
      for (var i = 0; i < coder; ++i)
        inBase += this.Coders[i].NumInStreams;
      for (var j = 0; j < this.Coders[coder].NumInStreams; ++j)
        foreach (var (inIndex, outIndex) in this.BindPairs)
          if (inIndex == inBase + j)
            Walk(outIndex);
    }
    Walk(main);
    if (seen != this.Coders.Count)
      throw new InvalidDataException("7z: a folder holds coders its output does not use.");
  }
}

/// <summary>One coder of a 7z folder.</summary>
internal sealed class SevenZipCoder {
  /// <summary>Gets or sets the codec identifier bytes.</summary>
  public byte[] CodecId { get; set; } = [];

  /// <summary>Gets or sets the number of input streams.</summary>
  public int NumInStreams { get; set; } = 1;

  /// <summary>Gets or sets the number of output streams.</summary>
  public int NumOutStreams { get; set; } = 1;

  /// <summary>Gets or sets the coder properties (codec-specific configuration).</summary>
  public byte[]? Properties { get; set; }

  /// <summary>Whether this coder is the method <paramref name="codecId"/>.</summary>
  public bool Is(ReadOnlySpan<byte> codecId) => this.CodecId.AsSpan().SequenceEqual(codecId);
}

/// <summary>Where the packed streams lie and how long they are.</summary>
internal sealed class SevenZipPackInfo {
  /// <summary>Gets or sets the position of packed data relative to the end of the signature header.</summary>
  public long PackPos { get; set; }

  /// <summary>Gets or sets the packed sizes for each pack stream.</summary>
  public long[] PackSizes { get; set; } = [];

  /// <summary>Gets or sets optional CRC-32 values for each pack stream.</summary>
  public uint?[] PackCrcs { get; set; } = [];
}

/// <summary>How each folder's output divides into files.</summary>
internal sealed class SevenZipSubStreamsInfo {
  /// <summary>Gets or sets the number of unpack streams (files) per folder.</summary>
  public int[] NumUnpackStreams { get; set; } = [];

  /// <summary>Gets or sets the unpack size for each individual file within a folder.</summary>
  public long[] UnpackSizes { get; set; } = [];

  /// <summary>
  /// Gets or sets the CRC-32 of each sub-stream, aligned with <see cref="UnpackSizes"/>;
  /// <see langword="null"/> where the archive records none.
  /// </summary>
  public uint?[] Digests { get; set; } = [];
}

/// <summary>One entry of the FilesInfo list.</summary>
internal sealed class SevenZipFileInfo {
  /// <summary>Gets or sets the file name.</summary>
  public string Name { get; set; } = "";

  /// <summary>Gets or sets whether this entry is a directory.</summary>
  public bool IsDirectory { get; set; }

  /// <summary>Gets or sets whether this entry is an empty stream (no data).</summary>
  public bool IsEmptyStream { get; set; }

  /// <summary>Gets or sets whether this entry is an empty file (vs. directory).</summary>
  public bool IsEmptyFile { get; set; }

  /// <summary>Gets or sets whether this entry is an anti-item (a deletion marker).</summary>
  public bool IsAnti { get; set; }

  /// <summary>Gets or sets the file creation time.</summary>
  public DateTime? CreationTime { get; set; }

  /// <summary>Gets or sets the last access time.</summary>
  public DateTime? LastAccessTime { get; set; }

  /// <summary>Gets or sets the last write time.</summary>
  public DateTime? LastWriteTime { get; set; }

  /// <summary>Gets or sets the Windows file attributes.</summary>
  public uint? Attributes { get; set; }

  /// <summary>Gets or sets the <c>kStartPos</c> value (offset of this file inside its original), if recorded.</summary>
  public ulong? StartPos { get; set; }

  /// <summary>
  /// The header properties this reader skips — FilesInfo properties it does not interpret, and
  /// top-level ones such as archive properties — named for a refusal message; shared by every
  /// file of one header. Padding (<c>kDummy</c>) is not listed.
  /// </summary>
  public IReadOnlySet<string> SkippedProperties { get; set; } = new HashSet<string>();
}
