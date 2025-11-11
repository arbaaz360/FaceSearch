// Workers.Indexer/Program.cs
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Workers.Indexer;
using Infrastructure.Mongo.Models;
using MongoDB.Driver;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddIndexerServices(builder.Configuration);

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
