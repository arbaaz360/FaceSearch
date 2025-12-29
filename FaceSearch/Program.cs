using Application.Indexing;
using Contracts.Indexing;
using FaceSearch.Application.Search;
using FaceSearch.Infrastructure;
using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Indexing;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Options.Config;
using FaceSearch.Services.Implementations;
using Infrastructure.Mongo.Models;
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
builder.Services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoContext>().Database);

// ✅ Remove BOTH of these duplicates (they’re no longer needed):


// ---------- Bootstraps ----------
builder.Services.AddSingleton<QdrantCollectionBootstrap>();
builder.Services.AddSingleton<MongoBootstrap>();

// ---------- App services / repos ----------
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<ISeedingService, SeedingService>();
builder.Services.AddScoped<IInstagramSeedingService, InstagramSeedingService>();
builder.Services.AddScoped<Contracts.Indexing.IPostFetchService, PostFetchService>();

builder.Services.AddScoped<IImageRepository, ImageRepository>();
builder.Services.AddScoped<IAlbumRepository, AlbumRepository>();
builder.Services.AddScoped<IAlbumClusterRepository, AlbumClusterRepository>();
builder.Services.AddScoped<AlbumReviewService>();
builder.Services.AddSingleton<IReviewRepository, ReviewRepository>();
builder.Services.AddSingleton<IFaceReviewRepository, FaceReviewRepository>();

builder.Services.AddScoped<AlbumFinalizerService>();
builder.Services.Configure<AlbumFinalizerOptions>(
    builder.Configuration.GetSection("AlbumFinalizer"));


// ---------- Web ----------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opt =>
{
    // include XML comments for better Swagger docs
    var xml = Path.Combine(AppContext.BaseDirectory, "FaceSearch.xml");
    if (File.Exists(xml))
        opt.IncludeXmlComments(xml);
});
builder.Services.AddInfrastructure2();
builder.Services.AddSingleton<IAlbumRepository, AlbumRepository>();

builder.Logging.AddConsole();

var app = builder.Build();

// Log embedder configuration
var embedderOpts = app.Services.GetRequiredService<EmbedderOptions>();
if (embedderOpts.BaseUrls != null && embedderOpts.BaseUrls.Length > 1)
{
    app.Logger.LogInformation("Embedder: Load-balanced with {Count} instances: {Urls}", 
        embedderOpts.BaseUrls.Length, string.Join(", ", embedderOpts.BaseUrls));
}
else
{
    app.Logger.LogInformation("Embedder BaseUrl: {Base}", embedderOpts.BaseUrl);
}

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

// Serve SPA static assets and default index file
app.UseDefaultFiles();
app.UseStaticFiles();
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
app.MapControllers();
app.MapFallbackToFile("/index.html");
app.Run();
