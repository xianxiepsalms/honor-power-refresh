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

        // Same export used by the HONOR Fn+R handler.
        [DllImport(HonorDll, EntryPoint = "ChangeFrequency", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ChangeFrequency(ref int afterHz, ref int beforeHz);
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

        [STAThread]
        private static int Main(string[] args)
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(directory, "HonorPowerRefresh.log");
            string config = Path.Combine(directory, "HonorPowerRefresh.config.ini");
            LoadConfig(config);
            Log("Config: AC=" + acHz + " Hz, battery=" + batteryHz + " Hz, source=" + config);
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
                }
            }
            logging = enableLogging;
            acHz = ac;
            batteryHz = battery;
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
            if (!File.Exists(Native.HonorDll)) throw new FileNotFoundException("HONOR HardwareHal.dll was not found.", Native.HonorDll);
            Native.SYSTEM_POWER_STATUS power;
            if (!Native.GetSystemPowerStatus(out power)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (power.ACLineStatus > 1) { Log("Power source unknown; skipped."); return; }
            int target = power.ACLineStatus == 1 ? acHz : batteryHz;
            Native.DEVMODE current = ReadCurrent();
            Log((power.ACLineStatus == 1 ? "AC" : "battery") + ": current " + current.dmDisplayFrequency + " Hz; target " + target + " Hz.");
            if (current.dmDisplayFrequency == target) return;

            HashSet<int> rates = AvailableRates(current);
            if (!rates.Contains(target)) { Log(target + " Hz unavailable at current resolution."); return; }

            for (int attempt = 0; attempt < rates.Count + 1; attempt++)
            {
                int after = 0, before = 0;
                int result = Native.ChangeFrequency(ref after, ref before);
                if (result != 1) throw new InvalidOperationException("HONOR ChangeFrequency returned " + result + ".");
                Thread.Sleep(350);
                int observed = ReadCurrent().dmDisplayFrequency;
                Log("HONOR ChangeFrequency: " + before + " -> " + after + " Hz; observed " + observed + " Hz.");
                if (observed == target) return;
                if (observed == current.dmDisplayFrequency) throw new InvalidOperationException("Refresh rate did not advance.");
            }
            throw new InvalidOperationException("Target was not reached within one mode cycle.");
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
