using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace FaceSearch.Infrastructure.Persistence.Mongo;

public static class MongoServiceCollectionExtensions
{
    public static IServiceCollection AddMongoInfrastructure(this IServiceCollection services, Action<MongoOptions>? configure = null)
    {
        var opt = new MongoOptions();
        configure?.Invoke(opt);

        services.AddSingleton(opt);
        services.AddSingleton<IMongoContext, MongoContext>();
        services.AddSingleton<IImageRepository, ImageRepository>();
        return services;
    }
}
