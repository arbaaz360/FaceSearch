using FaceSearch.Application.Search;
using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Services.Interfaces;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Application.Indexing;
using FaceSearch.Infrastructure.Indexing;
using FaceSearch.Infrastructure;
var builder = WebApplication.CreateBuilder(args);

// ---- strongly-typed options ----
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection("Qdrant"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<QdrantOptions>>().Value);

// (EmbedderOptions is registered inside AddEmbedderClient)
builder.Services.AddEmbedderClient(opt =>
    builder.Configuration.GetSection("Embedder").Bind(opt));

// ---- config fallbacks ----
var qdrantBase = builder.Configuration.GetValue<string>("Qdrant:BaseUrl") ?? "http://localhost:6333";
var mongoConn = builder.Configuration.GetValue<string>("Mongo:ConnectionString") ?? "mongodb://localhost:27017";
var mongoDb = builder.Configuration.GetValue<string>("Mongo:Database") ?? "facesearch";


// ---- HTTP clients ----
builder.Services.AddHttpClient<IQdrantClient, QdrantClient>(c =>
{
    c.BaseAddress = new Uri(qdrantBase);
});
builder.Services.AddHttpClient<QdrantSearchClient>(c =>
{
    c.BaseAddress = new Uri(qdrantBase);
});
builder.Services.AddHttpClient<IQdrantUpsert, QdrantUpsert>(c =>        // <-- add this
{
    c.BaseAddress = new Uri(qdrantBase);
});

// ---- bootstraps / mongo ----
builder.Services.AddSingleton<QdrantCollectionBootstrap>();
builder.Services.AddSingleton<MongoBootstrap>();

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConn));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDb));

// ---- app services ----
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(builder.Configuration["Mongo:Connection"] ?? "mongodb://localhost:27017"));
builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(builder.Configuration["Mongo:Database"] ?? "facesearch"));
builder.Services.AddScoped<ISeedingService, SeedingService>();
builder.Services.AddInfrastructure();

// ---- web ----
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Logging.AddConsole();

var app = builder.Build();
app.Logger.LogInformation("Embedder BaseUrl from config: {Base}",
    app.Services.GetRequiredService<EmbedderOptions>().BaseUrl);
// ---- startup bootstraps ----
// ---- startup bootstraps ----
using (var scope = app.Services.CreateScope())
{
    try
    {
        var qBoot = scope.ServiceProvider.GetRequiredService<QdrantCollectionBootstrap>();
        await qBoot.EnsureCollectionsAsync();

        var mBoot = scope.ServiceProvider.GetRequiredService<MongoBootstrap>();
        await mBoot.EnsureIndexesAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Bootstrap encountered a non-critical error; continuing startup.");
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
