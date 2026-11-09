using ExcelDna.Integration;
using ExcelDna.IntelliSense;

using LambdaBoss.Commands;
using LambdaBoss.Common;

namespace LambdaBoss;

/// <summary>
///     Argument hints for library LAMBDAs (spec 0013), on unless <see cref="Settings.ArgumentHints"/>
///     is turned off. ExcelDna.IntelliSense draws the tooltips; it reads <c>&lt;xll&gt;.intellisense.xml</c>
///     beside every loaded Excel-DNA add-in, so this class keeps that file in step with
///     <see cref="Catalog"/> and asks the server to refresh. Nothing here runs while the user types.
/// </summary>
internal static class LambdaHints
{
    private static readonly object Gate = new();
    private static bool _installed;
    private static string? _lastWritten;

    internal static LambdaHintCatalog Catalog { get; } = new();

    private static bool Enabled => Settings.Current.ArgumentHints;

    /// <summary>
    ///     Starts the hints if Settings.ArgumentHints is on. Must run in a macro context (AutoOpen or QueueAsMacro).
    /// </summary>
    public static void StartIfEnabled()
    {
        if (!Enabled || _installed)
            return;

        try
        {
            // The file must exist before Install: the server registers each XLL's file only if
            // it's there when it first looks. Last session's file is kept until the catalogue
            // fills, so hints work from the first keystroke.
            if (!File.Exists(XmlPath) && !WriteFile(force: true))
                return;

            IntelliSenseServer.Install();
            _installed = true;
            Logger.Info($"LambdaHints: started ({Catalog.Count} names), file {XmlPath}");

            // Fill the catalogue in the background, the way the popup would (disk cache, else GitHub).
            // LibraryProvider.RefreshAsync hands its lambdas to OnLibrariesRefreshed.
            Task.Run(async () =>
            {
                try
                {
                    await ShowLambdaPopupCommand.CreateProvider().GetAllLambdasAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error("LambdaHints: background library load", ex);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("LambdaHints.Start", ex);
        }
    }

    /// <summary>
    ///     Stops the hints. Must run in a macro context. With <paramref name="removeFile"/>
    ///     the XML file goes too (hints turned off); on AutoClose it stays for the next session.
    /// </summary>
    public static void Stop(bool removeFile)
    {
        try
        {
            if (_installed)
            {
                IntelliSenseServer.Uninstall();
                _installed = false;
                Logger.Info("LambdaHints: stopped");
            }

            if (removeFile && File.Exists(XmlPath))
            {
                File.Delete(XmlPath);
                _lastWritten = null;

                // Another add-in's IntelliSense server may still hold our names: let it reload.
                IntelliSenseServer.Refresh();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("LambdaHints.Stop", ex);
        }
    }

    /// <summary>
    ///     Applies Settings.ArgumentHints after the settings window saves. Safe from any thread.
    /// </summary>
    public static void ApplySwitch()
    {
        ExcelAsyncUtil.QueueAsMacro(() =>
        {
            if (Enabled)
                StartIfEnabled();
            else if (_installed || File.Exists(XmlPath))
                Stop(removeFile: true);
        });
    }

    /// <summary>
    ///     Called by <see cref="LibraryProvider"/> after every library refresh. Safe from any thread.
    /// </summary>
    public static void OnLibrariesRefreshed(IEnumerable<LambdaInfo> lambdas)
    {
        Catalog.AddLibraryLambdas(lambdas);
        Publish();
    }

    /// <summary>
    ///     Called after a library is injected, with the names it was injected under. Safe from any thread.
    /// </summary>
    public static void OnLibraryLoaded(string? prefix, IEnumerable<string> loadedNames)
    {
        if (Catalog.AddLoadedNames(prefix, loadedNames))
            Publish();
    }

    private static void Publish()
    {
        // An empty catalogue (offline, nothing cached) never replaces last session's file.
        if (!_installed || Catalog.Count == 0)
            return;

        if (WriteFile(force: false))
            ExcelAsyncUtil.QueueAsMacro(() =>
            {
                try
                {
                    if (_installed)
                        IntelliSenseServer.Refresh();
                }
                catch (Exception ex)
                {
                    Logger.Error("LambdaHints: refresh", ex);
                }
            });
    }

    private static string XmlPath => Path.ChangeExtension(ExcelDnaUtil.XllPath, ".intellisense.xml");

    // Writes the catalogue to the file. True when written; false on failure, or (without
    // force) when the text is the same as last written.
    private static bool WriteFile(bool force)
    {
        lock (Gate)
        {
            try
            {
                var xml = Catalog.BuildXml();
                if (!force && xml == _lastWritten)
                    return false;

                File.WriteAllText(XmlPath, xml);
                _lastWritten = xml;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"LambdaHints: can't write {XmlPath}; hints stay off", ex);
                return false;
            }
        }
    }
}
