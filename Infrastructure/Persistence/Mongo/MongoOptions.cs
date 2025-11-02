namespace FaceSearch.Infrastructure.Persistence.Mongo;

public sealed class MongoOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string Database { get; set; } = "facesearch";
    public string ImagesCollection { get; set; } = "images";
}
