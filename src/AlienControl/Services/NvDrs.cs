using System;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// beta1.3-ov.11: the NVIDIA driver's own frame rate cap and V-Sync, per game, through the driver settings database
/// (NVAPI DRS, the same profiles the NVIDIA app, the old Control Panel and Profile Inspector edit). Nothing is injected
/// and nothing keeps running: each call opens a session, reads or writes, saves and closes (nvapi64.dll ships with the
/// driver). Writing needs admin, which the app already has.
///
/// Settings (NvApiDriverSettings.h): FRL_FPS_ID 0x10835002 is "Max Frame Rate" (0 = off, 20..1000 in the UI),
/// VSYNCMODE_ID 0x00A879CF with VSYNCMODE_FORCEON 0x47814940 is "Vertical sync: On". Off removes our value, so the
/// profile goes back to whatever NVIDIA predefined. The game's profile is found by its exe; a game NVIDIA doesn't know
/// gets a small user profile "Alien Controls: game.exe". Scope ALL writes the base (global) profile.
/// The driver reads a profile when the game starts, so a change is for the next launch.
/// </summary>
public static class NvDrs
{
    public const uint FrlId = 0x10835002, VsyncId = 0x00A879CF, VsyncForceOn = 0x47814940;
    public const int MinFps = 20, MaxFps = 1000;

    /// <summary>What the driver has for a game (or globally): fps 0 = no cap. Own = the game has a profile of its own
    /// (false: the values shown are the global ones it falls back to).</summary>
    public readonly record struct State(int Fps, bool Vsync, bool Own);

    // NvAPI_QueryInterface ids (nvapi_interface.h)
    private const uint IdInitialize = 0x0150E828, IdCreateSession = 0x0694D52E, IdDestroySession = 0xDAD9CFF8,
        IdLoadSettings = 0x375DBD6B, IdSaveSettings = 0xFCBC7E14, IdFindApp = 0xEEE566B2, IdCreateProfile = 0xCC176068,
        IdFindProfile = 0x7E4A9A0B, IdCreateApp = 0x4347A9DE, IdSetSetting = 0x577DD202, IdGetSetting = 0x73BF8338,
        IdDeleteSetting = 0xE4A26362, IdGetBase = 0xDA8466A0;

    // NvAPI_Status
    private const int Ok = 0, NvidiaDeviceNotFound = -6, InvalidUserPrivilege = -137, SettingNotFound = -160,
        ProfileNotFound = -163, ProfileNameInUse = -164, ExecutableNotFound = -166;

