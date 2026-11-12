using System.Collections.Concurrent;
using System.Security.Cryptography;

using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.IO.Objects.OnDemand;
using CUE4Parse.UE4.Readers;

using Microsoft.AspNetCore.Mvc;

using Serilog;

/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */
/* Mirror Cloud Content Hashes */
/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ */

namespace Mirror.Cloud.Controllers;

public partial class CloudApiController
{
    /* What a reader comparing two builds of the game needs to know about a file: whether its cooked
     * bytes are the same. An IoStore container already says so for every chunk it holds (the hash
     * the cooker wrote into the table of contents, over the chunk's uncompressed bytes), so nothing
     * has to be read or decompressed to answer. The mappings play no part in it: a hash names the
     * cooked bytes, not how they are read.
     *
     * The table's hashes are not kept when a container is mounted, so each container's is read once
     * more, on first use, and kept here. */
    private sealed record TocHashes(Dictionary<FIoChunkId, int> Index, FIoStoreTocEntryMeta[] Metas);

    private static readonly ConcurrentDictionary<string, TocHashes?> TocHashesByContainer = new(StringComparer.OrdinalIgnoreCase);

    /* A container mounted from the game's Paks folder by name only (the on-demand ones) is looked for
     * there, where it sits */
    private static TocHashes? ReadTocHashes(IoStoreReader reader, string? archiveDirectory)
    {
        return TocHashesByContainer.GetOrAdd(reader.Path, named =>
        {
            var path = named;

            if (!System.IO.File.Exists(path) && archiveDirectory is not null)
            {
                path = Path.Combine(archiveDirectory, Path.GetFileName(named));
            }

            try
            {
                var archive = new FByteArchive(path, System.IO.File.ReadAllBytes(path), reader.Versions);
                var toc = new FIoStoreTocResource(archive, EIoStoreTocReadOptions.ReadTocMeta);

                if (toc.ChunkMetas is null) return null;

                var index = new Dictionary<FIoChunkId, int>(toc.ChunkIds.Length);

                for (var i = 0; i < toc.ChunkIds.Length; i++)
                {
                    index.TryAdd(toc.ChunkIds[i], i);
                }

                return new TocHashes(index, toc.ChunkMetas);
            }
            catch (Exception exception)
            {
                Log.Warning($"[Mirror.Cloud]: Could not read the chunk hashes of {path}: {exception.Message}");

                return null;
            }
        });
    }

    private static readonly ConcurrentDictionary<IOnDemandContainerEntry, Dictionary<FIoChunkId, int>> OnDemandIndex = new();

    private static string? OnDemandHash(IOnDemandContainerEntry container, FIoChunkId chunkId)
    {
        /* The partitioned table's own lookup answers with the partition's hash, which many chunks
         * share, so the chunk's entry is read here instead */
        if (container is CUE4Parse.UE4.IO.Objects.OnDemand.V2.FOnDemandContainerEntry { ChunkIds: { } ids, ChunkEntries: { } entries })
        {
            var index = OnDemandIndex.GetOrAdd(container, _ =>
            {
                var map = new Dictionary<FIoChunkId, int>(ids.Length);

                for (var i = 0; i < ids.Length; i++) map.TryAdd(ids[i], i);

                return map;
            });

            if (!index.TryGetValue(chunkId, out var at) || at >= entries.Length) return null;

            var entry = entries[at];

            var hex = entry.Hash.ToString();

            /* A 96-bit hash fills the first 12 of the 20 bytes */
            if (hex.AsSpan(24).IndexOfAnyExcept('0') < 0) hex = hex[..24];

            return $"{hex}:{entry.RawSize}";
        }

        return container.TryGetFileEntryHash(chunkId, out var fileEntry) ? fileEntry.FileEntryHash.ToString() : null;
    }

    /* The hash of one file and where it came from: "toc" when the container's table names it,
     * "ondemand" when the on-demand table does,
     * "sha1" when it had to be read (a pak entry), and null with "unavailable" when it could not
     * be had. A container's file is never read for its hash: an on-demand one would be downloaded. */
    private static (string? Hash, string Basis) HashOf(GameFile file, string? archiveDirectory, bool readWhenNoToc)
    {
        /* An on-demand container sits on the CDN, and its table there names each chunk by a hash of
         * the chunk (FOnDemandChunkEntry::Hash, 96 bits in the partitioned tables) */
        if (file is FIoStoreEntry { IoStoreReader: IoStoreOnDemandReader onDemand } remote)
        {
            return OnDemandHash(onDemand.Container, remote.ChunkId) is { } chunkHash
                ? (chunkHash, "ondemand")
                : (null, "unavailable");
        }

        if (file is FIoStoreEntry entry)
        {
            if (ReadTocHashes(entry.IoStoreReader, archiveDirectory) is { } hashes &&
                hashes.Index.TryGetValue(entry.ChunkId, out var index))
            {
                var hash = hashes.Metas[index].ChunkHash.ToString();

                /* A container written without hashes leaves them zero, which would make every file the same */
                if (hash.AsSpan().IndexOfAnyExcept('0') >= 0) return (hash, "toc");
            }

            return (null, "unavailable");
        }

        if (!readWhenNoToc) return (null, "unavailable");

        try
        {
            var data = file.Read();

            return (Convert.ToHexString(SHA1.HashData(data)), "sha1");
        }
        catch (Exception)
        {
            return (null, "unavailable");
        }
    }

    /* Request to hash every file mounted under a path, for telling which cooked files changed between
     * two builds. Paged by offset, as a whole game holds far more files than one answer should carry. */
    [HttpGet("hashes")]
    public ActionResult GetHashes(string? prefix, int offset = 0, int limit = 20000, bool read = true)
    {
        var profile = RouteBuild(out var routeError) ?? MainProfile;

        if (routeError is not null) return routeError;
        if (profile is null || !profile.IsInitialized || profile.Provider.Files.Count == 0) return NotInitializedResponse;

        var matching = profile.Provider.Files
            .Where(pair => prefix is null || pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var page = matching.Skip(Math.Max(offset, 0)).Take(Math.Clamp(limit, 1, 100000));

        var files = page.AsParallel().AsOrdered().Select(pair =>
        {
            var (hash, basis) = HashOf(pair.Value, profile.ArchiveDirectory, read);

            return new
            {
                path = pair.Value.Path,
                size = pair.Value.Size,
                hash,
                basis
            };
        }).ToList();

        var next = offset + files.Count;

        return new JsonResult(new
        {
            prefix,
            total = matching.Count,
            offset,
            next = next < matching.Count ? next : (int?) null,
            build = profile.Name,
            archiveDirectory = profile.ArchiveDirectory,
            mappings = profile.LoadedMappingsFile,
            files
        });
    }
}
