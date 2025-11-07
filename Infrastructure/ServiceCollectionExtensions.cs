using Microsoft.Extensions.DependencyInjection;
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;

namespace FaceSearch.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructure2(this IServiceCollection services)
        {
            services.AddSingleton<IMongoContext, MongoContext>();

            // Repositories
            services.AddSingleton<IAlbumRepository, AlbumRepository>();
            services.AddSingleton<IAlbumClusterRepository, AlbumClusterRepository>();
            services.AddSingleton<IReviewRepository, ReviewRepository>();
            services.AddSingleton<IImageRepository, ImageRepository>();

            return services;
        }
    }
}
