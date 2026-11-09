using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Writes a <see cref="ConfigState"/> as a snapshot (one content-addressed blob per collection) and
/// loads a snapshot back into a state.
/// </summary>
public sealed class ConfigSnapshotStore
{
    private readonly IConfigBlobStore _blobs;
    private readonly string _siteSlug;

    public ConfigSnapshotStore(IConfigBlobStore blobs, string siteSlug)
    {
        _blobs = blobs;
        _siteSlug = siteSlug;
    }

    /// <summary>Stores the state's collections and inserts the snapshot row.</summary>
    public async Task<ConfigSnapshot> SaveAsync(
        IChangeControlRepository repository, ConfigState state, ConfigSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var blobs = new List<ConfigSnapshotBlob>();
        long total = 0;
        var objects = 0;
        foreach (var (collection, list) in state.Collections)
        {
            var json = CanonicalJson.SerializeCollection(list);
            var hash = await _blobs.PutAsync(_siteSlug, json, cancellationToken);
            var size = CanonicalJson.ByteCount(json);
            blobs.Add(new ConfigSnapshotBlob { Collection = collection, Hash = hash, ObjectCount = list.Count, SizeBytes = size });
            total += size;
            objects += list.Count;
        }

        snapshot.TakenAt = snapshot.TakenAt == default ? state.ReadAt : snapshot.TakenAt;
        snapshot.NetworkAppVersion ??= state.NetworkAppVersion;
        snapshot.ObjectCount = objects;
        snapshot.TotalBytes = total;
        snapshot.ReadErrorsJson = state.ReadErrors.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(state.ReadErrors);
        return await repository.AddSnapshotAsync(snapshot, blobs, cancellationToken);
    }

    /// <summary>Loads a snapshot into a state. A blob that cannot be read is recorded as a read error.</summary>
    public async Task<ConfigState> LoadAsync(IChangeControlRepository repository, ConfigSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var state = new ConfigState { ReadAt = snapshot.TakenAt, NetworkAppVersion = snapshot.NetworkAppVersion };
        foreach (var blob in await repository.GetSnapshotBlobsAsync(snapshot.Id, cancellationToken))
        {
            var json = await _blobs.GetAsync(_siteSlug, blob.Hash, cancellationToken);
            if (json == null)
            {
                state.ReadErrors[blob.Collection] = "The stored copy is missing or unreadable";
                continue;
            }
            state.Collections[blob.Collection] = CanonicalJson.DeserializeCollection(json);
        }
        if (snapshot.ReadErrorsJson is { Length: > 0 } errors)
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(errors) ?? [];
            foreach (var (k, v) in map) state.ReadErrors.TryAdd(k, v);
        }
        return state;
    }

    /// <summary>Deletes blob files no snapshot references any more.</summary>
    public async Task<int> CollectGarbageAsync(IChangeControlRepository repository, CancellationToken cancellationToken = default)
    {
        var referenced = await repository.GetReferencedBlobHashesAsync(cancellationToken);
        var orphans = _blobs.ListHashes(_siteSlug).Where(h => !referenced.Contains(h)).ToList();
        _blobs.Delete(_siteSlug, orphans);
        return orphans.Count;
    }
}
