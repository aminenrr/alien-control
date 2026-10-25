using System;
using System.IO;
using System.Reflection;

namespace AlienControl.Overlay;

/// <summary>
/// beta3.6: the Metal level's font. Apple's HUD uses SF Mono, which Apple only licenses for Apple platforms, so the app
/// bundles JetBrains Mono NL (SIL OFL, no ligatures so "&gt;=" stays two characters), SemiBold and Bold. The files ride in
/// the exe and are copied once to %LocalAppData%\AlienControl\Fonts, where DirectWrite and WPF load them privately
/// (nothing is installed system wide). Cascadia Mono / Consolas stay as the fallback.
/// </summary>
internal static class MonoFont
{
    public const string Family = "JetBrains Mono NL";
    public static readonly string[] Files = { "JetBrainsMonoNL-SemiBold.ttf", "JetBrainsMonoNL-Bold.ttf" };

    private static readonly Lazy<string?> _dir = new(Extract);

    /// <summary>The folder holding the font files, or null when they couldn't be written (then the fallback is used).</summary>
    public static string? Dir => _dir.Value;

    private static string? Extract()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlienControl", "Fonts");
            Directory.CreateDirectory(dir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in Files)
            {
                using var s = asm.GetManifestResourceStream("AlienControl.Fonts." + name);
                if (s == null) return null;
                string path = Path.Combine(dir, name);
                if (File.Exists(path) && new FileInfo(path).Length == s.Length) continue;
                string tmp = path + ".tmp";
                using (var f = File.Create(tmp)) s.CopyTo(f);
                File.Move(tmp, path, true);
            }
            return dir;
        }
        catch { return null; }
    }
}
