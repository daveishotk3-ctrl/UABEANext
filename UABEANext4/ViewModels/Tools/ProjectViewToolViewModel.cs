using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UABEANext4.AssetWorkspace;
using UABEANext4.Logic;
using UABEANext4.Logic.Project;

namespace UABEANext4.ViewModels.Tools;

/// <summary>
/// Shows the loaded files as the world they describe: every mesh, placed with its position, rotation and scale.
/// </summary>
public partial class ProjectViewToolViewModel : Tool
{
    const string TOOL_TITLE = "Project View";

    public Workspace Workspace { get; }

    [ObservableProperty]
    private ProjectScene? _scene = null;
    [ObservableProperty]
    private bool _showInactive = true;
    [ObservableProperty]
    private bool _isBuilding = false;
    [ObservableProperty]
    private double _progressValue = 0;
    [ObservableProperty]
    private string _statusText = "Select files in the Workspace Explorer, then press \"Build from selection\".";
    [ObservableProperty]
    private string _warningsText = string.Empty;

    private List<WorkspaceItem> _selectedItems = new();
    private CancellationTokenSource? _buildCts = null;

    [Obsolete("This constructor is for the designer only and should not be used directly.", true)]
    public ProjectViewToolViewModel()
    {
        Workspace = new();

        Id = TOOL_TITLE.Replace(" ", "");
        Title = TOOL_TITLE;
    }

    public ProjectViewToolViewModel(Workspace workspace)
    {
        Workspace = workspace;

        Id = TOOL_TITLE.Replace(" ", "");
        Title = TOOL_TITLE;

        WeakReferenceMessenger.Default.Register<SelectedWorkspaceItemChangedMessage>(this, (r, m) => OnSelectedWorkspaceItemsChanged(m));
        WeakReferenceMessenger.Default.Register<WorkspaceClosingMessage>(this, OnWorkspaceClosing);
    }

    private void OnSelectedWorkspaceItemsChanged(SelectedWorkspaceItemChangedMessage message)
    {
        _selectedItems = message.Value;

        if (!IsBuilding && Scene == null)
        {
            var fileCount = GetSelectedFiles().Count;
            StatusText = fileCount == 0
                ? "Select files in the Workspace Explorer, then press \"Build from selection\"."
                : $"{fileCount:N0} file(s) selected. Press \"Build from selection\" to show them.";
        }
    }

    private void OnWorkspaceClosing(object recipient, WorkspaceClosingMessage message)
    {
        _buildCts?.Cancel();
        _selectedItems = new();

        Scene = null;
        IsBuilding = false;
        ProgressValue = 0;
        WarningsText = string.Empty;
        StatusText = "Select files in the Workspace Explorer, then press \"Build from selection\".";
    }

    private List<AssetsFileInstance> GetSelectedFiles()
    {
        return GetAssetsFiles(_selectedItems);
    }

    private List<AssetsFileInstance> GetAllFiles()
    {
        return GetAssetsFiles(Workspace.RootItems);
    }

    private static List<AssetsFileInstance> GetAssetsFiles(IEnumerable<WorkspaceItem> items)
    {
        // a bundle in the selection stands for every assets file inside it
        return WorkspaceItem.GetAssetsFileWorkspaceItems(items)
            .Where(i => i.Loaded)
            .Select(i => i.Object)
            .OfType<AssetsFileInstance>()
            .ToList();
    }

    [RelayCommand]
    private async Task BuildFromSelection()
    {
        await BuildAsync(GetSelectedFiles(), "the selected files");
    }

    [RelayCommand]
    private async Task BuildFromAllFiles()
    {
        await BuildAsync(GetAllFiles(), "all loaded files");
    }

    [RelayCommand]
    private void CancelBuild()
    {
        _buildCts?.Cancel();
    }

    private async Task BuildAsync(List<AssetsFileInstance> files, string description)
    {
        if (files.Count == 0)
        {
            StatusText = $"There are no assets files in {description}.";
            return;
        }

        _buildCts?.Cancel();
        var cts = new CancellationTokenSource();
        _buildCts = cts;

        IsBuilding = true;
        ProgressValue = 0;
        WarningsText = string.Empty;
        StatusText = $"Building from {files.Count:N0} file(s)...";

        // Progress<T> sends its updates to the thread it was created on, which is the UI thread
        var progress = new Progress<(float Fraction, string Text)>(update =>
        {
            if (cts.IsCancellationRequested)
                return;

            ProgressValue = update.Fraction;
            StatusText = update.Text;
        });
        IProgress<(float Fraction, string Text)> reporter = progress;

        try
        {
            var workspace = Workspace;
            var token = cts.Token;
            var result = await Task.Run(
                () => ProjectSceneBuilder.Build(workspace, files, (fraction, text) => reporter.Report((fraction, text)), token),
                token);

            if (cts.IsCancellationRequested)
                return;

            Scene = result;
            StatusText = CreateSummary(result);
            WarningsText = CreateWarnings(result);
        }
        catch (OperationCanceledException)
        {
            if (_buildCts == cts)
                StatusText = "Build cancelled.";
        }
        catch (ObjectDisposedException)
        {
            if (_buildCts == cts)
                StatusText = "Build stopped because a file was closed.";
        }
        catch (Exception ex)
        {
            if (_buildCts == cts)
                StatusText = $"Build failed: {ex.Message}";
        }
        finally
        {
            if (_buildCts == cts)
            {
                IsBuilding = false;
                ProgressValue = 0;
            }
        }
    }

    private static string CreateSummary(ProjectScene scene)
    {
        if (scene.Instances.Count == 0)
        {
            return scene.MeshesMissing > 0 || scene.MeshesFailed > 0
                ? $"Nothing to show: no meshes could be read ({scene.MeshesMissing:N0} not found, {scene.MeshesFailed:N0} failed to decode)."
                : "Nothing to show: these files have no objects with meshes.";
        }

        var text = $"{scene.Instances.Count:N0} mesh instances ({scene.Meshes.Count:N0} unique meshes) from {scene.GameObjectsVisited:N0} objects in {scene.FileCount:N0} file(s).";

        var skipped = new List<string>();
        if (scene.MeshesMissing > 0)
            skipped.Add($"{scene.MeshesMissing:N0} not found (built-in or in files that aren't loaded)");
        if (scene.MeshesFailed > 0)
            skipped.Add($"{scene.MeshesFailed:N0} failed to decode");
        if (scene.InstancesWithoutTransform > 0)
            skipped.Add($"{scene.InstancesWithoutTransform:N0} without a transform");

        if (skipped.Count > 0)
            text += " Skipped: " + string.Join(", ", skipped) + ".";

        return text;
    }

    private static string CreateWarnings(ProjectScene scene)
    {
        return string.Join(Environment.NewLine, scene.Warnings);
    }
}
