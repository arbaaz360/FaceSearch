using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.Qdrant;
using FastSearch.FastApi;
using FaceSearch.Infrastructure.FastIndexing;
using FaceSearch.Infrastructure.Persistence.Mongo;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<EmbedderOptions>(builder.Configuration.GetSection("Embedder"));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection("Qdrant"));
builder.Services.Configure<FastSearchOptions>(builder.Configuration.GetSection("FastSearch"));
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));

builder.Services.AddEmbedderClient();
builder.Services.AddQdrantClient(opt =>
{
    var cfg = builder.Configuration.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();
    opt.BaseUrl = cfg.BaseUrl;
    opt.TimeoutSeconds = cfg.TimeoutSeconds;
    opt.MaxRetries = cfg.MaxRetries;
    opt.BaseDelayMs = cfg.BaseDelayMs;
});

builder.Services.AddSingleton<IMongoContext, MongoContext>();
builder.Services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoContext>().Database);

builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

app.MapControllers();
app.MapGet("/fast/health", () => Results.Ok(new { ok = true, service = "fast-search" }));

app.Run();
