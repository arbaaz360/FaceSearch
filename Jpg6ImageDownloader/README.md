# JPG6 Image Downloader

This console application extracts and downloads images from URLs stored in the MongoDB `jpg6_data` collection. It handles both direct image URLs and images embedded in web pages.

## Features

- **Smart Image Extraction**: Automatically detects if a URL is a direct image or a web page containing images
- **HTML Parsing**: Extracts images from HTML pages using multiple strategies:
  - Standard `<img src="">` tags
  - Lazy-loaded images (`data-src`, `data-lazy-src`)
  - `<picture>` and `<source>` tags with `srcset`
- **Size Filtering**: Only downloads images that are 255px or larger (width or height)
- **Efficient Downloading**: Downloads each image only once (stores bytes during dimension check)
- **Organized Storage**: Saves images in folders organized by document title
- **Retry Logic**: Automatically retries failed requests with exponential backoff
- **Progress Tracking**: Shows progress updates and summary statistics

## Prerequisites

- .NET 8.0 SDK
- MongoDB running on `mongodb://127.0.0.1:27017`
- Database: `facesearch` with `jpg6_data` collection (populated by Jpg6Importer)

## Usage

### Option 1: Using the batch file
```bash
cd Jpg6ImageDownloader
run-download.bat
```

### Option 2: Using dotnet CLI
```bash
cd Jpg6ImageDownloader
dotnet build
dotnet run
```

## Configuration

You can modify these settings in `Program.cs`:

```csharp
var connectionString = "mongodb://127.0.0.1:27017";
var databaseName = "facesearch";
var collectionName = "jpg6_data";
var outputFolder = Path.Combine(Directory.GetCurrentDirectory(), "test");
var minSize = 255; // Minimum dimension (width or height)
```

## Output Structure

Images are saved in the following structure:
```
test/
  ├── Demi_Rose_Mawby/
  │   ├── image1.jpg
  │   ├── image2.png
  │   └── ...
  ├── Another_Title/
  │   └── ...
  └── ...
```

Each document's images are saved in a folder named after the document's title (sanitized for filesystem compatibility).

## How It Works

1. **Fetches all documents** from the `jpg6_data` MongoDB collection
2. **For each URL** in `jpg6Urls`:
   - First tries to fetch as a direct image
   - If not an image, fetches as HTML and parses for embedded images
   - Checks image dimensions (width or height must be >= 255px)
   - Downloads and saves qualifying images
3. **Organizes images** by document title in separate folders
4. **Skips duplicates** (if an image file already exists, it won't be re-downloaded)

## Supported Image Formats

- JPEG/JPG
- PNG
- GIF
- WebP
- BMP

## Error Handling

- Failed URLs are logged but don't stop the process
- Invalid image URLs are skipped
- Network errors trigger automatic retries (3 attempts with exponential backoff)
- Images that are too small are skipped with a log message

## Performance Notes

- Images are downloaded once during dimension checking, then saved if they meet size requirements
- Progress is reported every 50 URLs processed
- The process can be interrupted and resumed (already downloaded images won't be re-downloaded)

