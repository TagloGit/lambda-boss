namespace LambdaBoss;

/// <summary>
///     Configuration for a local filesystem directory source of LAMBDA libraries.
/// </summary>
public sealed class LocalSourceConfig
{
    /// <summary>
    ///     The absolute path to the directory containing library sub-folders
    ///     (e.g. "C:\Users\trjac\repositories\lambda-boss\lambdas").
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    ///     Whether this local source is enabled for loading.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Returns the folder name from the path, used as the display name.
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(Path)
        ? ""
        : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}
