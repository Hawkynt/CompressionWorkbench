using System.Collections.Generic;
using OggVorbisEncoder.Setup;

namespace OggVorbisEncoder;

/// <summary>
/// Represents a codec setup.
/// </summary>
public class CodecSetup
{
    /// <summary>
    /// Initializes a new instance of <see cref="CodecSetup"/>.
    /// </summary>
public CodecSetup(EncodeSetup encodeSetup)
    {
        EncodeSetup = encodeSetup;
    }

    /// <summary>
    /// Gets the encode setup.
    /// </summary>
public EncodeSetup EncodeSetup { get; }

    /// <summary>
    /// Gets the block sizes.
    /// </summary>
public int[] BlockSizes { get; } = new int[2];

    /// <summary>
    /// Gets or sets the full books.
    /// </summary>
public CodeBook[] FullBooks { get; set; }
    /// <summary>
    /// Gets the book params.
    /// </summary>
public IList<IStaticCodeBook> BookParams { get; } = new List<IStaticCodeBook>();
    /// <summary>
    /// Gets the mode params.
    /// </summary>
public IList<Mode> ModeParams { get; } = new List<Mode>();
    /// <summary>
    /// Gets the map params.
    /// </summary>
public IList<Mapping> MapParams { get; } = new List<Mapping>();
    /// <summary>
    /// Gets the floor params.
    /// </summary>
public IList<Floor> FloorParams { get; } = new List<Floor>();
    /// <summary>
    /// Gets the residue params.
    /// </summary>
public IList<ResidueEntry> ResidueParams { get; } = new List<ResidueEntry>();
    /// <summary>
    /// Gets the psy params.
    /// </summary>
public IList<PsyInfo> PsyParams { get; } = new List<PsyInfo>();
    /// <summary>
    /// Gets or sets the psy global param.
    /// </summary>
public PsyGlobal PsyGlobalParam { get; set; }
}
