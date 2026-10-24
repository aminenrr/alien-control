using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using AlienControl.Services;
using Xunit;

namespace AlienControl.Tests;

/// <summary>2.0.0-beta1.4 (build review #2 and #15): repo paths and an STA runner for the WPF tests.</summary>
internal static class Repo
{
    /// <summary>The checkout root: the first parent of the test binaries that holds src/AlienControl/AlienControl.csproj.</summary>
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "AlienControl", "AlienControl.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("repo root (src/AlienControl/AlienControl.csproj) not found above " + AppContext.BaseDirectory);
    }

    public static string Src => Path.Combine(Root, "src", "AlienControl");
    public static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));

    public static IEnumerable<string> Files(string ext) =>
        Directory.EnumerateFiles(Src, "*" + ext, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

    public static readonly string[] ThemeFiles = { "Themes/Legend.xaml", "Themes/Legend2.xaml" };

    /// <summary>Runs the action on a new STA thread (WPF needs one) and rethrows what it threw.</summary>
    public static void Sta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw new Exception("STA test failed: " + err.Message, err);
    }

    public static readonly Regex Key = new("x:Key=\"([^\"{]+)\"");
    public static readonly Regex XamlRef = new(@"\{(?:StaticResource|DynamicResource)\s+([^}\s]+)\s*\}");
    public static readonly Regex CsRef = new("(?:SetResourceReference\\([^,]+,\\s*|TryFindResource\\(|FindResource\\(|Resources\\[)\"([^\"]+)\"");
    public static readonly Regex IconLit = new("\"(Icon\\.[A-Za-z0-9]+)\"");
}

/// <summary>
/// 2.0.0-beta1.4 (build review #2): the beta1 startup crash (a duplicate Card2 key in Legend2.xaml) was only found on the
/// laptop. These load the two theme dictionaries the way App.ApplyTheme does and check every key the XAML and C# use.
/// One test method does all WPF work, on one STA thread with one Application (there is one per process).
/// </summary>
public class ThemeTests
{
    private static Dictionary<string, List<string>> DeclaredInThemes()
    {
        var d = new Dictionary<string, List<string>>();
        foreach (var f in Repo.ThemeFiles)
            foreach (Match m in Repo.Key.Matches(Repo.Read("src", "AlienControl", f)))
            {
                if (!d.TryGetValue(m.Groups[1].Value, out var l)) d[m.Groups[1].Value] = l = new List<string>();
                l.Add(f);
            }
        return d;
    }

    [Fact]
    public void No_key_is_declared_twice_in_the_themes()
    {
        var dup = DeclaredInThemes().Where(kv => kv.Value.Count > 1).Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value)})").ToList();
        Assert.True(dup.Count == 0, "duplicate theme keys: " + string.Join("; ", dup));
    }

    [Fact]
    public void Each_xaml_file_declares_a_key_once()
    {
        var bad = new List<string>();
        foreach (var f in Repo.Files(".xaml"))
            bad.AddRange(Repo.Key.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)
                .GroupBy(k => k).Where(g => g.Count() > 1).Select(g => $"{Path.GetFileName(f)}: {g.Key}"));
        Assert.True(bad.Count == 0, "duplicate x:Key: " + string.Join("; ", bad));
    }

    [Fact]
    public void Themes_load_and_every_referenced_key_resolves()
    {
        // what the XAML and C# reference
        var refs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Repo.Files(".xaml"))
            foreach (Match m in Repo.XamlRef.Matches(File.ReadAllText(f))) refs.Add(m.Groups[1].Value);
        var icons = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Repo.Files(".cs"))
        {
            var t = File.ReadAllText(f);
            foreach (Match m in Repo.CsRef.Matches(t)) refs.Add(m.Groups[1].Value);
            foreach (Match m in Repo.IconLit.Matches(t)) icons.Add(m.Groups[1].Value);
        }
        foreach (TrayProfile p in Enum.GetValues(typeof(TrayProfile))) icons.Add(Profiles.IconKey(p));
        Assert.NotEmpty(refs);
        Assert.Contains("Icon.GMode", icons);

        // MainWindow.xaml's own resources count too
        var local = new HashSet<string>(Repo.Key.Matches(Repo.Read("src", "AlienControl", "MainWindow.xaml")).Select(m => m.Groups[1].Value));

        Repo.Sta(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var md = app.Resources.MergedDictionaries;
            md.Clear();
            foreach (var f in Repo.ThemeFiles)   // same order as App.ApplyTheme (Legend2 uses StaticResource keys from Legend)
                md.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AlienControl;component/" + f, UriKind.Absolute) });

            var missing = refs.Where(k => !local.Contains(k) && app.TryFindResource(k) is null).ToList();
            Assert.True(missing.Count == 0, "keys referenced but not in Legend.xaml / Legend2.xaml: " + string.Join(", ", missing));

            var noIcon = icons.Where(k => app.TryFindResource(k) is null).ToList();
            Assert.True(noIcon.Count == 0, "icon keys missing: " + string.Join(", ", noIcon));

            // every declared key really loads (a broken style or brush throws on first lookup)
            foreach (var k in DeclaredInThemes().Keys) Assert.NotNull(app.TryFindResource(k));

            // 2.0.0-beta1.4 (Amine): the XAML defaults of the tint brushes are the default cyan's tints, so the first
            // frame (before ApplyAccent) matches what ApplyAccent writes
            var (sel, pill) = MainWindow.AccentTints(Color.FromRgb(0, 229, 255));
            Assert.Equal(sel, ((SolidColorBrush)app.TryFindResource("AccentSelBg")).Color);
            Assert.Equal(pill, ((SolidColorBrush)app.TryFindResource("AccentPillBg")).Color);
        });
    }
}

