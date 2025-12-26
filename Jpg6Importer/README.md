# JPG6 Data Importer

This console application imports JSON files from the jpg6-bulk directory structure into MongoDB.

## Prerequisites

- .NET 8.0 SDK
- MongoDB running on `mongodb://127.0.0.1:27017`
- Database: `facesearch`
- Collection: `jpg6_data` (created automatically)

## Directory Structure

The importer expects JSON files in the following structure:
```
C:\Users\ASUS\Downloads\simpscrapped\jpg6-bulk\
  ├── page-1\
  │   ├── jpg6-data-1-Demi Rose Mawby.json
  │   └── ...
  ├── page-2\
  │   └── ...
  └── ...
```

## Usage

### Option 1: Using the batch file
```bash
cd Jpg6Importer
run-import.bat
```

### Option 2: Using dotnet CLI
```bash
cd Jpg6Importer
dotnet build
dotnet run
```

### Option 3: Update path in code
If your JSON files are in a different location, edit `Program.cs` and update the `baseDirectory` variable:
```csharp
var baseDirectory = @"C:\Users\ASUS\Downloads\simpscrapped\jpg6-bulk";
```

## Features

- Reads all JSON files from page-1 through page-912 subdirectories
- Parses each JSON file and maps it to MongoDB documents
- Skips duplicate documents (based on title or source filename)
- Provides progress updates every 100 files
- Shows summary statistics after completion

## MongoDB Collection

The data is stored in the `jpg6_data` collection with the following structure:
- `_id`: Unique identifier (GUID)
- `Title`: Title from JSON file
- `Handles`: Object containing social media handles (fanfix, fanflix, instagram, onlyfans, other, tiktok, youtube)
- `Jpg6Urls`: Array of URLs
- `SourceFileName`: Original JSON filename
- `CreatedAt`: Timestamp when imported

