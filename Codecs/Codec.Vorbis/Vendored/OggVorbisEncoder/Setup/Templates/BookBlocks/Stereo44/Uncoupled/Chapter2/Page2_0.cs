namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter2;

/// <summary>
/// Represents a page 2 0.
/// </summary>
public class Page2_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 4;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         2, 5, 5, 5, 6, 6, 5, 6, 6, 5, 6, 6, 7, 8, 8, 6,
         8, 8, 5, 6, 6, 6, 8, 7, 7, 8, 8, 5, 6, 6, 7, 8,
         8, 6, 8, 8, 6, 8, 8, 8, 9,10, 8,10,10, 6, 8, 8,
         7,10, 8, 8,10,10, 5, 6, 6, 6, 8, 8, 7, 8, 8, 6,
         8, 8, 8,10,10, 8, 8,10, 6, 8, 8, 8,10,10, 8,10,
         9,
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
