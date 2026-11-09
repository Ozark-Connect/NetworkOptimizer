using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.ChangeControl;

public sealed partial class ChangeControlService
{
    public Task<List<ConsoleBackupRecord>> GetConsoleBackupsAsync(CancellationToken cancellationToken = default) =>
        Repo.UseAsync((r, c) => r.GetConsoleBackupsAsync(c), cancellationToken);

    public async Task<(string FileName, byte[] Content)?> DownloadConsoleBackupAsync(int backupId, CancellationToken cancellationToken = default)
    {
        var record = (await Repo.UseAsync((r, c) => r.GetConsoleBackupsAsync(c), cancellationToken)).FirstOrDefault(b => b.Id == backupId);
        if (record == null) return null;
        var bytes = await _registry.Blobs.GetFileAsync(_site.Slug, ConsoleWatch.BackupPrefix + record.FileName, cancellationToken);
        return bytes == null ? null : (record.FileName, bytes);
    }
}
