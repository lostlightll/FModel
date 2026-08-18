using System;
using System.Collections;
using System.Linq;
using System.Threading;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using FModel.Services;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.ViewModels.Commands;

public class RightClickMenuCommand : ViewModelCommand<ApplicationViewModel>
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;

    public RightClickMenuCommand(ApplicationViewModel contextViewModel) : base(contextViewModel) { }

    private enum EAction
    {
        Show,
        Export,
    }

    private enum EShowAssetType
    {
        None,
        JSON,
        Metadata,
        References,
        Decompile,
    }

    public override async void Execute(ApplicationViewModel contextViewModel, object parameter)
    {
        if (parameter is not object[] parameters || parameters[0] is not string trigger)
            return;

        var param = (parameters[1] as IEnumerable)?.OfType<object>().ToArray() ?? [];
        if (param.Length == 0) return;

        var folders = param.OfType<TreeItem>().ToArray();
        var assets = param
            .Select(static item => item switch
            {
                GameFile gf => gf, // Search view passes GameFile directly
                GameFileViewModel gvm => gvm.Asset,
                _ => null
            })
            .Where(static gf => gf is not null).ToArray();

        if (folders.Length == 0 && assets.Length == 0)
            return;

        var (action, showtype, bulktype) = trigger switch
        {
            "Assets_Extract_New_Tab" => (EAction.Show, EShowAssetType.JSON, EBulkType.None),
            "Assets_Show_Metadata" => (EAction.Show, EShowAssetType.Metadata, EBulkType.None),
            "Assets_Show_References" => (EAction.Show, EShowAssetType.References, EBulkType.None),
            "Assets_Decompile" => (EAction.Show, EShowAssetType.Decompile, EBulkType.Code),

            "Save_Data" => (EAction.Export, EShowAssetType.None, EBulkType.Raw),
            "Save_Properties" => (EAction.Export, EShowAssetType.None, EBulkType.Properties),
            "Save_Textures" => (EAction.Export, EShowAssetType.None, EBulkType.Textures),
            "Save_Models" => (EAction.Export, EShowAssetType.None, EBulkType.Meshes),
            "Save_Animations" => (EAction.Export, EShowAssetType.None, EBulkType.Animations),
            "Save_Audio" => (EAction.Export, EShowAssetType.None, EBulkType.Audio),
            "Save_Code" => (EAction.Export, EShowAssetType.None, EBulkType.Code),

            _ => throw new ArgumentOutOfRangeException("Unsupported asset action."),
        };

        await _threadWorkerView.Begin(cancellationToken =>
        {
            if (action is EAction.Show)
            {
                if (showtype is EShowAssetType.References)
                    assets = [assets.FirstOrDefault()];

                Action<GameFile> entryAction = showtype switch
                {
                    EShowAssetType.JSON => entry => contextViewModel.CUE4Parse.Extract(cancellationToken, entry, true),
                    EShowAssetType.Metadata => entry => contextViewModel.CUE4Parse.ShowMetadata(entry),
                    EShowAssetType.Decompile => entry => contextViewModel.CUE4Parse.Decompile(entry),
                    EShowAssetType.References => entry => contextViewModel.CUE4Parse.FindReferences(entry),
                    _ => throw new ArgumentOutOfRangeException("Unsupported asset action type."),
                };

                foreach (var entry in assets)
                {
                    Thread.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    entryAction(entry);
                }

                return;
            }

            var fileType = bulktype switch
            {
                EBulkType.Raw => "files",
                EBulkType.Properties => "json files",
                EBulkType.Textures => "textures",
                EBulkType.Meshes => "models",
                EBulkType.Animations => "animations",
                EBulkType.Audio => "audio files",
                EBulkType.Code => "code files",
                _ => throw new ArgumentOutOfRangeException(nameof(bulktype), bulktype, "Unsupported bulk export type"),
            };

            var batchAssets = new System.Collections.Generic.List<GameFile>();
            foreach (var item in param)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (item)
                {
                    case TreeItem folder:
                        batchAssets.AddRange(contextViewModel.CUE4Parse.GetEffectiveFolderAssets(folder));
                        break;
                    case GameFile gameFile:
                        batchAssets.Add(gameFile);
                        break;
                    case GameFileViewModel gameFileViewModel:
                        batchAssets.Add(gameFileViewModel.Asset);
                        break;
                }
            }

            var summary = contextViewModel.CUE4Parse.ExportBulk(batchAssets, bulktype, cancellationToken);
            Log.Information(
                "Bulk export completed: Type={BulkType} Assets={AssetCount} Processed={ProcessedAssetCount} Matched={MatchedAssetCount} Files={ExportedFileCount} Failed={FailedAssetCount} Workers={WorkerCount} ElapsedMs={ElapsedMilliseconds} OutputDirectory={OutputDirectory}",
                bulktype, summary.AssetCount, summary.ProcessedAssetCount, summary.MatchedAssetCount,
                summary.ExportedFileCount, summary.FailedAssetCount, summary.WorkerCount,
                summary.Elapsed.TotalMilliseconds, summary.OutputDirectory);
            LogExportSummary(summary, fileType);
        });
    }

    private static void LogExportSummary(BulkExportSummary summary, string fileType)
    {
        var severity = summary.FailedAssetCount > 0
            ? ELog.Error
            : summary.ExportedFileCount > 0 ? ELog.Information : ELog.Warning;
        FLogger.Append(severity, () =>
        {
            FLogger.Text(
                $"Exported {summary.ExportedFileCount} {fileType} from {summary.AssetCount} assets " +
                $"({summary.FailedAssetCount} failed, {summary.WorkerCount} workers, {summary.Elapsed.TotalSeconds:F1}s) to ",
                Constants.WHITE);
            FLogger.Link(summary.OutputDirectory, summary.OutputDirectory, true);
        });
    }
}
