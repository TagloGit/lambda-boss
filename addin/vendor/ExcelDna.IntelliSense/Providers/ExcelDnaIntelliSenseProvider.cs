using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ExcelDna.Integration;

namespace ExcelDna.IntelliSense
{
    // Provides IntelliSense info for all Excel-DNA based .xll add-ins, using the built-in RegistrationInfo helper function.
    class ExcelDnaIntelliSenseProvider : IIntelliSenseProvider
    {
        class XllRegistrationInfo
        {
            readonly string _xllPath;
            bool _regInfoNotAvailable = false;  // Set to true if we know for sure that reginfo is #N/A
            double _version = -1;               // Version indicator to enumerate from scratch
            object[,] _regInfo = null;          // Default value

            public XllRegistrationInfo(string xllPath)
            {
                _xllPath = xllPath;
            }

            // Called on the main thread in a macro context
            public void Refresh()
            {
                if (_regInfoNotAvailable)
                    return;

                object regInfoResponse = ExcelIntegration.GetRegistrationInfo(_xllPath, _version);

                if (regInfoResponse.Equals(ExcelError.ExcelErrorNA))
                {
                    _regInfoNotAvailable = true;
                    Logger.Provider.Info($"XllRegistrationInfo not available for {_xllPath}");
                    return;
                }

                if (regInfoResponse == null || regInfoResponse.Equals(ExcelError.ExcelErrorNum))
                {
                    // no update - versions match
                    return;
                }

                _regInfo = regInfoResponse as object[,];
                if (_regInfo != null)
                {
                    Debug.Assert((string)_regInfo[0, 0] == _xllPath);
                    _version = (double)_regInfo[0, 1];
                }
            }

            // Not in macro context - don't call Excel, could be any thread.
            public IEnumerable<FunctionInfo> GetFunctionInfos()
            {
                // to avoid worries about locking and this being updated from another thread, we take a copy of _regInfo
                var regInfo = _regInfo;
                /*
                    result[0, 0] = XlAddIn.PathXll;
                    result[0, 1] = registrationInfoVersion;
                 */

                if (regInfo == null)
                    yield break;

                int regInfoCount = regInfo.GetLength(0);
                Logger.Provider.Verbose($"XllRegistrationInfo for {_xllPath}: {regInfoCount} registrations");
                for (int i = 0; i < regInfoCount; i++)
                {
                    if (regInfo[i, 0] is ExcelEmpty)
                    {
                        string functionName = regInfo[i, 3] as string;
                        string helpTopic = regInfo[i, 8] as string;
                        string description = regInfo[i, 9] as string;

                        string argumentStr = regInfo[i, 4] as string;
                        string[] argumentNames = string.IsNullOrEmpty(argumentStr) ? new string[0] : argumentStr.Split(',');

                        List<FunctionInfo.ArgumentInfo> argumentInfos = new List<FunctionInfo.ArgumentInfo>();
                        for (int j = 0; j < argumentNames.Length; j++)
                        {
                            argumentInfos.Add(new FunctionInfo.ArgumentInfo 
                            { 
                                Name = argumentNames[j], 
                                Description = regInfo[i, j + 10] as string 
                            });
                        }

                        yield return new FunctionInfo
                        {
                            Name = functionName,
                            Description = description,
                            HelpTopic = helpTopic,
                            ArgumentList = argumentInfos,
                            SourcePath = _xllPath
                        };
                    }
                }
            }
        }

        readonly SynchronizationContext _syncContextMain; // Main thread, not macro context
        readonly ExcelSynchronizationContext _syncContextExcel; // Proper macro context
        readonly XmlIntelliSenseProvider _xmlProvider;
        readonly Dictionary<string, XllRegistrationInfo> _xllRegistrationInfos = new Dictionary<string, XllRegistrationInfo>();
        public event EventHandler Invalidate;

        // LAMBDA BOSS PATCH (lambda-boss#433): upstream registered LdrRegisterDllNotification here, which runs
        // managed code under the loader lock on whatever thread loads a DLL, including Office's own threads
        // (React Native / hermes.dll building the account flyout). Excel crashed in hermes.dll when that flyout
        // was first built with the server running. XLLs loaded after Initialize are now picked up by Refresh
        // (AddLoadedXlls), which runs on the main thread in a macro context.
        public ExcelDnaIntelliSenseProvider(SynchronizationContext syncContextMain)
        {
            _syncContextMain = syncContextMain;
            _syncContextExcel = new ExcelSynchronizationContext();
            _xmlProvider = new XmlIntelliSenseProvider();
            _xmlProvider.Invalidate += (sender, e) => OnInvalidate(null);
        }

        #region IIntelliSenseProvider implementation

        // Must be called on the main Excel thread
        public void Initialize()
        {
            Debug.Assert(Thread.CurrentThread.ManagedThreadId == 1);
            Logger.Provider.Info("ExcelDnaIntelliSenseProvider.Initialize");

            _xmlProvider.Initialize();

            lock (_xllRegistrationInfos)
            {
                Logger.Provider.Info("ExcelDnaIntelliSenseProvider.Initialize - inside lock");
                foreach (var regInfo in AddLoadedXlls())
                {
                    regInfo.Refresh();
                }
            }
            Logger.Provider.Info("ExcelDnaIntelliSenseProvider.Initialize - after lock");
        }

