using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HonorPowerRefresh
{
    internal static class Native
    {
        internal const string HonorDll = @"C:\Program Files\HONOR\OSD\HardwareHal.dll";

        internal const int DM_DISPLAYFREQUENCY = 0x400000;
        internal const int CDS_TEST = 0x00000002;
        internal const int DISP_CHANGE_SUCCESSFUL = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public short dmOrientation, dmPaperSize, dmPaperLength, dmPaperWidth;
            public short dmScale, dmCopies, dmDefaultSource, dmPrintQuality;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
            public int dmPanningWidth, dmPanningHeight;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettingsEx(string device, int mode, ref DEVMODE devMode, int flags);

        // Windows standard path: set dmDisplayFrequency directly in one call.
        // flags: CDS_TEST probes without applying; 0 applies a dynamic (non-persisted) mode change.
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

        // HONOR private export, kept only as a fallback. Interface behaviour notes:
        //   * both ref parameters are OUTPUTS (afterHz, beforeHz); the DLL never reads caller input;
        //   * each call rotates to the next HIGHER hard-coded rate for this model, wrapping to the lowest.
        // So it cannot be given a target Hz, which is why the old code had to poll.
        [DllImport(HonorDll, EntryPoint = "ChangeFrequency", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ChangeFrequency(ref int afterHz, ref int beforeHz);
    }

    internal enum SwitchMethod
    {
        Direct,
        HonorHal
    }

    internal static class Program
    {
        private static bool logging;
        private static string logPath;

        // Targets come from HonorPowerRefresh.config.ini; these are the built-in fallbacks.
        private const int DefaultAcHz = 120;
        private const int DefaultBatteryHz = 60;
        private static int acHz = DefaultAcHz;
        private static int batteryHz = DefaultBatteryHz;
        private static SwitchMethod method = SwitchMethod.Direct;

        [STAThread]
        private static int Main(string[] args)
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(directory, "HonorPowerRefresh.log");
            string config = Path.Combine(directory, "HonorPowerRefresh.config.ini");
            LoadConfig(config);
            Log("Config: AC=" + acHz + " Hz, battery=" + batteryHz + " Hz, method=" + method + ", source=" + config);
            try
            {
                Thread.Sleep(750);
                Sync();
                return 0;
            }
            catch (Exception ex)
            {
                Log("Failure: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        // Read the runtime config next to the exe. Unknown keys and comment lines are ignored;
        // a missing file or an invalid Hz value falls back to the built-in defaults.
        private static void LoadConfig(string path)
        {
            bool enableLogging = false;
            int ac = DefaultAcHz, battery = DefaultBatteryHz;
            SwitchMethod selected = SwitchMethod.Direct;
            List<string> warnings = new List<string>();
            if (File.Exists(path))
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key.Equals("EnableLogging", StringComparison.OrdinalIgnoreCase))
                        enableLogging = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("AcRefreshRate", StringComparison.OrdinalIgnoreCase))
                        ac = ParseHz(value, DefaultAcHz, key, warnings);
                    else if (key.Equals("BatteryRefreshRate", StringComparison.OrdinalIgnoreCase))
                        battery = ParseHz(value, DefaultBatteryHz, key, warnings);
                    else if (key.Equals("SwitchMethod", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Equals("HonorHal", StringComparison.OrdinalIgnoreCase)) selected = SwitchMethod.HonorHal;
                        else if (value.Equals("Direct", StringComparison.OrdinalIgnoreCase)) selected = SwitchMethod.Direct;
                        else warnings.Add("Config: unknown SwitchMethod='" + value + "', using Direct.");
                    }
                }
            }
            logging = enableLogging;
            acHz = ac;
            batteryHz = battery;
            method = selected;
            foreach (string warning in warnings) Log(warning);
        }

        private static int ParseHz(string value, int fallback, string key, List<string> warnings)
        {
            int hz;
            if (int.TryParse(value, out hz) && hz >= 1 && hz <= 1000) return hz;
            warnings.Add("Config: invalid " + key + "='" + value + "', using default " + fallback + " Hz.");
            return fallback;
        }

        private static void Log(string message)
        {
            if (logging)
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }

        private static void Sync()
        {
            Native.SYSTEM_POWER_STATUS power;
            if (!Native.GetSystemPowerStatus(out power)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (power.ACLineStatus > 1) { Log("Power source unknown; skipped."); return; }
            int target = power.ACLineStatus == 1 ? acHz : batteryHz;
            Native.DEVMODE current = ReadCurrent();
            Log((power.ACLineStatus == 1 ? "AC" : "battery") + ": current " + current.dmDisplayFrequency + " Hz; target " + target + " Hz.");
            if (current.dmDisplayFrequency == target) return;

            HashSet<int> rates = AvailableRates(current);
            if (!rates.Contains(target)) { Log(target + " Hz unavailable at current resolution."); return; }

            if (method == SwitchMethod.Direct && ApplyDirect(current, target)) return;

            if (!File.Exists(Native.HonorDll))
            {
                if (method == SwitchMethod.HonorHal)
                    throw new FileNotFoundException("HONOR HardwareHal.dll was not found.", Native.HonorDll);
                Log("Direct path unavailable and HONOR HardwareHal.dll missing; nothing left to try.");
                return;
            }
            if (method == SwitchMethod.Direct) Log("Direct switch failed or unverified; falling back to HONOR HAL.");
            ApplyViaHonorHal(current, target, rates.Count);
        }

        // One Windows call instead of a rotate-and-poll loop. CDS_TEST first avoids applying a
        // mode the driver would reject; a short readback loop only waits for the mode change to settle.
        private static bool ApplyDirect(Native.DEVMODE current, int target)
        {
            Native.DEVMODE desired = current;
            desired.dmFields |= Native.DM_DISPLAYFREQUENCY;
            desired.dmDisplayFrequency = target;

            int test = Native.ChangeDisplaySettingsEx(null, ref desired, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
            if (test != Native.DISP_CHANGE_SUCCESSFUL)
            {
                Log("Direct: CDS_TEST rejected " + target + " Hz (rc=" + test + ").");
                return false;
            }

            int rc = Native.ChangeDisplaySettingsEx(null, ref desired, IntPtr.Zero, 0, IntPtr.Zero);
            if (rc != Native.DISP_CHANGE_SUCCESSFUL)
            {
                Log("Direct: ChangeDisplaySettingsEx failed (rc=" + rc + ").");
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                int observed = ReadCurrent().dmDisplayFrequency;
                if (observed == target)
                {
                    Log("Direct: set " + target + " Hz with one call; readback " + observed + " Hz.");
                    return true;
                }
                Thread.Sleep(100);
            }
            Log("Direct: applied, but readback never reported " + target + " Hz.");
            return false;
        }

        // Fallback only. The HAL export rotates one step per call, so the loop stays; the fixed
        // 350ms sleep is replaced by a bounded readback that returns as soon as the rate changes.
        private static void ApplyViaHonorHal(Native.DEVMODE current, int target, int rateCount)
        {
            int startHz = current.dmDisplayFrequency;
            for (int attempt = 0; attempt < rateCount + 1; attempt++)
            {
                int after = 0, before = 0;
                int result = Native.ChangeFrequency(ref after, ref before);
                if (result != 1) throw new InvalidOperationException("HONOR ChangeFrequency returned " + result + ".");
                int observed = WaitForRateChange(startHz);
                Log("HONOR ChangeFrequency: " + before + " -> " + after + " Hz; observed " + observed + " Hz.");
                if (observed == target) return;
                if (observed == startHz) throw new InvalidOperationException("Refresh rate did not advance.");
                startHz = observed;
            }
            throw new InvalidOperationException("Target was not reached within one mode cycle.");
        }

        private static int WaitForRateChange(int previousHz)
        {
            for (int i = 0; i < 6; i++)
            {
                int hz = ReadCurrent().dmDisplayFrequency;
                if (hz != previousHz) return hz;
                Thread.Sleep(100);
            }
            return ReadCurrent().dmDisplayFrequency;
        }

        private static Native.DEVMODE ReadCurrent()
        {
            Native.DEVMODE mode = new Native.DEVMODE();
            mode.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            if (!Native.EnumDisplaySettingsEx(null, -1, ref mode, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return mode;
        }

        private static HashSet<int> AvailableRates(Native.DEVMODE current)
        {
            HashSet<int> rates = new HashSet<int>();
            for (int i = 0; i < 4096; i++)
            {
                Native.DEVMODE candidate = new Native.DEVMODE();
                candidate.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                if (!Native.EnumDisplaySettingsEx(null, i, ref candidate, 0)) break;
                if (candidate.dmPelsWidth == current.dmPelsWidth &&
                    candidate.dmPelsHeight == current.dmPelsHeight &&
                    candidate.dmBitsPerPel == current.dmBitsPerPel)
                    rates.Add(candidate.dmDisplayFrequency);
            }
            return rates;
        }
    }
}
