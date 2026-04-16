using System.Runtime.InteropServices;

using ExcelDna.Integration.CustomUI;

using LambdaBoss.Commands;

using Taglo.Excel.Common;

namespace LambdaBoss;

[ComVisible(true)]
public class RibbonController : ExcelRibbon
{
    private IRibbonUI? _ribbon;

    public override string GetCustomUI(string ribbonId) =>
        @"<customUI xmlns='http://schemas.microsoft.com/office/2009/07/customui' onLoad='OnLoad'>
  <ribbon>
    <tabs>
      <tab id='LambdaBossTab' label='Lambda Boss'>
        <group id='LibraryGroup' label='Library'>
          <button id='LoadLibrary'
                  label='Load Library'
                  size='large'
                  imageMso='ModuleInsert'
                  onAction='OnLoadLibrary'
                  screentip='Open the Lambda library browser (Ctrl+Shift+L)' />
        </group>
        <group id='GenerateGroup' label='Generate'>
          <button id='LetToLambdaButton'
                  label='LET to LAMBDA'
                  size='large'
                  imageMso='FunctionWizard'
                  onAction='OnLetToLambda'
                  screentip='Convert the active cell&apos;s =LET(...) formula into a workbook-scoped LAMBDA' />
        </group>
        <group id='ManageGroup' label='Manage'>
          <button id='SettingsButton'
                  label='Settings'
                  size='large'
                  imageMso='ControlProperties'
                  onAction='OnSettings'
                  screentip='Manage repository sources and preferences' />
          <button id='RefreshButton'
                  label='Refresh'
                  size='normal'
                  imageMso='Refresh'
                  onAction='OnRefresh'
                  screentip='Re-fetch libraries from all repos' />
        </group>
        <group id='InfoGroup' label='Info'>
          <button id='AboutButton'
                  label='About'
                  size='normal'
                  imageMso='Info'
                  onAction='OnAbout'
                  screentip='About Lambda Boss' />
          <button id='UpdateButton'
                  label='Update Available'
                  size='normal'
                  imageMso='FileUpdate'
                  onAction='OnUpdate'
                  getVisible='GetUpdateVisible'
                  screentip='A new version is available — click to open the download page' />
        </group>
      </tab>
    </tabs>
  </ribbon>
</customUI>";

    public void OnLoad(IRibbonUI ribbon)
    {
        _ribbon = ribbon;
        UpdateChecker.UpdateAvailable += () =>
        {
            try { _ribbon?.InvalidateControl("UpdateButton"); }
            catch { /* ribbon may be disposed */ }
        };
    }

    public void OnLoadLibrary(IRibbonControl control)
    {
        ShowLambdaPopupCommand.ShowLambdaPopup();
    }

    public void OnSettings(IRibbonControl control)
    {
        ShowLambdaPopupCommand.ShowSettings();
    }

    public void OnRefresh(IRibbonControl control)
    {
        ShowLambdaPopupCommand.RefreshData();
    }

    public void OnLetToLambda(IRibbonControl control)
    {
        ConvertLetToLambdaCommand.Run();
    }

    public void OnAbout(IRibbonControl control)
    {
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                          ?? new Version(0, 0, 0);
            System.Windows.MessageBox.Show(
                $"Lambda Boss v{version.Major}.{version.Minor}.{version.Build}\n\n"
                + "Excel add-in for accessing GitHub Lambda libraries.\n\n"
                + "https://github.com/TagloGit/lambda-boss",
                "About Lambda Boss",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("RibbonController.OnAbout", ex);
        }
    }

    public void OnUpdate(IRibbonControl control)
    {
        try
        {
            var url = UpdateChecker.ReleaseUrl;
            if (!string.IsNullOrEmpty(url))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("RibbonController.OnUpdate", ex);
        }
    }

    public bool GetUpdateVisible(IRibbonControl control)
    {
        return UpdateChecker.NewVersionAvailable != null;
    }
}
