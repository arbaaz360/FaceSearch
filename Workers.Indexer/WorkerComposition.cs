// Workers.Indexer/WorkerComposition.cs
using Application.Indexing;
using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Indexing;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Services.Implementations;
using FaceSearch.Workers.Indexer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace FaceSearch.Workers.Indexer;

public static class WorkerComposition
{
    public static IServiceCollection AddIndexerServices(this IServiceCollection services, IConfiguration config)
    {
        // -------- Options --------
        services.Configure<MongoOptions>(config.GetSection("Mongo"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MongoOptions>>().Value);

        services.Configure<QdrantOptions>(config.GetSection("Qdrant"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<QdrantOptions>>().Value);

        services.Configure<IndexerOptions>(config.GetSection("Indexer"));

        // -------- Embedder --------
        services.AddEmbedderClient(opt => config.GetSection("Embedder").Bind(opt));

        // -------- Qdrant clients --------
        var qdrantBase = config.GetValue<string>("Qdrant:BaseUrl") ?? "http://localhost:6333";
        services.AddHttpClient<IQdrantClient, QdrantClient>(c => c.BaseAddress = new Uri(qdrantBase));
        services.AddHttpClient<QdrantSearchClient>(c => c.BaseAddress = new Uri(qdrantBase));
        services.AddHttpClient<IQdrantUpsert, QdrantUpsert>(c => c.BaseAddress = new Uri(qdrantBase));

        // -------- Mongo --------
        services.AddSingleton<IMongoContext, MongoContext>();
        services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoContext>().Database);

        // -------- Repos / services --------
        services.AddSingleton<IImageRepository, ImageRepository>();
        services.AddSingleton<IAlbumRepository, AlbumRepository>();
        services.AddSingleton<IAlbumClusterRepository, AlbumClusterRepository>();
        services.AddSingleton<IReviewRepository, ReviewRepository>();

        services.AddSingleton<AlbumFinalizerService>();
        services.AddSingleton<AlbumReviewService>();
        services.AddSingleton<ISeedingService, SeedingService>();

        // 👇 Add your seeding service (interface if you have one)
        services.AddSingleton<SeedingService>(); // or: services.AddSingleton<ISeedingService, SeedingService>();

        services.AddSingleton<MongoBootstrap>();

        // -------- Hosted worker --------
        services.AddHostedService<IndexerWorker>();

        return services;
    }
}
