using FaceSearch.Infrastructure.Embedder;
using FaceSearch.Infrastructure.FastIndexing;
using FaceSearch.Infrastructure.Qdrant;
using FaceSearch.Infrastructure.Persistence.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workers.FastIndexer;
using MongoDB.Driver;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(cfg =>
    {
        cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        cfg.AddEnvironmentVariables();
        cfg.AddCommandLine(args);
    })
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<FastIndexerOptions>(ctx.Configuration.GetSection("FastIndexer"));
        services.Configure<EmbedderOptions>(ctx.Configuration.GetSection("Embedder"));
        services.Configure<QdrantOptions>(ctx.Configuration.GetSection("Qdrant"));
        services.Configure<MongoOptions>(ctx.Configuration.GetSection("Mongo"));
        services.Configure<FastIndexerOptions>(ctx.Configuration.GetSection("FastIndexer"));

        services.AddEmbedderClient();
        services.AddQdrantClient(opt =>
        {
            var cfg = ctx.Configuration.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();
            opt.BaseUrl = cfg.BaseUrl;
            opt.TimeoutSeconds = cfg.TimeoutSeconds;
            opt.MaxRetries = cfg.MaxRetries;
            opt.BaseDelayMs = cfg.BaseDelayMs;
        });

        services.AddSingleton<IMongoContext, MongoContext>();
        services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoContext>().Database);

        services.AddHostedService<FastIndexerWorker>();
    })
    .Build();

await host.RunAsync();
