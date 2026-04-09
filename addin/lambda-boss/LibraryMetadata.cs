using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LambdaBoss;

/// <summary>
///     Represents the metadata from a library's _library.yaml file.
/// </summary>
public sealed class LibraryMetadata
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "default_prefix")]
    public string DefaultPrefix { get; set; } = "";

    /// <summary>
    ///     Deserializes a _library.yaml file from the given path.
    /// </summary>
    public static LibraryMetadata LoadFromFile(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        return LoadFromString(yaml);
    }

    /// <summary>
    ///     Deserializes a _library.yaml from a YAML string.
    /// </summary>
    internal static LibraryMetadata LoadFromString(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        return deserializer.Deserialize<LibraryMetadata>(yaml);
    }
}
