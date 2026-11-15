using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MouseProfiles;

/// <summary>
/// Raw Input listener: reports which physical device each event came from, and reads vendor HID reports
/// that never turn into ordinary key or mouse messages. Raw Input can observe but never block; blocking is the hooks' job.
/// </summary>
sealed class RawInputWindow : NativeWindow, IDisposable
{
    readonly Engine engine;
    readonly Dictionary<IntPtr, string> deviceNames = new();
    readonly Dictionary<string, byte[]> lastHid = new();
    RAWDEV[] registered = Array.Empty<RAWDEV>();

    record struct RAWDEV(ushort Page, ushort Usage);

    public RawInputWindow(Engine engine)
    {
        this.engine = engine;
        CreateHandle(new CreateParams());
    }

    /// <summary>
    /// feed: also listen to every keyboard and mouse (high traffic, so only while the setup window is open).
    /// hid: listen to vendor HID interfaces (needed to capture or match "Hid:" signals).
    /// </summary>
    public void Configure(bool feed, bool hid)
    {
        if (registered.Length > 0)
        {
            var remove = registered.Select(d => new Native.RAWINPUTDEVICE { usUsagePage = d.Page, usUsage = d.Usage, dwFlags = Native.RIDEV_REMOVE }).ToArray();
            Native.RegisterRawInputDevices(remove, (uint)remove.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
            registered = Array.Empty<RAWDEV>();
        }
        var wanted = new HashSet<RAWDEV>();
        if (feed) { wanted.Add(new RAWDEV(1, 2)); wanted.Add(new RAWDEV(1, 6)); }
        if (feed || hid)
            foreach (var d in EnumerateHidUsages()) wanted.Add(d);
        if (!feed && !hid) return;
        if (!feed) wanted.RemoveWhere(d => d is { Page: 1, Usage: 2 or 6 });

        var devs = wanted.Select(d => new Native.RAWINPUTDEVICE { usUsagePage = d.Page, usUsage = d.Usage, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = Handle }).ToArray();
        if (devs.Length == 0) return;
        if (Native.RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            registered = wanted.ToArray();
        else
            engine.Log($"Raw Input registration failed (Win32 error {Marshal.GetLastWin32Error()}) - RAW lines will be missing.");
    }

    static IEnumerable<RAWDEV> EnumerateHidUsages()
    {
        uint count = 0, size = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        Native.GetRawInputDeviceList(null, ref count, size);
        if (count == 0) yield break;
        var list = new Native.RAWINPUTDEVICELIST[count];
        Native.GetRawInputDeviceList(list, ref count, size);
        var seen = new HashSet<RAWDEV>();
        for (int i = 0; i < count; i++)
        {
            if (list[i].dwType != Native.RIM_TYPEHID) continue;
            uint len = 0;
            Native.GetRawInputDeviceInfo(list[i].hDevice, Native.RIDI_DEVICEINFO, null, ref len);
            var buf = new byte[Math.Max(len, 32)];
            len = (uint)buf.Length;
            BitConverter.GetBytes(len).CopyTo(buf, 0);
            if (Native.GetRawInputDeviceInfo(list[i].hDevice, Native.RIDI_DEVICEINFO, buf, ref len) == uint.MaxValue) continue;
            var dev = new RAWDEV(BitConverter.ToUInt16(buf, 20), BitConverter.ToUInt16(buf, 22));
            if (dev.Page != 0 && seen.Add(dev)) yield return dev;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT)
        {
            try { ProcessInput(m.LParam); } catch { }
        }
        base.WndProc(ref m);
    }

    void ProcessInput(IntPtr hRaw)
    {
        uint size = 0, headerSize = (uint)(8 + IntPtr.Size * 2);
        Native.GetRawInputData(hRaw, Native.RID_INPUT, null, ref size, headerSize);
        if (size == 0) return;
        var buf = new byte[size];
        if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, headerSize) == uint.MaxValue) return;

        uint type = BitConverter.ToUInt32(buf, 0);
        IntPtr hDevice = new(BitConverter.ToInt64(buf, 8));
        int d = (int)headerSize;

        switch (type)
        {
            case Native.RIM_TYPEMOUSE:
            {
                ushort flags = BitConverter.ToUInt16(buf, d + 4);
                if (flags == 0 || !engine.FeedOn) return; // plain motion
                var parts = new List<string>();
                string[] names = { "Left down", "Left up", "Right down", "Right up", "Middle down", "Middle up", "Button4 down", "Button4 up", "Button5 down", "Button5 up" };
                for (int i = 0; i < names.Length; i++) if ((flags & (1 << i)) != 0) parts.Add(names[i]);
                if ((flags & 0x400) != 0) parts.Add($"Wheel {(short)BitConverter.ToUInt16(buf, d + 6)}");
                if ((flags & 0x800) != 0) parts.Add($"HWheel {(short)BitConverter.ToUInt16(buf, d + 6)}");
                engine.Log($"RAW mouse  [{Short(hDevice)}] {string.Join(", ", parts)}");
                break;
            }
            case Native.RIM_TYPEKEYBOARD:
            {
                if (!engine.FeedOn) return;
                ushort make = BitConverter.ToUInt16(buf, d), kflags = BitConverter.ToUInt16(buf, d + 2), vkey = BitConverter.ToUInt16(buf, d + 6);
                if (vkey == 0xFF) return; // fake key Windows inserts around some sequences
                engine.Log($"RAW kbd    [{Short(hDevice)}] {KeyNames.Name(vkey),-12} {((kflags & 1) != 0 ? "up  " : "down")} scan=0x{make:X2}");
                break;
            }
            case Native.RIM_TYPEHID:
            {
                uint each = BitConverter.ToUInt32(buf, d), count = BitConverter.ToUInt32(buf, d + 4);
                string path = DeviceName(hDevice);
                for (uint i = 0; i < count; i++)
                {
                    var report = new byte[each];
                    Array.Copy(buf, d + 8 + i * each, report, 0, each);
                    // Skip repeats and the all-zero "released" reports; the press is the interesting one.
                    if (lastHid.TryGetValue(path, out var prev) && prev.AsSpan().SequenceEqual(report)) continue;
                    lastHid[path] = report;
                    if (report.Skip(1).All(b => b == 0)) continue;
                    engine.Log($"RAW hid    [{Signal.ShortDevice(path)}] report {Convert.ToHexString(report)}");
                    engine.OnHid(path, report);
                }
                break;
            }
        }
    }

    string DeviceName(IntPtr hDevice)
    {
        if (deviceNames.TryGetValue(hDevice, out var name)) return name;
        uint chars = 0;
        Native.GetRawInputDeviceInfoName(hDevice, Native.RIDI_DEVICENAME, null, ref chars);
        name = "(unknown)";
        if (chars > 0)
        {
            var buf = new char[chars];
            if (Native.GetRawInputDeviceInfoName(hDevice, Native.RIDI_DEVICENAME, buf, ref chars) != uint.MaxValue)
                name = new string(buf).TrimEnd('\0');
        }
        return deviceNames[hDevice] = name;
    }

    string Short(IntPtr hDevice) => Signal.ShortDevice(DeviceName(hDevice));

    public void Dispose()
    {
        Configure(false, false);
        DestroyHandle();
    }
}
