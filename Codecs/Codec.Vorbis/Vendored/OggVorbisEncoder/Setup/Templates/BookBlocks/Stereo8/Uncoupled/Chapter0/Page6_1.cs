namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo8.Uncoupled.Chapter0;

/// <summary>
/// Represents a page 6 1.
/// </summary>
public class Page6_1 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 6, 6, 4, 6, 5, 7, 7, 4, 5, 6, 7, 7, 6,
         7, 7, 7, 7, 6, 7, 7, 7, 7,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -533725184;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1611661312;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 3;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        2,
        1,
        3,
        0,
        4,
    };
}