    // struct sizes (nvapi.h): NVDRS_SETTING_V1 is pack(4) = 12320, NVDRS_APPLICATION_V1 = 12296, NVDRS_PROFILE_V1 = 4116
    private const int SettingSize = 12320, SettingIdOff = 4100, SettingTypeOff = 4104, SettingCurOff = 8220;
    private const int AppSize = 12296, AppNameOff = 8, ProfileSize = 4116, ProfileNameOff = 4, UStr = 2048;
    private static uint Ver(int size, int v) => (uint)size | ((uint)v << 16);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryFn(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OutHFn(out IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HFn(IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindAppFn(IntPtr s, IntPtr name, out IntPtr prof, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProfileFn(IntPtr s, IntPtr info, out IntPtr prof);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindProfileFn(IntPtr s, IntPtr name, out IntPtr prof);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateAppFn(IntPtr s, IntPtr prof, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSettingFn(IntPtr s, IntPtr prof, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSettingFn(IntPtr s, IntPtr prof, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteSettingFn(IntPtr s, IntPtr prof, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetBaseFn(IntPtr s, out IntPtr prof);

    private static readonly object Gate = new();
    private static QueryFn? _query;
    private static bool _loadFailed;

    /// <summary>False when nvapi64.dll isn't there (no NVIDIA driver): the card hides its buttons.</summary>
    public static bool Available
    {
        get { lock (Gate) return Load(); }
    }

    private static bool Load()
    {
        if (_query is not null) return true;
        if (_loadFailed) return false;
        try
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib) || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qi))
            { _loadFailed = true; return false; }
            _query = Marshal.GetDelegateForFunctionPointer<QueryFn>(qi);
            return true;
        }
        catch (Exception e) { Log.Warn("NvDrs.Load", e); _loadFailed = true; return false; }
    }

    private static T Fn<T>(uint id) where T : Delegate
    {
        var p = _query!(id);
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"NVAPI 0x{id:X8}");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    /// <summary>Reads the cap and V-Sync for exe (null = global). Error = a short reason, State default then.</summary>
    public static (State State, string? Error) Read(string? exe)
    {
        lock (Gate)
        {
            if (!Load()) return (default, "Needs the NVIDIA driver");
            return Session(false, (s, f) =>
            {
                var (prof, own) = Profile(s, f, exe, create: false);
                if (prof == IntPtr.Zero) return (default(State), "NVIDIA profiles not readable");
                return (new State((int)Get(s, f, prof, FrlId, 0), Get(s, f, prof, VsyncId, 0) == VsyncForceOn, own), (string?)null);
            });
        }
    }

    /// <summary>Writes the cap (fps 0 = remove ours, null = leave) and V-Sync (true = force on, false = remove ours,
    /// null = leave) for exe (null = global). Returns null when saved, otherwise a short reason.</summary>
    public static string? Write(string? exe, int? fps, bool? vsync)
    {
        lock (Gate)
        {
            if (!Load()) return "Needs the NVIDIA driver";
            var (_, err) = Session(true, (s, f) =>
            {
                var (prof, _) = Profile(s, f, exe, create: true);
                if (prof == IntPtr.Zero) return (0, "couldn't make a profile for " + exe);
                if (fps is int v)
                {
                    int r = v <= 0 ? Delete(f, s, prof, FrlId) : Set(f, s, prof, FrlId, (uint)Math.Clamp(v, MinFps, MaxFps));
                    if (r != Ok) return (0, Why(r));
                }
                if (vsync is bool on)
                {
                    int r = on ? Set(f, s, prof, VsyncId, VsyncForceOn) : Delete(f, s, prof, VsyncId);
                    if (r != Ok) return (0, Why(r));
                }
                return (0, (string?)null);
            });
            if (err is null) Log.Info("NvDrs", $"{exe ?? "global"}: cap {(fps is null ? "kept" : fps <= 0 ? "off" : fps.ToString())}, vsync {(vsync is null ? "kept" : vsync == true ? "on" : "default")}");
            return err;
        }
    }

    private sealed class Fns
    {
        public HFn Destroy = null!, LoadS = null!, Save = null!;
        public OutHFn Create = null!;
        public FindAppFn FindApp = null!;
        public CreateProfileFn CreateProfile = null!;
        public FindProfileFn FindProfile = null!;
        public CreateAppFn CreateApp = null!;
        public SetSettingFn SetSetting = null!;
        public GetSettingFn GetSetting = null!;
        public DeleteSettingFn DeleteSetting = null!;
        public GetBaseFn GetBase = null!;
    }

    private static Fns? _fns;

    private static (T, string?) Session<T>(bool save, Func<IntPtr, Fns, (T, string?)> body)
    {
        try
        {
            int r = Fn<VoidFn>(IdInitialize)();
            if (r != Ok) return (default!, Why(r));
            var f = _fns ??= new Fns
            {
                Create = Fn<OutHFn>(IdCreateSession), Destroy = Fn<HFn>(IdDestroySession), LoadS = Fn<HFn>(IdLoadSettings),
                Save = Fn<HFn>(IdSaveSettings), FindApp = Fn<FindAppFn>(IdFindApp), CreateProfile = Fn<CreateProfileFn>(IdCreateProfile),
                FindProfile = Fn<FindProfileFn>(IdFindProfile), CreateApp = Fn<CreateAppFn>(IdCreateApp), SetSetting = Fn<SetSettingFn>(IdSetSetting),
                GetSetting = Fn<GetSettingFn>(IdGetSetting), DeleteSetting = Fn<DeleteSettingFn>(IdDeleteSetting), GetBase = Fn<GetBaseFn>(IdGetBase),
            };
            if ((r = f.Create(out var s)) != Ok) return (default!, Why(r));
            try
            {
                if ((r = f.LoadS(s)) != Ok) return (default!, Why(r));
                var res = body(s, f);
                if (save && res.Item2 is null && (r = f.Save(s)) != Ok) return (default!, Why(r));
                return res;
            }
            finally { f.Destroy(s); }
        }
        catch (EntryPointNotFoundException) { return (default!, "This NVIDIA driver is too old"); }
        catch (Exception e) { Log.Warn("NvDrs", e); return (default!, "NVIDIA driver error"); }
    }

    /// <summary>The profile to use: base for null, the game's own, or (create) a new user profile holding the exe.</summary>
    private static (IntPtr Prof, bool Own) Profile(IntPtr s, Fns f, string? exe, bool create)
    {
        IntPtr prof;
        if (string.IsNullOrEmpty(exe)) return f.GetBase(s, out prof) == Ok ? (prof, true) : (IntPtr.Zero, false);
        var name = UBuf(exe);
        var app = Marshal.AllocHGlobal(AppSize);
        try
        {
            Zero(app, AppSize);
            Marshal.WriteInt32(app, 0, (int)Ver(AppSize, 1));
            int r = f.FindApp(s, name, out prof, app);
            if (r == Ok) return (prof, true);
            if (r != ExecutableNotFound) { Log.Info("NvDrs", $"find {exe}: {r}"); }
            if (!create) return f.GetBase(s, out prof) == Ok ? (prof, false) : (IntPtr.Zero, false);

            // a game NVIDIA doesn't know: our own small profile with just this exe
            var info = Marshal.AllocHGlobal(ProfileSize);
            var pname = UBuf("Alien Controls: " + exe);
            try
            {
                Zero(info, ProfileSize);
                Marshal.WriteInt32(info, 0, (int)Ver(ProfileSize, 1));
                CopyU(pname, info + ProfileNameOff);
                r = f.CreateProfile(s, info, out prof);
                if (r == ProfileNameInUse) r = f.FindProfile(s, pname, out prof);
                if (r != Ok) { Log.Info("NvDrs", $"create profile {exe}: {r}"); return (IntPtr.Zero, false); }
                Zero(app, AppSize);
                Marshal.WriteInt32(app, 0, (int)Ver(AppSize, 1));
                CopyU(name, app + AppNameOff);
                r = f.CreateApp(s, prof, app);
                if (r != Ok) { Log.Info("NvDrs", $"add {exe}: {r}"); return (IntPtr.Zero, false); }
                return (prof, true);
            }
            finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(pname); }
        }
        finally { Marshal.FreeHGlobal(app); Marshal.FreeHGlobal(name); }
    }

    private static uint Get(IntPtr s, Fns f, IntPtr prof, uint id, uint fallback)
    {
        var b = Marshal.AllocHGlobal(SettingSize);
        try
        {
            Zero(b, SettingSize);
            Marshal.WriteInt32(b, 0, (int)Ver(SettingSize, 1));
            int r = f.GetSetting(s, prof, id, b);
            return r == Ok ? (uint)Marshal.ReadInt32(b, SettingCurOff) : fallback;
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    private static int Set(Fns f, IntPtr s, IntPtr prof, uint id, uint value)
    {
        var b = Marshal.AllocHGlobal(SettingSize);
        try
        {
            Zero(b, SettingSize);
            Marshal.WriteInt32(b, 0, (int)Ver(SettingSize, 1));
            Marshal.WriteInt32(b, SettingIdOff, (int)id);
            Marshal.WriteInt32(b, SettingTypeOff, 0);   // NVDRS_DWORD_TYPE
            Marshal.WriteInt32(b, SettingCurOff, (int)value);
            return f.SetSetting(s, prof, b);
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    private static int Delete(Fns f, IntPtr s, IntPtr prof, uint id)
    {
        int r = f.DeleteSetting(s, prof, id);
        return r == SettingNotFound ? Ok : r;   // nothing of ours there: already default
    }

    private static string Why(int r) => r switch
    {
        NvidiaDeviceNotFound => "NVIDIA GPU is off (iGPU only)",
        InvalidUserPrivilege => "needs admin",
        ProfileNotFound => "NVIDIA profile not found",
        _ => $"NVIDIA driver said {r}",
    };

    private static IntPtr UBuf(string s)
    {
        var p = Marshal.AllocHGlobal(UStr * 2);
        Zero(p, UStr * 2);
        var chars = s.AsSpan(0, Math.Min(s.Length, UStr - 1));
        for (int i = 0; i < chars.Length; i++) Marshal.WriteInt16(p, i * 2, chars[i]);
        return p;
    }

    private static void CopyU(IntPtr src, IntPtr dst)
    {
        var tmp = new byte[UStr * 2];
        Marshal.Copy(src, tmp, 0, tmp.Length);
        Marshal.Copy(tmp, 0, dst, tmp.Length);
    }

    private static readonly byte[] Zeros = new byte[4096];

    private static void Zero(IntPtr p, int n)
    {
        for (int o = 0; o < n; o += Zeros.Length) Marshal.Copy(Zeros, 0, p + o, Math.Min(Zeros.Length, n - o));
    }

    /// <summary>The G-SYNC preset: a few frames under the refresh rate so the game stays inside the VRR range
    /// (Blur Busters: at least 3 below, 237 at 240 Hz).</summary>
    public static int GsyncCap(int hz) => Math.Clamp(hz - 3, MinFps, MaxFps);
}
