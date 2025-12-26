using System.Text.Json;
using Infrastructure.Mongo.Models;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;

var baseDirectory = @"C:\Users\ASUS\Downloads\simpscrapped\jpg6-bulk";

if (!Directory.Exists(baseDirectory))
{
    Console.WriteLine($"Directory not found: {baseDirectory}");
    Console.WriteLine("Please update the baseDirectory path in Program.cs");
    return;
}

// MongoDB connection
var connectionString = "mongodb://127.0.0.1:27017";
var databaseName = "facesearch";
var collectionName = "jpg6_data";

var client = new MongoClient(connectionString);
var database = client.GetDatabase(databaseName);
var collection = database.GetCollection<Jpg6DataMongo>(collectionName);

Console.WriteLine($"Connecting to MongoDB: {connectionString}");
Console.WriteLine($"Database: {databaseName}, Collection: {collectionName}");
Console.WriteLine($"Reading JSON files from: {baseDirectory}");
Console.WriteLine();

// Get all page directories
var pageDirectories = Directory.GetDirectories(baseDirectory, "page-*")
    .OrderBy(d => d)
    .ToList();

Console.WriteLine($"Found {pageDirectories.Count} page directories");

var totalFiles = 0;
var processedFiles = 0;
var insertedCount = 0;
var errorCount = 0;
var skippedCount = 0;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

foreach (var pageDir in pageDirectories)
{
    var jsonFiles = Directory.GetFiles(pageDir, "*.json");
    totalFiles += jsonFiles.Length;

    Console.WriteLine($"Processing {Path.GetFileName(pageDir)}: {jsonFiles.Length} JSON files");

    foreach (var jsonFile in jsonFiles)
    {
        try
        {
            var jsonContent = await File.ReadAllTextAsync(jsonFile);
            var jsonData = JsonSerializer.Deserialize<Jpg6JsonData>(jsonContent, jsonOptions);

            if (jsonData == null)
            {
                Console.WriteLine($"  ⚠️  Failed to parse JSON: {Path.GetFileName(jsonFile)}");
                errorCount++;
                continue;
            }

            // Check if document already exists (by title or source file name)
            var existingDoc = await collection.Find(x => x.Title == jsonData.Title || x.SourceFileName == Path.GetFileName(jsonFile))
                .FirstOrDefaultAsync();

            if (existingDoc != null)
            {
                skippedCount++;
                continue;
            }

            // Map to MongoDB model
            var mongoDoc = new Jpg6DataMongo
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = jsonData.Title ?? string.Empty,
                Handles = new Jpg6Handles
                {
                    Fanfix = jsonData.Handles?.Fanfix ?? new List<string>(),
                    Fanflix = jsonData.Handles?.Fanflix ?? new List<string>(),
                    Instagram = jsonData.Handles?.Instagram ?? new List<string>(),
                    Onlyfans = jsonData.Handles?.Onlyfans ?? new List<string>(),
                    Other = jsonData.Handles?.Other ?? new List<string>(),
                    Tiktok = jsonData.Handles?.Tiktok ?? new List<string>(),
                    Youtube = jsonData.Handles?.Youtube ?? new List<string>()
                },
                Jpg6Urls = jsonData.Jpg6Urls ?? new List<string>(),
                SourceFileName = Path.GetFileName(jsonFile),
                CreatedAt = DateTime.UtcNow
            };

            await collection.InsertOneAsync(mongoDoc);
            insertedCount++;
            processedFiles++;

            if (processedFiles % 100 == 0)
            {
                Console.WriteLine($"  Progress: {processedFiles}/{totalFiles} files processed, {insertedCount} inserted, {skippedCount} skipped, {errorCount} errors");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Error processing {Path.GetFileName(jsonFile)}: {ex.Message}");
            errorCount++;
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== Import Summary ===");
Console.WriteLine($"Total files found: {totalFiles}");
Console.WriteLine($"Successfully processed: {processedFiles}");
Console.WriteLine($"Documents inserted: {insertedCount}");
Console.WriteLine($"Documents skipped (already exist): {skippedCount}");
Console.WriteLine($"Errors: {errorCount}");

// Get final count from MongoDB
var finalCount = await collection.CountDocumentsAsync(FilterDefinition<Jpg6DataMongo>.Empty);
Console.WriteLine($"Total documents in collection: {finalCount}");

// JSON data structure for deserialization
public class Jpg6JsonData
{
    [System.Text.Json.Serialization.JsonPropertyName("handles")]
    public Jpg6JsonHandles? Handles { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("jpg6Urls")]
    public List<string>? Jpg6Urls { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string? Title { get; set; }
}

public class Jpg6JsonHandles
{
    [System.Text.Json.Serialization.JsonPropertyName("fanfix")]
    public List<string>? Fanfix { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("fanflix")]
    public List<string>? Fanflix { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("instagram")]
    public List<string>? Instagram { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("onlyfans")]
    public List<string>? Onlyfans { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("other")]
    public List<string>? Other { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("tiktok")]
    public List<string>? Tiktok { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("youtube")]
    public List<string>? Youtube { get; set; }
}

