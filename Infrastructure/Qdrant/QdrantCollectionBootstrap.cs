// Infrastructure/Qdrant/QdrantCollectionBootstrap.cs
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace FaceSearch.Infrastructure.Qdrant;

public sealed class QdrantCollectionBootstrap
{
    private readonly QdrantSearchClient _client;
    private readonly ILogger<QdrantCollectionBootstrap> _log;

    public QdrantCollectionBootstrap(QdrantSearchClient client, ILogger<QdrantCollectionBootstrap> log)
    {
        _client = client;
        _log = log;
    }

    public async Task EnsureCollectionsAsync(CancellationToken ct = default)
    {
        await EnsureCollectionAsync(new CollectionSpec("clip_512", 512, "Cosine"), ct);
        await EnsureCollectionAsync(new CollectionSpec("faces_arcface_512", 512, "Cosine"), ct);
    }

    private async Task EnsureCollectionAsync(CollectionSpec spec, CancellationToken ct)
    {
        if (await _client.CollectionExistsAsync(spec.Name, ct))
        {
            _log.LogInformation("Qdrant collection {Name} already exists", spec.Name);
            return;
        }

        _log.LogInformation("Qdrant collection {Name} missing; creating…", spec.Name);
        await _client.CreateCollectionAsync(spec.Name, spec.VectorSize, spec.Distance, ct);
    }
}
