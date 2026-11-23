using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Whole collections kept current in memory by Firestore listeners, for ManagerWeb's background checks
/// (PERFORMANCE_REPORT.md, P2 / I-02). The first request for a collection reads it once; after that
/// Firestore sends only the documents that change, instead of the checks re-reading every shift and
/// payment every 30 minutes (and every SOS alert every 3 minutes), day and night.
///
/// The documents are the same ones a direct read would return - the listener holds the latest committed
/// state - so totals and alerts don't change. Used by the background checks and the read-only Executive
/// Dashboard (P4). Pages that save something read Firestore directly, so they can't show a list from before
/// their own write.
/// If a listener can't deliver (not started in time, or failed), the caller gets null and reads
/// Firestore directly, exactly as before.
/// </summary>
public sealed class CollectionCache : IAsyncDisposable
{
    private static readonly TimeSpan FirstSnapshotWait = TimeSpan.FromSeconds(30);

    private readonly Lazy<FirestoreDb> _db;
    private readonly ILogger<CollectionCache> _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public CollectionCache(Lazy<FirestoreDb> db, ILogger<CollectionCache> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>The collection's current documents, or null when the listener can't provide them (the
    /// caller then reads Firestore directly).</summary>
    public async Task<IReadOnlyList<DocumentSnapshot>?> GetDocumentsAsync(string collection)
    {
        Entry entry = _entries.GetOrAdd(collection, Start);
        if (entry.Latest is QuerySnapshot latest)
        {
            return latest.Documents;
        }

        try
        {
            QuerySnapshot first = await entry.First.Task.WaitAsync(FirstSnapshotWait);
            return first.Documents;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Collection cache for {Collection} isn't ready; reading Firestore directly.", collection);
            return null;
        }
    }

    private Entry Start(string collection)
    {
        var entry = new Entry();
        try
        {
            entry.Listener = _db.Value.Collection(collection).Listen(snapshot =>
            {
                entry.Latest = snapshot;
                entry.First.TrySetResult(snapshot);
                PerfLog.Reads($"{collection} (listener update)", snapshot.Changes.Count);
            });

            // A failed listener is dropped, so the next request starts a fresh one.
            _ = entry.Listener.ListenerTask.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    _logger.LogWarning(task.Exception, "Collection cache listener for {Collection} stopped; it restarts on next use.", collection);
                }
                entry.First.TrySetException(task.Exception ?? new Exception("Listener stopped."));
                _entries.TryRemove(new KeyValuePair<string, Entry>(collection, entry));
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            entry.First.TrySetException(ex);
            _entries.TryRemove(new KeyValuePair<string, Entry>(collection, entry));
        }

        return entry;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Entry entry in _entries.Values)
        {
            if (entry.Listener is null) continue;
            try { await entry.Listener.StopAsync(); } catch { /* already stopped */ }
        }
        _entries.Clear();
    }

    private sealed class Entry
    {
        public FirestoreChangeListener? Listener;
        public volatile QuerySnapshot? Latest;
        public readonly TaskCompletionSource<QuerySnapshot> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