/// <summary>2.0.0-beta1.4 (build review #15): cheap checks on the UI text, assets and release files.</summary>
public class UiAndReleaseTests
{
    [Fact]
    public void No_em_or_en_dashes_in_ui_xaml()
    {
        var bad = new List<string>();
        foreach (var f in Repo.Files(".xaml"))
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].IndexOf('\u2013') >= 0 || lines[i].IndexOf('\u2014') >= 0) bad.Add($"{Path.GetFileName(f)}:{i + 1}");
        }
        Assert.True(bad.Count == 0, "em / en dash in: " + string.Join(", ", bad));
    }

    [Fact]
    public void No_em_or_en_dashes_in_cs_string_literals()
    {
        var lit = new Regex("\"(?:[^\"\\\\\\n]|\\\\.)*\"");
        var bad = new List<string>();
        foreach (var f in Repo.Files(".cs"))
            foreach (Match m in lit.Matches(File.ReadAllText(f)))
                if (m.Value.IndexOf('\u2013') >= 0 || m.Value.IndexOf('\u2014') >= 0) bad.Add($"{Path.GetFileName(f)}: {m.Value}");
        Assert.True(bad.Count == 0, "em / en dash in: " + string.Join("; ", bad));
    }

    [Fact]
    public void Each_swatch_shows_the_colour_it_applies()
    {
        // build review #9: the green swatch showed a different green than the one it sent to the keyboard
        var doc = XDocument.Parse(Repo.Read("src", "AlienControl", "MainWindow.xaml"));
        int n = 0;
        foreach (var el in doc.Descendants())
        {
            var bg = (string?)el.Attribute("Background");
            var tag = (string?)el.Attribute("Tag");
            if ((string?)el.Attribute("Style") != "{DynamicResource Swatch2}" || bg is null || tag is null || !bg.StartsWith("#")) continue;
            var rgb = tag.Split(',').Select(byte.Parse).ToArray();
            Assert.Equal($"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}", bg.ToUpperInvariant());
            n++;
        }
        Assert.Equal(10, n);   // 5 colours on Home and 5 on Lights (the custom swatch has no Tag)
    }

    [Fact]
    public void Bundled_wavs_load()
    {
        // build review #15: every chime the packs play is a 16-bit PCM WAV the defensive reader accepts, read from the
        // compiled WPF resources (what Sounds plays through pack://application:,,,/Assets/Sounds/...)
        var asm = typeof(Settings).Assembly;
        using var s = asm.GetManifestResourceStream(asm.GetName().Name + ".g.resources");
        Assert.NotNull(s);
        using var r = new System.Resources.ResourceReader(s!);
        var wavs = new List<string>();
        foreach (DictionaryEntry e in r)
        {
            var name = (string)e.Key;
            if (!name.StartsWith("assets/sounds/") || !name.EndsWith(".wav")) continue;
            using var ms = new MemoryStream();
            ((Stream)e.Value!).CopyTo(ms);
            var w = Sounds.ReadWav(ms.ToArray());
            Assert.True(w is { dataBytes: > 0 }, name + " does not load");
            wavs.Add(name);
        }
        var onDisk = Directory.GetFiles(Path.Combine(Repo.Src, "Assets", "Sounds"), "*.wav", SearchOption.AllDirectories).Length;
        Assert.True(onDisk > 0);
        Assert.Equal(onDisk, wavs.Count);
    }

    private static string Version()
    {
        var proj = XDocument.Parse(Repo.Read("src", "AlienControl", "AlienControl.csproj"));
        return proj.Descendants("InformationalVersion").Select(e => e.Value.Trim()).First();
    }

    [Fact]
    public void Version_has_a_changelog_entry_and_release_notes()
    {
        // build review #13: release.yml checks the tag against this version and reads docs/releases/<tag>.md
        var v = Version();
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", v);
        var log = Repo.Read("docs", "CHANGELOG.md");
        var first = log.Split('\n').First(l => l.StartsWith("## v"));
        Assert.StartsWith($"## v{v} ", first + " ");
        var notes = Path.Combine(Repo.Root, "docs", "releases", $"v{v}.md");
        Assert.True(File.Exists(notes), notes + " is missing");
        Assert.Contains("What to test", File.ReadAllText(notes));
    }
}

