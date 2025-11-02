// Infrastructure/Mongo/MongoBootstrap.cs
using MongoDB.Bson;
using MongoDB.Driver;

public sealed class MongoBootstrap
{
    private readonly IMongoDatabase _db;
    public MongoBootstrap(IMongoDatabase db) => _db = db;

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var images = _db.GetCollection<ImageDoc>("images");

        var idx1 = new CreateIndexModel<ImageDoc>(
            Builders<ImageDoc>.IndexKeys.Ascending(x => x.ImageId),
            new CreateIndexOptions<ImageDoc>
            {
                Name = "ux_image_id",
                Unique = true,
                // ✅ only enforce uniqueness when image_id is non-null
                PartialFilterExpression = Builders<ImageDoc>.Filter.Exists(x => x.ImageId, true)
            });

        var idx2 = new CreateIndexModel<ImageDoc>(
            Builders<ImageDoc>.IndexKeys
                .Ascending(x => x.AlbumId)
                .Ascending(x => x.Account)
                .Ascending(x => x.UnixTs),
            new CreateIndexOptions<ImageDoc> { Name = "ix_album_account_ts" });

        var idx3 = new CreateIndexModel<ImageDoc>(
            Builders<ImageDoc>.IndexKeys
                .Ascending(x => x.Tags),
            new CreateIndexOptions<ImageDoc> { Name = "ix_tags" });

        try
        {
            await images.Indexes.CreateManyAsync(new[] { idx1, idx2, idx3 }, ct);
        }
        catch (MongoCommandException ex) when (ex.Code == 85)
        {
            // Index already exists with different options; ignore
            Console.WriteLine("Indexes already exist, continuing...");
        }
    }
}
