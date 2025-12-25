// Quick script to inspect Instagram collections structure
// Run with: dotnet script inspect-instagram-collections.cs

using MongoDB.Driver;
using MongoDB.Bson;
using System.Text.Json;

var connectionString = "mongodb://127.0.0.1:27017";
var client = new MongoClient(connectionString);
var db = client.GetDatabase("instafollowing");

// Inspect followings collection
var followingsCollection = db.GetCollection<BsonDocument>("followings");
var followingSample = await followingsCollection.Find(FilterDefinition<BsonDocument>.Empty)
    .Limit(1)
    .FirstOrDefaultAsync();

Console.WriteLine("=== FOLLOWINGS COLLECTION SAMPLE ===");
Console.WriteLine(JsonSerializer.Serialize(followingSample, new JsonSerializerOptions { WriteIndented = true }));

// Inspect posts collection
var postsCollection = db.GetCollection<BsonDocument>("posts");
var postSample = await postsCollection.Find(FilterDefinition<BsonDocument>.Empty)
    .Limit(1)
    .FirstOrDefaultAsync();

Console.WriteLine("\n=== POSTS COLLECTION SAMPLE ===");
Console.WriteLine(JsonSerializer.Serialize(postSample, new JsonSerializerOptions { WriteIndented = true }));

// Get counts
var followingsCount = await followingsCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
var postsCount = await postsCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

Console.WriteLine($"\n=== COLLECTION STATS ===");
Console.WriteLine($"Followings: {followingsCount}");
Console.WriteLine($"Posts: {postsCount}");

// Get unique usernames
var uniqueTargetUsernames = await followingsCollection.DistinctAsync<string>("target_username", FilterDefinition<BsonDocument>.Empty);
var targetUsernames = await uniqueTargetUsernames.ToListAsync();
Console.WriteLine($"\nUnique target usernames: {targetUsernames.Count}");
Console.WriteLine($"Sample: {string.Join(", ", targetUsernames.Take(5))}");

