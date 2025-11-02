using Application.Albums;
using Application.Indexing;
using FaceSearch.Application.Search;
using FaceSearch.Infrastructure;
using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Indexing;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// ---------- Options ----------
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection("Qdrant"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<QdrantOptions>>().Value);

builder.Services.AddEmbedderClient(opt =>
    builder.Configuration.GetSection("Embedder").Bind(opt));

builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MongoOptions>>().Value);

// ---------- HTTP clients ----------
var qdrantBase = builder.Configuration.GetValue<string>("Qdrant:BaseUrl") ?? "http://localhost:6333";
builder.Services.AddHttpClient<IQdrantClient, QdrantClient>(c => c.BaseAddress = new Uri(qdrantBase));
builder.Services.AddHttpClient<QdrantSearchClient>(c => c.BaseAddress = new Uri(qdrantBase));
builder.Services.AddHttpClient<IQdrantUpsert, QdrantUpsert>(c => c.BaseAddress = new Uri(qdrantBase));

// ---------- Mongo (Context + Db projection) ----------
builder.Services.AddSingleton<IMongoContext, MongoContext>();                           // <— provides Db + collections
builder.Services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoContext>().Db);

// ✅ Remove BOTH of these duplicates (they’re no longer needed):
// builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(...));
// builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(...));

// ---------- Bootstraps ----------
builder.Services.AddSingleton<QdrantCollectionBootstrap>();
builder.Services.AddSingleton<MongoBootstrap>();

// ---------- App services / repos ----------
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<ISeedingService, SeedingService>();

builder.Services.AddScoped<IImageRepository, ImageRepository>();
builder.Services.AddScoped<IAlbumRepository, AlbumRepository>();
builder.Services.AddScoped<IAlbumClusterRepository, AlbumClusterRepository>();
builder.Services.AddScoped<IReviewRepository, ReviewRepository>();
builder.Services.AddScoped<AlbumDominanceService>();

// ---------- Web ----------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Logging.AddConsole();

var app = builder.Build();

app.Logger.LogInformation("Embedder BaseUrl: {Base}", app.Services.GetRequiredService<EmbedderOptions>().BaseUrl);

// ---------- Startup bootstraps ----------
using (var scope = app.Services.CreateScope())
{
    try
    {
        await scope.ServiceProvider.GetRequiredService<QdrantCollectionBootstrap>().EnsureCollectionsAsync();
        await scope.ServiceProvider.GetRequiredService<MongoBootstrap>().EnsureIndexesAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Bootstrap warning; continuing startup.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();
app.Run();