        // Must be called on the main Excel thread
        public void Refresh()
        {
            Debug.Assert(Thread.CurrentThread.ManagedThreadId == 1);
            Logger.Provider.Info("ExcelDnaIntelliSenseProvider.Refresh");
            lock (_xllRegistrationInfos)
            {
                Logger.Provider.Info("ExcelDnaIntelliSenseProvider.Refresh - inside lock");
                AddLoadedXlls(); // LAMBDA BOSS PATCH (#433): replaces the loader notification
                foreach (var regInfo in _xllRegistrationInfos.Values)
                {
                    regInfo.Refresh();
                }
                _xmlProvider.Refresh();
            }
            Logger.Provider.Info("ExcelDnaIntelliSenseProvider.Refresh - after lock");
        }

        // LAMBDA BOSS PATCH (#433): registers every loaded .xll not yet known (and its .intellisense.xml),
        // returning the new ones, and forgets any that have unloaded. Call inside lock (_xllRegistrationInfos),
        // on the main thread.
        List<XllRegistrationInfo> AddLoadedXlls()
        {
            var added = new List<XllRegistrationInfo>();
            var loaded = new HashSet<string>(GetLoadedXllPaths());

            foreach (var xllPath in _xllRegistrationInfos.Keys.Where(p => !loaded.Contains(p)).ToList())
            {
                _xllRegistrationInfos.Remove(xllPath);
                _xmlProvider.UnregisterXmlFunctionInfo(GetXmlPath(xllPath));
            }

            foreach (var xllPath in loaded)
            {
                if (!_xllRegistrationInfos.ContainsKey(xllPath))
                {
                    Logger.Provider.Verbose($"ExcelDnaIntelliSenseProvider: Adding XllRegistrationInfo for {xllPath}");
                    var regInfo = new XllRegistrationInfo(xllPath);
                    _xllRegistrationInfos[xllPath] = regInfo;
                    _xmlProvider.RegisterXmlFunctionInfo(GetXmlPath(xllPath));
                    added.Add(regInfo);
                }
            }
            return added;
        }

        // May be called from any thread
        public IList<FunctionInfo> GetFunctionInfos()
        {
            IList<FunctionInfo> excelDnaInfos;
            Logger.Provider.Verbose("ExcelDnaIntelliSenseProvider.GetFunctionInfos");
            lock (_xllRegistrationInfos)
            {
                Logger.Provider.Verbose("ExcelDnaIntelliSenseProvider.GetFunctionInfos - inside lock");
                excelDnaInfos = _xllRegistrationInfos.Values.SelectMany(ri => ri.GetFunctionInfos()).ToList();
            }
            Logger.Provider.Verbose("ExcelDnaIntelliSenseProvider.GetFunctionInfos - after lock");
            foreach (var info in excelDnaInfos)
            {
                Logger.Provider.Verbose($"\t{info.Name}({info.ArgumentList.Count}) - {info.Description} ");
            }

            var xmlInfos = _xmlProvider.GetFunctionInfos();
            var allInfos = excelDnaInfos.Concat(xmlInfos).ToList();

            Logger.Provider.Verbose("ExcelDnaIntelliSenseProvider.GetFunctionInfos End");
            return allInfos;
        }

        #endregion

        string GetXmlPath(string xllPath) => Path.ChangeExtension(xllPath, ".intellisense.xml");

        // Called in macro context
        // Might be implemented by COM AddIns check, or checking loaded Modules with Win32
        // Application.AddIns2 also lists add-ins interactively loaded (Excel 2010+) and has IsOpen property.
        // See: http://blogs.office.com/2010/02/16/migrating-excel-4-macros-to-vba/
        // Alternative on old Excel is DOCUMENTS(2) which lists all loaded .xlm (also .xll?)
        // Alternative, more in line with our update watch, is to enumerate all loaded modules...
        IEnumerable<string> GetLoadedXllPaths()
        {
            //// DOCUMENT(2) does not seem to include .xll add-ins

            //var loadedDocs = Integration.XlCall.Excel(Integration.XlCall.xlfDocuments, 2) as object[,];
            //if (loadedDocs == null)
            //{
            //    Logger.Provider.Verbose($"ExcelDnaIntelliSenseProvider.GetLoadedXllPaths - DOCUMENTS(2) failed");
            //    yield break;
            //}
            //for (int i = 0; i < loadedDocs.GetLength(1); i++)
            //{
            //    var docName = loadedDocs[0, i] as string;
            //    if (docName != null && Path.GetExtension(docName) == ".xll")
            //    {
            //        yield return docName;
            //    }
            //}

            //// TODO: Implement properly...
            //dynamic app = ExcelDnaUtil.Application;
            //foreach (var addin in app.AddIns2)
            //{
            //    if (addin.IsOpen && Path.GetExtension(addin.FullName) == ".xll")
            //    {
            //        yield return addin.FullName;
            //    }
            //}

            // Enumerate loaded modules - pick .xll files
            var process = Process.GetCurrentProcess();
            var modules = process.Modules;
            foreach (ProcessModule module in modules)
            {
                var fileName = module.FileName; 
                if (Path.GetExtension(fileName) == ".xll")
                {
                    yield return fileName;
                }
            }
        }

        // Must be called on the main thread, in a macro context
        void OnInvalidate(object _unused_)
        {
            Debug.Assert(Thread.CurrentThread.ManagedThreadId == 1);
            Invalidate?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Logger.Provider.Info("ExcelDnaIntelliSenseProvider.Dispose");
        }
    }
}
