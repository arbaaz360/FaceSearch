# Embedder Scaling & GPU Acceleration Guide

This guide explains how to scale the embedder service across multiple instances and ensure GPU acceleration is enabled.

## Overview

The embedder service can be scaled horizontally by running multiple instances on different ports. The .NET clients automatically load-balance requests across all available instances.

## Quick Start

### Option 1: Start Multiple Instances (Recommended)

```bash
cd embedder
start-multiple.bat 3
```

This starts 3 embedder instances on ports 8090, 8091, and 8092.

### Option 2: Start Single Instance (Default)

```bash
cd embedder
start.bat
```

This starts a single instance on port 8090.

## Configuration

### Multiple Embedder Instances

Update `appsettings.json` in both `FaceSearch` and `Workers.Indexer`:

```json
{
  "Embedder": {
    "BaseUrls": [
      "http://localhost:8090",
      "http://localhost:8091",
      "http://localhost:8092"
    ],
    "LoadBalancingStrategy": "round-robin",
    "TimeoutSeconds": 30,
    "MaxRetries": 3
  }
}
```

**Load Balancing Strategies:**
- `round-robin` (default): Distributes requests sequentially across instances
- `random`: Randomly selects an instance for each request

### Single Instance (Backward Compatible)

If you only specify `BaseUrl`, it will use a single instance:

```json
{
  "Embedder": {
    "BaseUrl": "http://localhost:8090"
  }
}
```

## GPU Acceleration

The embedder automatically detects and uses GPU acceleration:

### CUDA (NVIDIA GPUs)

1. Ensure CUDA is installed
2. Set environment variable: `set CLIP_DEVICE=cuda`
3. The embedder will use `onnxruntime-gpu` for InsightFace and PyTorch CUDA for CLIP

### DirectML (Windows AMD/Intel GPUs)

1. DirectML is the default on Windows
2. Set environment variable: `set CLIP_DEVICE=dml`
3. The embedder will use `torch-directml` for CLIP and `onnxruntime-gpu` for InsightFace

### CPU Fallback

If no GPU is available or `CLIP_DEVICE=cpu` is set, the embedder will use CPU.

## Performance Tips

1. **Multiple Instances**: Start 2-4 instances for better throughput
2. **GPU Acceleration**: Always use GPU if available (much faster)
3. **Worker Parallelism**: Increase `Parallelism` in `Workers.Indexer/appsettings.json` to match your embedder capacity:
   ```json
   {
     "Indexer": {
       "Parallelism": 16  // Increase if you have multiple embedder instances
     }
   }
   ```

## Verifying GPU Usage

Check the embedder logs or visit:
- `http://localhost:8090/_gpu` - GPU status
- `http://localhost:8090/_status` - Service status

You should see device information like:
```json
{
  "clip_device": "cuda:0 (NVIDIA GeForce RTX 3090)",
  "face_device": "CUDAExecutionProvider"
}
```

## Troubleshooting

### Instances Not Starting

- Check if ports are already in use: `netstat -an | findstr "8090"`
- Ensure Python 3.11 is installed
- Check `embedder.log` and `embedder.err.log` files

### GPU Not Detected

- Verify GPU drivers are installed
- Check CUDA installation: `nvcc --version`
- Try setting `CLIP_DEVICE=cuda` explicitly

### Load Balancing Not Working

- Ensure `BaseUrls` array is configured (not just `BaseUrl`)
- Check that all instances are running on the specified ports
- Verify logs show "LoadBalancedEmbedderClient initialized with X embedder instance(s)"

