using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Workers.Indexer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

var builder = Host.CreateApplicationBuilder(args);

// -------- Options binding --------
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MongoOptions>>().Value);

builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection("Qdrant"));
// Provide QdrantOptions *value* because IndexerWorker takes QdrantOptions directly
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<QdrantOptions>>().Value);

builder.Services.Configure<IndexerOptions>(builder.Configuration.GetSection("Indexer"));

// -------- Embedder (matches your EmbedderClient ctor) --------
builder.Services.AddEmbedderClient(opt => builder.Configuration.GetSection("Embedder").Bind(opt));

// -------- Qdrant clients --------
var qdrantBase = builder.Configuration.GetValue<string>("Qdrant:BaseUrl") ?? "http://localhost:6333";
builder.Services.AddHttpClient<IQdrantClient, QdrantClient>(c => c.BaseAddress = new Uri(qdrantBase));
builder.Services.AddHttpClient<QdrantSearchClient>(c => c.BaseAddress = new Uri(qdrantBase));
// IQdrantUpsert uses HttpClient too → give it a BaseAddress
builder.Services.AddHttpClient<IQdrantUpsert, QdrantUpsert>(c => c.BaseAddress = new Uri(qdrantBase));

// -------- Mongo (context + db + repo + bootstrap) --------
// Your MongoContext builds IMongoClient internally from MongoOptions, so just register the context.
builder.Services.AddSingleton<IMongoContext, MongoContext>();

// MongoBootstrap currently expects IMongoDatabase → project Db from your context:
builder.Services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoContext>().Db);

builder.Services.AddSingleton<IImageRepository, ImageRepository>();
builder.Services.AddSingleton<MongoBootstrap>();

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

    // quick sanity – prints total docs
    var ctx = scope.ServiceProvider.GetRequiredService<IMongoContext>();
    var count = await ctx.Images.CountDocumentsAsync(Builders<ImageDocMongo>.Filter.Empty);
    Console.WriteLine($"[Mongo] images count = {count}");
}

await app.RunAsync();
