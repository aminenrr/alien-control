using System;
using System.IO;

namespace AlienControl.Services;

/// <summary>Full System32 paths for the Windows tools the app runs. The app runs as admin, so it never resolves a
/// bare name like "pnputil.exe" through the search path (a planted exe next to the app or in PATH can't be picked up).</summary>
public static class SysExe
{
    public static string Get(string name) => Path.Combine(Environment.SystemDirectory, name);
}
