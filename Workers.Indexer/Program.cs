using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Services.Implementations;
using FaceSearch.Workers.Indexer;
using Infrastructure.Mongo.Models;                          // if you use ImageDocMongo in the boot log
using Microsoft.Extensions.Options;
using MongoDB.Driver;

var builder = Host.CreateApplicationBuilder(args);

// -------- Options binding --------
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MongoOptions>>().Value);

builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection("Qdrant"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<QdrantOptions>>().Value);

builder.Services.Configure<IndexerOptions>(builder.Configuration.GetSection("Indexer"));

// -------- Embedder --------
builder.Services.AddEmbedderClient(opt => builder.Configuration.GetSection("Embedder").Bind(opt));

// -------- Qdrant clients --------
var qdrantBase = builder.Configuration.GetValue<string>("Qdrant:BaseUrl") ?? "http://localhost:6333";
builder.Services.AddHttpClient<IQdrantClient, QdrantClient>(c => c.BaseAddress = new Uri(qdrantBase));
builder.Services.AddHttpClient<QdrantSearchClient>(c => c.BaseAddress = new Uri(qdrantBase));
builder.Services.AddHttpClient<IQdrantUpsert, QdrantUpsert>(c => c.BaseAddress = new Uri(qdrantBase));

// -------- Mongo (context + repos + bootstrap) --------
builder.Services.AddSingleton<IMongoContext, MongoContext>();
builder.Services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoContext>().Database);

builder.Services.AddSingleton<IImageRepository, ImageRepository>();
builder.Services.AddSingleton<IAlbumRepository, AlbumRepository>();        // ✅ add
builder.Services.AddSingleton<IAlbumClusterRepository, AlbumClusterRepository>();
builder.Services.AddSingleton<AlbumFinalizerService>();                    // ✅ add
builder.Services.AddSingleton<AlbumReviewService>();
builder.Services.AddSingleton<IAlbumRepository, AlbumRepository>();
builder.Services.AddSingleton<MongoBootstrap>();
builder.Services.AddSingleton<IReviewRepository, ReviewRepository>();


// -------- Hosted worker --------
builder.Services.AddHostedService<IndexerWorker>();

var app = builder.Build();

// ---- Optional bootstraps at startup ----
using (var scope = app.Services.CreateScope())
{
    var qBoot = new QdrantCollectionBootstrap(
        scope.ServiceProvider.GetRequiredService<QdrantSearchClient>(),
        scope.ServiceProvider.GetRequiredService<ILogger<QdrantCollectionBootstrap>>());
    await qBoot.EnsureCollectionsAsync();

    await scope.ServiceProvider.GetRequiredService<MongoBootstrap>().EnsureIndexesAsync();

    var ctx = scope.ServiceProvider.GetRequiredService<IMongoContext>();
    var count = await ctx.Images.CountDocumentsAsync(Builders<ImageDocMongo>.Filter.Empty);
    Console.WriteLine($"[Mongo] images count = {count}");
}

await app.RunAsync();
