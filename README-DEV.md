# Development Workflow - Hot Reload

## Quick Start

### For Development (Hot Reload Enabled)
```bash
start-dev.bat
```

This starts all services with **hot reload** enabled:
- ✅ **Frontend**: Changes appear instantly (Vite HMR)
- ✅ **API**: Auto-restarts when you save `.cs` files
- ✅ **Worker**: Auto-restarts when you save `.cs` files
- ⚠️ **Embedder**: Manual restart needed (Python)

### For Production/Testing (No Auto-Reload)
```bash
start-all.bat
```

## How It Works

### Frontend (React + Vite)
- **No restart needed!** Just save your `.jsx` or `.js` files
- Changes appear instantly in the browser (Hot Module Replacement)
- The browser automatically refreshes when needed

### Backend (.NET API & Worker)
- Uses `dotnet watch` instead of `dotnet run`
- Automatically rebuilds and restarts when you save `.cs` files
- You'll see "watch : File changed: ..." in the console
- Wait a few seconds for the restart to complete

### Python Embedder
- Manual restart required (Ctrl+C in the embedder window, then restart)
- Or just restart the embedder service separately

## Workflow Tips

1. **Start once**: Run `start-dev.bat` at the beginning of your session
2. **Just code**: Make changes and save - services auto-reload
3. **Frontend changes**: See them instantly, no refresh needed
4. **Backend changes**: Wait 2-5 seconds after saving for auto-restart
5. **Check console**: Watch the service windows for restart confirmations

## Troubleshooting

### API/Worker not restarting?
- Make sure you're using `start-dev.bat` (not `start-all.bat`)
- Check the console window for errors
- Try manually stopping (Ctrl+C) and restarting that service

### Frontend not updating?
- Check the browser console for errors
- Hard refresh: Ctrl+Shift+R or Ctrl+F5
- Check that Vite is running (should see "VITE" in the frontend window)

### Changes not taking effect?
- Wait a few seconds for auto-restart to complete
- Check the service console for build errors
- Make sure you saved the file (Ctrl+S)

