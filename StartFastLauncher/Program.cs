using System.Diagnostics;
using System.IO;

var scriptDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var batPath = Path.Combine(scriptDir, "start-fast.bat");
if (!File.Exists(batPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"start-fast.bat not found at {batPath}");
    Console.ResetColor();
    return 1;
}

Console.WriteLine("Launching start-fast.bat ...");
var psi = new ProcessStartInfo
{
    FileName = "cmd.exe",
    Arguments = $"/c \"\"{batPath}\"\"",
    UseShellExecute = true,
    WorkingDirectory = scriptDir
};
try
{
    Process.Start(psi);
    Console.WriteLine("Started. You can close this window.");
    return 0;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Failed to launch: " + ex.Message);
    Console.ResetColor();
    return 1;
}
