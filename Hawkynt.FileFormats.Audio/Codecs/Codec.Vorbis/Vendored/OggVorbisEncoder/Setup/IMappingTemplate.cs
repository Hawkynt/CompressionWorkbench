namespace OggVorbisEncoder.Setup;

/// <summary>
/// Defines the contract for i mapping template.
/// </summary>
public interface IMappingTemplate
{
    Mapping[] Mapping { get; }
    IResidueTemplate[] ResidueTemplate { get; }
}
