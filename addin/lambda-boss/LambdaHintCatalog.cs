using System.Text;
using System.Xml;
using System.Xml.Serialization;

using ExcelDna.IntelliSense;

namespace LambdaBoss;

/// <summary>
///     The names the argument hints know (spec 0013): each library LAMBDA's header metadata,
///     under its bare name, its library's default prefix and any prefix it was loaded with.
///     Pure and thread-safe; <see cref="LambdaHints"/> writes it out for ExcelDna.IntelliSense.
/// </summary>
internal sealed class LambdaHintCatalog
{
    private readonly object _gate = new();

    // Bare name -> metadata.
    private readonly Dictionary<string, LambdaDoc> _docs = new(StringComparer.OrdinalIgnoreCase);

    // Name as typed in a cell (bare or prefixed) -> bare name.
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Records every documented LAMBDA the popup knows, under its bare name and its
    ///     library's default prefix. Constants and files without metadata are skipped.
    /// </summary>
    public void AddLibraryLambdas(IEnumerable<LambdaInfo> lambdas)
    {
        lock (_gate)
        {
            foreach (var lambda in lambdas)
            {
                if (lambda.IsConstant || lambda.Doc is null)
                    continue;

                var bare = lambda.Doc.Name;
                _docs[bare] = lambda.Doc;
                _aliases[bare] = bare;

                var prefix = lambda.LibraryInfo?.DefaultPrefix;
                if (!string.IsNullOrEmpty(prefix))
                    _aliases[$"{prefix}.{bare}"] = bare;
            }
        }
    }

    /// <summary>
    ///     Records the names a library was just loaded under (for a prefix the user chose).
    ///     Returns true when anything new was added.
    /// </summary>
    public bool AddLoadedNames(string? prefix, IEnumerable<string> loadedNames)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var name in loadedNames)
            {
                var bare = !string.IsNullOrEmpty(prefix)
                           && name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
                    ? name[(prefix!.Length + 1)..]
                    : name;

                if (!_docs.ContainsKey(bare) || _aliases.ContainsKey(name))
                    continue;

                _aliases[name] = bare;
                changed = true;
            }
        }

        return changed;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _aliases.Count;
        }
    }

    /// <summary>
    ///     One ExcelDna.IntelliSense entry per known name, ordered by name.
    /// </summary>
    public IReadOnlyList<FunctionInfo> BuildFunctionInfos()
    {
        lock (_gate)
        {
            return _aliases
                .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                .Select(a => ToFunctionInfo(a.Key, _docs[a.Value]))
                .ToList();
        }
    }

    /// <summary>
    ///     The <c>.intellisense.xml</c> text ExcelDna.IntelliSense reads beside the XLL.
    /// </summary>
    public string BuildXml() => ToXml(BuildFunctionInfos());

    internal static FunctionInfo ToFunctionInfo(string typedName, LambdaDoc doc)
    {
        var description = new StringBuilder();
        if (doc.Returns.Length > 0)
            description.Append("Returns: ").Append(doc.Returns);
        if (doc.Summary.Length > 0)
        {
            if (description.Length > 0)
                description.Append('\n');
            description.Append(doc.Summary);
        }

        return new FunctionInfo
        {
            Name = typedName,
            Description = description.ToString(),
            HelpTopic = "",
            ArgumentList = doc.Parameters
                .Select(p => new FunctionInfo.ArgumentInfo
                {
                    Name = p.Required ? p.Name : $"[{p.Name}]",
                    Description = p.Description
                })
                .ToList()
        };
    }

    internal static string ToXml(IEnumerable<FunctionInfo> functions)
    {
        var document = new XmlIntelliSense
        {
            XmlFunctionInfo = new XmlFunctionInfo { FunctionsList = functions.ToList() }
        };

        var serializer = new XmlSerializer(typeof(XmlIntelliSense));
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            serializer.Serialize(writer, document);
        }

        return new UTF8Encoding(false).GetString(stream.ToArray());
    }
}
