# FaceSearch Frontend

React frontend for the FaceSearch facial recognition system.

## Setup

1. Install dependencies:
```bash
npm install
```

2. Start development server:
```bash
npm run dev
```

The frontend will run on `http://localhost:3000` and proxy API requests to `http://localhost:5240`.

## Build

To build for production (outputs to `FaceSearch/wwwroot`):

```bash
npm run build
```

The built files will be served by the .NET API as static files.

## Features

- **Albums**: Browse and manage albums with dominant face previews
- **Search**: Text, image, and face search
- **Face Review**: Review and resolve unresolved faces
- **Indexing**: Seed directories for indexing
- **Diagnostics**: System status and diagnostic tools