/// <summary>2.0.0-beta1.4 (services review #17, #18): settings keep keys they don't know, and AccentTints math.</summary>
public class SettingsAndAccentTests
{
    [Fact]
    public void Round_trip_keeps_unknown_keys()
    {
        var s = Settings.Parse("{\"SettingsVersion\":7,\"F3Opens\":false,\"OverlayCorner\":\"TopLeft\",\"Overlay\":{\"Rows\":[\"Fps\",\"Cpu\"]}}")!;
        Assert.False(s.F3Opens);
        Assert.NotNull(s.Extra);
        var json = JsonSerializer.Serialize(s);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("TopLeft", doc.RootElement.GetProperty("OverlayCorner").GetString());
        Assert.Equal("Cpu", doc.RootElement.GetProperty("Overlay").GetProperty("Rows")[1].GetString());
        Assert.False(doc.RootElement.TryGetProperty("Extra", out _));   // flattened, not a nested "Extra" object

        var again = Settings.Parse(json)!;
        Assert.Equal(JsonSerializer.Serialize(s), JsonSerializer.Serialize(again));
    }

    [Fact]
    public void Round_trip_keeps_every_known_key()
    {
        var a = JsonSerializer.Serialize(Settings.Parse("{\"SettingsVersion\":7}")!);
        var b = JsonSerializer.Serialize(Settings.Parse(a)!);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Newer_settings_version_is_kept()
    {
        var s = Settings.Parse("{\"SettingsVersion\":9,\"F3Opens\":false}")!;
        Assert.Equal(9, s.SettingsVersion);
    }

    [Fact]
    public void Tints_of_the_default_cyan()
    {
        var (sel, pill) = MainWindow.AccentTints(Color.FromRgb(0, 229, 255));
        Assert.Equal(Color.FromRgb(0x0C, 0x32, 0x39), sel);    // 16 % of cyan over the card #0E1013
        Assert.Equal(Color.FromRgb(0x09, 0x3D, 0x45), pill);   // 22 % over the pill track #0B0D10
    }

    [Fact]
    public void Black_darkens_the_card_colours()
    {
        // black has no saturation, so it gets 60 % strength: 9.6 % of black over #0E1013, 13.2 % over #0B0D10
        var (sel, pill) = MainWindow.AccentTints(Color.FromRgb(0, 0, 0));
        Assert.Equal(Color.FromRgb(13, 14, 17), sel);
        Assert.Equal(Color.FromRgb(10, 11, 14), pill);
    }

    [Fact]
    public void White_is_a_weaker_neutral_grey()
    {
        var (sel, pill) = MainWindow.AccentTints(Color.FromRgb(255, 255, 255));
        Assert.Equal(Color.FromRgb(37, 39, 42), sel);    // 60 % strength (9.6 %) of white over #0E1013
        Assert.Equal(Color.FromRgb(43, 45, 48), pill);   // 13.2 % of white over #0B0D10
        // weaker than a saturated colour at full strength
        var (red, _) = MainWindow.AccentTints(Color.FromRgb(255, 77, 106));
        Assert.True(red.R > sel.R);
    }

    [Theory]
    [InlineData(124, 77, 255)]
    [InlineData(255, 77, 106)]
    [InlineData(60, 255, 122)]
    public void Tints_stay_dark_and_lean_to_the_colour(byte r, byte g, byte b)
    {
        var (sel, pill) = MainWindow.AccentTints(Color.FromRgb(r, g, b));
        foreach (var c in new[] { sel, pill })
            Assert.True(c.R < 80 && c.G < 80 && c.B < 80, $"{c} is too bright for white text");
        // the strongest channel of the colour is the strongest channel of the tint
        int max = Math.Max(r, Math.Max(g, b));
        byte Ch(Color c) => max == r ? c.R : max == g ? c.G : c.B;
        Assert.True(Ch(sel) >= Math.Max(sel.R, Math.Max(sel.G, sel.B)));
    }
}
