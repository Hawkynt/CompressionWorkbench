namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter7;

/// <summary>
/// Represents a page 1 0.
/// </summary>
public class Page1_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 4;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 4, 7, 7, 5, 7, 7, 5, 8, 8, 8,10,10, 7,
        10,10, 5, 8, 8, 7,10,10, 8,10,10, 5, 8, 8, 8,11,
        10, 8,10,10, 8,10,10,10,12,13,10,13,13, 7,10,10,
        10,13,12,10,13,13, 5, 8, 8, 8,11,10, 8,10,11, 7,
        10,10,10,13,13,10,12,13, 8,11,11,10,13,13,10,13,
        12,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -535822336;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1611661312;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 2;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        1,
        0,
        2,
    };
}
