namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a mode.
/// </summary>
public struct Mode
{
    /// <summary>
    /// Provides the block flag value.
    /// </summary>
public int BlockFlag;
    /// <summary>
    /// Provides the window type value.
    /// </summary>
public int WindowType;
    /// <summary>
    /// Provides the transform type value.
    /// </summary>
public int TransformType;
    /// <summary>
    /// Provides the mapping value.
    /// </summary>
public int Mapping;

    /// <summary>
    /// Initializes a new instance of <see cref="Mode"/>.
    /// </summary>
public Mode(
        int blockFlag,
        int windowType,
        int transformType,
        int mapping)
    {
        BlockFlag = blockFlag;
        WindowType = windowType;
        TransformType = transformType;
        Mapping = mapping;
    }
}
