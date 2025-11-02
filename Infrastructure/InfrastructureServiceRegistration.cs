using Microsoft.Extensions.DependencyInjection;
using FaceSearch.Infrastructure.Qdrant;

namespace FaceSearch.Infrastructure
{
    public static class InfrastructureServiceRegistration
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services)
        {
            services.AddSingleton<IQdrantClient, QdrantClient>();
            services.AddSingleton<IQdrantUpsert, QdrantUpsert>();
            // add other shared services like Mongo, etc.
            return services;
        }
    }
}
