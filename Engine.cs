// Core optimization, power management, hardware diagnostics, and tweak engine.
// Decoupled from UI controls via the Sink callback so the main window, tray menu,
// and desktop widget share the exact same verified logic.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PCTune
{
    enum Lv { Plain = 0, Ok = 1, Warn = 2, Bad = 3, Head = 4 }

    class Engine
    {
        public readonly List<Tweak> tweaks = new List<Tweak>();
        public Action<string, Lv> Sink;

        // AC screen timeout in minutes (0 = Never turn off display on AC).
        public int screenMinutes = 0;

        static bool? memCompCache;
        static readonly Dictionary<string, string> biosCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static bool warmingSlowChecks;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);
        const uint SPI_SETMOUSE = 0x0004;
        const uint SPIF_SENDCHANGE = 0x0002;

        public Engine()
        {
            HwInfo.Ensure();
            screenMinutes = Cfg.ScreenMinutes;
            BuildTweaks();
        }

        void Emit(string s, Lv lv)
        {
            var k = Sink;
            if (k != null) k(s, lv);
            Sys.ToFile(s);
        }

        void Say(string s) { Emit(s, Lv.Plain); }
        void Ok(string s) { Emit("  [+] " + s, Lv.Ok); }
        void Warn(string s) { Emit("  [!] " + s, Lv.Warn); }
        void Bad(string s) { Emit("  [x] " + s, Lv.Bad); }
        void Head(string s) { Emit("", Lv.Plain); Emit("== " + s, Lv.Head); }

        public void Echo(string s)
        {
            Lv lv = s.IndexOf("[x]", StringComparison.Ordinal) >= 0 ? Lv.Bad
                  : s.IndexOf("[!]", StringComparison.Ordinal) >= 0 ? Lv.Warn
                  : s.IndexOf("[+]", StringComparison.Ordinal) >= 0 ? Lv.Ok
                  : s.StartsWith("==", StringComparison.Ordinal) ? Lv.Head
                  : Lv.Plain;
            Emit(s, lv);
        }

        public void RunPerf() { ApplyPerfMode(null); }
        public void RunGaming() { ApplyGamingMode(null); }
        public void RunCompetitive() { ApplyCompetitiveMode(null); }
        public void RunComp() { ApplyCompetitiveMode(null); }
        public void RunBalanced() { ApplyBalancedMode(null); }
        public void RunEco() { ApplyEcoMode(null); }
        public void RunCar() { ApplyCarMode(null); }
        public void RunRestorePower() { RestorePowerMode(null); }
        public void RunDiagnose() { Diagnose(null); }
        public void RunRecommended() { ApplyRecommended(null); }
        public void RunApply(List<Tweak> sel) { ApplyList(sel); }
        public void RunRevert(List<Tweak> sel) { RevertList(sel); }

        public void RefreshTweaks()
        {
            foreach (var t in tweaks) t.RefreshState();
        }

        public void WarmSlowChecksAsync(Action onDone)
        {
            if (memCompCache.HasValue || warmingSlowChecks) return;
            warmingSlowChecks = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { WarmSlowChecksSync(); }
                catch { }
                finally { warmingSlowChecks = false; }
                if (onDone != null)
                {
                    try { onDone(); } catch { }
                }
            });
        }

        public static void WarmSlowChecksSync()
        {
            MemComp();
            if (Sys.HasBios)
            {
                string[] keys = {
                    "ThermalManagement", "Absolute", "BIOSConnect", "UefiNwStack", "WirelessWwan", "Nfc",
                    "BlockBootUntilChasIntrusionClr", "ChasIntrusion", "TelemetryAccessLvl",
                    "SmmSecurityMitigation", "CStatesCtrl", "Virtualization", "PrimaryBattChargeCfg"
                };
                foreach (string k in keys)
                {
                    string v = Sys.BiosGet(k);
                    lock (biosCache) { biosCache[k] = v; }
                }
            }
        }

        static string BiosCached(string opt)
        {
            lock (biosCache)
            {
                string v;
                return biosCache.TryGetValue(opt, out v) ? v : null;
            }
        }

        static TweakState CheckBios(string opt, string expected)
        {
            if (!Sys.HasBios) return TweakState.Unsupported;
            string v = BiosCached(opt);
            if (v == null) return TweakState.Unknown;
            if (v.Length == 0) return TweakState.Unsupported;
            return Eq(v, expected) ? TweakState.Applied : TweakState.NotApplied;
        }

        static TweakState CheckBios2(string opt1, string exp1, string opt2, string exp2)
        {
            if (!Sys.HasBios) return TweakState.Unsupported;
            string v1 = BiosCached(opt1), v2 = BiosCached(opt2);
            if (v1 == null || v2 == null) return TweakState.Unknown;
            if (v1.Length == 0 && v2.Length == 0) return TweakState.Unsupported;
            return Eq(v1, exp1) && Eq(v2, exp2) ? TweakState.Applied : TweakState.NotApplied;
        }

        // Ensures AC brightness and DC disk timeouts are not left stuck at Eco values
        // when switching back to AC performance/gaming/balanced modes.
        void EnsureNormalBrightnessAndDcDefaults(bool backup = true)
        {
            int acBr = Sys.PowIdx(Sys.SUB_VIDEO, Sys.BRIGHTNESS, true);
            if (acBr > 0 && acBr <= 50)
                Sys.Ac(Sys.SUB_VIDEO, Sys.BRIGHTNESS, 100, backup);

            int dcDisk = Sys.PowIdx(Sys.SUB_DISK, Sys.DISKIDLE, false);
            if (dcDisk > 0 && dcDisk <= 30)
                Sys.Dc(Sys.SUB_DISK, Sys.DISKIDLE, 120, backup);
        }

        // Zeroes all AC sleep, hibernate, unattended sleep, and hybrid sleep timers.
        void DisableAcSleepAndHibernate(bool backup = true)
        {
            Sys.Ac(Sys.SUB_SLEEP, Sys.STANDBYIDLE, 0, backup);
            Sys.Ac(Sys.SUB_SLEEP, Sys.HIBERNATEIDLE, 0, backup);
            Sys.Ac(Sys.SUB_SLEEP, Sys.UNATTENDSLEEP, 0, backup);
            Sys.Ac(Sys.SUB_SLEEP, Sys.HYBRIDSLEEP, 0, backup);
        }

        void VerifyPowerState(int expectedAcVideoSec)
        {
            int acVid = Sys.PowIdx(Sys.SUB_VIDEO, Sys.VIDEOIDLE, true);
            int acSlp = Sys.PowIdx(Sys.SUB_SLEEP, Sys.STANDBYIDLE, true);
            int acHib = Sys.PowIdx(Sys.SUB_SLEEP, Sys.HIBERNATEIDLE, true);
            int acIdleDisable = Sys.PowIdx(Sys.SUB_PROC, Sys.IDLEDISABLE, true);

            if (acVid == expectedAcVideoSec && acSlp == 0 && (acHib <= 0))
            {
                Ok("Verified AC timers: display=" + (acVid == 0 ? "Never" : (acVid / 60) + " min") +
                   ", sleep=Never, hibernate=Never, C-States=" + (acIdleDisable == 0 ? "Enabled" : "Disabled"));
            }
            else
            {
                Warn("Power verification: AC display=" + acVid + "s, sleep=" + acSlp + "s, hibernate=" + acHib + "s");
            }
        }

        // ------------------------------------------------------------ Power Modes

        void ApplyPerfMode(Action<string> _)
        {
            if (GameMode.Active) GameMode.ReleaseRuntimeAndServices(Say);
            Head("Mode «Plugged in»: maximum performance");
            Cfg.Profile = "perf";
            Games.CarTweaks(false, Echo);
            EnsureNormalBrightnessAndDcDefaults();

            Sys.Ac(Sys.SUB_PROC, Sys.PERFEPP, 0);
            Sys.Ac(Sys.SUB_PROC, Sys.PROCMAX, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.PROCMIN, 20);
            Sys.Ac(Sys.SUB_PROC, Sys.BOOST, 2);
            Ok("Energy preference 0, max processor state 100%, aggressive turbo boost");

            Sys.Ac(Sys.SUB_PROC, Sys.CPMINCORES, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.CPMAXCORES, 100);
            // Keep C-States enabled (IDLEDISABLE = 0) so idle cores can enter C1E/C6.
            // Forcing IDLEDISABLE = 1 locks all cores in C0 at max voltage, starving shared
            // iGPU TDP and preventing single/dual-core Turbo Boost bins.
            Sys.Ac(Sys.SUB_PROC, Sys.IDLEDISABLE, 0);
            Ok("Core parking disabled (100% unparked); CPU C-States kept active for max Turbo & GPU TDP headroom");

            Sys.Ac(Sys.SUB_DISK, Sys.DISKIDLE, 0);
            Sys.Ac(Sys.SUB_USB, Sys.USBSUSP, 0);
            Sys.Ac(Sys.SUB_PCIEXPRESS, Sys.ASPM, 0);
            Sys.Ac(Sys.SUB_WIFI, Sys.WIFISAVE, 0);
            Sys.Ac(Sys.SUB_INTELGFX, Sys.INTELGFXPLAN, 2);
            DisableAcSleepAndHibernate();
            Ok("Disk, USB, PCIe ASPM and Wi-Fi power saving disabled on AC; Intel Graphics set to Max Performance");

            int sec = ScreenSeconds();
            Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, sec);
            Ok(sec == 0 ? "Screen on AC: Never turns off" : "Screen on AC turns off after " + (sec / 60) + " min");

            Sys.SetReg(Sys.PWRTHROTTLE, "PowerThrottlingOff", 1, RegistryValueKind.DWord);
            Ok("Background power throttling disabled");

            Sys.Pow("/setactive SCHEME_CURRENT");
            Sys.Pow("/overlaysetactive " + Sys.OVERLAY_MAX);
            Ok("Windows power overlay: Best performance");
            VerifyPowerState(sec);

            if (Sys.OnBattery())
                Warn("Laptop is currently on battery - AC settings are saved and activate when plugged in");
        }

        void ApplyGamingMode(Action<string> _)
        {
            Head("Mode «Gaming»: sustained FPS & frametime consistency");
            Cfg.Profile = "game";
            Games.CarTweaks(false, Echo);
            EnsureNormalBrightnessAndDcDefaults();

            // On laptops with integrated graphics sharing a 15-28W package TDP, EPP=15 and
            // Boost=3 (Aggressive At Guaranteed) prevent CPU spin-loops from stealing watts
            // needed by the GPU. On desktop/dGPU systems, EPP=0 and Boost=2 are used.
            bool sharedTdp = HwInfo.IsLaptop && HwInfo.IsIntegratedOnly && !HwInfo.HasDiscreteGpu;
            int epp = sharedTdp ? 15 : 0;
            int boost = sharedTdp ? 3 : 2;

            Sys.Ac(Sys.SUB_PROC, Sys.PERFEPP, epp);
            Sys.Ac(Sys.SUB_PROC, Sys.PROCMAX, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.PROCMIN, 20);
            Sys.Ac(Sys.SUB_PROC, Sys.BOOST, boost);
            Sys.Ac(Sys.SUB_PROC, Sys.CPMINCORES, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.CPMAXCORES, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.IDLEDISABLE, 0);
            Ok(sharedTdp
                ? "CPU/iGPU shared-TDP balance: EPP 15, 100% unparked cores, C-States active so Iris Xe gets full watts"
                : "CPU gaming profile: EPP 0, 100% unparked cores, aggressive boost, C-States active");

            Sys.Ac(Sys.SUB_DISK, Sys.DISKIDLE, 0);
            Sys.Ac(Sys.SUB_USB, Sys.USBSUSP, 0);
            Sys.Ac(Sys.SUB_PCIEXPRESS, Sys.ASPM, 0);
            Sys.Ac(Sys.SUB_WIFI, Sys.WIFISAVE, 0);
            Sys.Ac(Sys.SUB_INTELGFX, Sys.INTELGFXPLAN, 2);
            DisableAcSleepAndHibernate();
            Ok("Disk, USB, PCIe ASPM, and Wi-Fi power saving disabled; AC sleep & hibernate set to Never");

            int sec = ScreenSeconds();
            Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, sec);
            Ok(sec == 0 ? "Screen on AC: Never turns off" : "Screen on AC turns off after " + (sec / 60) + " min");

            // Allow EcoQoS to throttle non-focused background apps on shared-TDP laptops
            // so the foreground game gets maximum package budget.
            Sys.SetReg(Sys.PWRTHROTTLE, "PowerThrottlingOff", sharedTdp ? 0 : 1, RegistryValueKind.DWord);

            Sys.Pow("/setactive SCHEME_CURRENT");
            Sys.Pow("/overlaysetactive " + Sys.OVERLAY_MAX);
            Ok("Windows power overlay: Best performance");
            VerifyPowerState(sec);
        }

        void ApplyCompetitiveMode(Action<string> _)
        {
            Head("Mode «Competitive»: lowest input & scheduling latency");
            Cfg.Profile = "comp";
            Games.CarTweaks(false, Echo);
            EnsureNormalBrightnessAndDcDefaults();

            Sys.Ac(Sys.SUB_PROC, Sys.PERFEPP, 0);
            Sys.Ac(Sys.SUB_PROC, Sys.PROCMAX, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.PROCMIN, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.BOOST, 2);
            Sys.Ac(Sys.SUB_PROC, Sys.CPMINCORES, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.CPMAXCORES, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.IDLEDISABLE, 0);
            Ok("Processor locked at 100% min/max state on AC, EPP 0, 0% core parking");

            Sys.Ac(Sys.SUB_DISK, Sys.DISKIDLE, 0);
            Sys.Ac(Sys.SUB_USB, Sys.USBSUSP, 0);
            Sys.Ac(Sys.SUB_PCIEXPRESS, Sys.ASPM, 0);
            Sys.Ac(Sys.SUB_WIFI, Sys.WIFISAVE, 0);
            Sys.Ac(Sys.SUB_INTELGFX, Sys.INTELGFXPLAN, 2);
            DisableAcSleepAndHibernate();
            Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, 0);
            Sys.SetReg(Sys.PWRTHROTTLE, "PowerThrottlingOff", 1, RegistryValueKind.DWord);

            Sys.Pow("/setactive SCHEME_CURRENT");
            Sys.Pow("/overlaysetactive " + Sys.OVERLAY_MAX);
            Ok("All AC timeouts (display, sleep, hibernate) set to Never; best performance overlay active");
            VerifyPowerState(0);
        }

        void ApplyBalancedMode(Action<string> _)
        {
            if (GameMode.Active) GameMode.ReleaseRuntimeAndServices(Say);
            Head("Mode «Balanced»: quiet daily operation & responsive boost");
            Cfg.Profile = "bal";
            Games.CarTweaks(false, Echo);
            EnsureNormalBrightnessAndDcDefaults();

            // Clean AC defaults
            Sys.Ac(Sys.SUB_PROC, Sys.PERFEPP, 35);
            Sys.Ac(Sys.SUB_PROC, Sys.PROCMAX, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.PROCMIN, 5);
            Sys.Ac(Sys.SUB_PROC, Sys.BOOST, 1);
            Sys.Ac(Sys.SUB_PROC, Sys.CPMINCORES, 50);
            Sys.Ac(Sys.SUB_PROC, Sys.CPMAXCORES, 100);
            Sys.Ac(Sys.SUB_PROC, Sys.IDLEDISABLE, 0);
            Sys.Ac(Sys.SUB_DISK, Sys.DISKIDLE, 0);
            Sys.Ac(Sys.SUB_USB, Sys.USBSUSP, 0);
            Sys.Ac(Sys.SUB_PCIEXPRESS, Sys.ASPM, 1);
            Sys.Ac(Sys.SUB_WIFI, Sys.WIFISAVE, 0);
            Sys.Ac(Sys.SUB_INTELGFX, Sys.INTELGFXPLAN, 1);
            DisableAcSleepAndHibernate();

            int sec = ScreenSeconds();
            Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, sec);

            // Clean DC defaults
            Sys.Dc(Sys.SUB_PROC, Sys.PERFEPP, 60);
            Sys.Dc(Sys.SUB_PROC, Sys.PROCMAX, 100);
            Sys.Dc(Sys.SUB_PROC, Sys.PROCMIN, 5);
            Sys.Dc(Sys.SUB_PROC, Sys.BOOST, 1);
            Sys.Dc(Sys.SUB_PROC, Sys.CPMINCORES, 25);
            Sys.Dc(Sys.SUB_PROC, Sys.CPMAXCORES, 100);
            Sys.Dc(Sys.SUB_PROC, Sys.IDLEDISABLE, 0);
            Sys.Dc(Sys.SUB_DISK, Sys.DISKIDLE, 120);
            Sys.Dc(Sys.SUB_USB, Sys.USBSUSP, 1);
            Sys.Dc(Sys.SUB_PCIEXPRESS, Sys.ASPM, 2);
            Sys.Dc(Sys.SUB_WIFI, Sys.WIFISAVE, 2);
            Sys.Dc(Sys.SUB_INTELGFX, Sys.INTELGFXPLAN, 0);
            Sys.Dc(Sys.SUB_VIDEO, Sys.VIDEOIDLE, DcScreenSeconds);
            Sys.Dc(Sys.SUB_SLEEP, Sys.STANDBYIDLE, 600);
            Sys.Dc(Sys.SUB_SLEEP, Sys.HIBERNATEIDLE, 1800);

            Sys.SetReg(Sys.PWRTHROTTLE, "PowerThrottlingOff", 0, RegistryValueKind.DWord);
            Sys.Pow("/setactive SCHEME_CURRENT");
            Sys.Pow("/overlaysetactive " + Sys.OVERLAY_BAL);
            Ok("Balanced AC/DC settings restored; Windows overlay set to Balanced");
            VerifyPowerState(sec);
        }

        void ApplyEcoMode(Action<string> _)
        {
            if (GameMode.Active) GameMode.ReleaseRuntimeAndServices(Say);
            Head("Mode «Economy»: maximum battery runtime & low power draw");
            Cfg.Profile = "eco";
            Games.CarTweaks(false, Echo);
            Say("  Applied to both AC (for weak adapters/inverters) and battery power.");

            Both(Sys.SUB_PROC, Sys.PERFEPP, 100);
            Both(Sys.SUB_PROC, Sys.PROCMAX, 30);
            Both(Sys.SUB_PROC, Sys.PROCMIN, 5);
            Both(Sys.SUB_PROC, Sys.BOOST, 0);
            Ok("Energy preference 100, frequency cap 30%, turbo boost off");

            Both(Sys.SUB_PROC, Sys.CPMINCORES, 10);
            Both(Sys.SUB_PROC, Sys.CPMAXCORES, 100);
            Both(Sys.SUB_PROC, Sys.IDLEDISABLE, 0);
            Ok("Core parking and deep C-States active");

            Both(Sys.SUB_PCIEXPRESS, Sys.ASPM, 2);
            Both(Sys.SUB_USB, Sys.USBSUSP, 1);
            Both(Sys.SUB_DISK, Sys.DISKIDLE, 30);
            Both(Sys.SUB_WIFI, Sys.WIFISAVE, 3);
            Both(Sys.SUB_INTELGFX, Sys.INTELGFXPLAN, 0);
            Ok("PCIe ASPM, USB, disk, Wi-Fi and Intel Graphics set to maximum power saving");

            Both(Sys.SUB_VIDEO, Sys.BRIGHTNESS, 25);

            int sec = ScreenSeconds();
            Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, sec);
            Sys.Dc(Sys.SUB_VIDEO, Sys.VIDEOIDLE, DcScreenSeconds);
            Ok("Brightness 25%; battery screen timeout " + (DcScreenSeconds / 60) + " min; AC screen timeout " +
               (sec == 0 ? "Never" : (sec / 60) + " min"));

            DisableAcSleepAndHibernate();
            Sys.Dc(Sys.SUB_SLEEP, Sys.STANDBYIDLE, 300);
            Sys.Dc(Sys.SUB_SLEEP, Sys.HIBERNATEIDLE, 900);
            Ok("On battery: sleep after 5 min, hibernate after 15 min; on AC: no sleep");

            Sys.SetReg(Sys.PWRTHROTTLE, "PowerThrottlingOff", 0, RegistryValueKind.DWord);
            Sys.Pow("/setactive SCHEME_CURRENT");
            Sys.Pow("/overlaysetactive " + Sys.OVERLAY_ECO);
            Ok("Windows power overlay: Best power efficiency");

            EcoCharge();
        }

        void ApplyCarMode(Action<string> _)
        {
            if (GameMode.Active) GameMode.ReleaseRuntimeAndServices(Say);
            Head("Mode «In the car»: gaming on a 20-30 W charger");
            Say("  Keeps package power within 11-13 W so the game stays smooth without draining the battery.");
            Cfg.Profile = "car";

            Both(Sys.SUB_PROC, Sys.PERFEPP, 60);
            Both(Sys.SUB_PROC, Sys.PROCMAX, 100);
            Both(Sys.SUB_PROC, Sys.PROCMIN, 5);
            Both(Sys.SUB_PROC, Sys.BOOST, 0);
            Ok("Turbo boost off, EPP 60: saves 6 W of CPU package power for graphics");

            Both(Sys.SUB_PROC, Sys.CPMINCORES, 50);
            Both(Sys.SUB_PROC, Sys.CPMAXCORES, 100);
            Both(Sys.SUB_PROC, Sys.IDLEDISABLE, 0);
            Ok("Idle cores sleep (C-States active), 50% unparked floor");

            Both(Sys.SUB_PCIEXPRESS, Sys.ASPM, 2);
            Both(Sys.SUB_USB, Sys.USBSUSP, 1);
            Both(Sys.SUB_DISK, Sys.DISKIDLE, 30);
            Both(Sys.SUB_WIFI, Sys.WIFISAVE, 1);
            Both(Sys.SUB_INTELGFX, Sys.INTELGFXPLAN, 1);
            Ok("PCIe, USB and disk save power; Wi-Fi set to low power saving to keep ping stable");

            var br = Sys.CurIndex(Sys.SUB_VIDEO, Sys.BRIGHTNESS);
            bool cut = false;
            if (br[0] > CarBrightness) { Sys.Ac(Sys.SUB_VIDEO, Sys.BRIGHTNESS, CarBrightness); cut = true; }
            if (br[1] > CarBrightness) { Sys.Dc(Sys.SUB_VIDEO, Sys.BRIGHTNESS, CarBrightness); cut = true; }
            int sec = ScreenSeconds();
            Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, sec);
            Sys.Dc(Sys.SUB_VIDEO, Sys.VIDEOIDLE, DcScreenSeconds);
            Ok((cut ? "Brightness capped at " + CarBrightness + "%" : "Brightness already <= " + CarBrightness + "%")
               + "; battery screen timeout " + (DcScreenSeconds / 60) + " min");

            DisableAcSleepAndHibernate();
            Sys.Dc(Sys.SUB_SLEEP, Sys.STANDBYIDLE, 1800);
            Sys.Dc(Sys.SUB_SLEEP, Sys.HIBERNATEIDLE, 7200);
            Ok("No sleep on AC; on battery sleep after 30 min idle");

            Sys.SetReg(Sys.PWRTHROTTLE, "PowerThrottlingOff", 0, RegistryValueKind.DWord);
            Sys.Pow("/setactive SCHEME_CURRENT");
            Sys.Pow("/overlaysetactive " + Sys.OVERLAY_BAL);
            Ok("Windows power overlay: Balanced");

            Games.CarTweaks(true, Echo);
            EcoCharge();
        }

        void RestorePowerMode(Action<string> _)
        {
            if (GameMode.Active) GameMode.ReleaseRuntimeAndServices(Say);
            Head("Restoring backed-up power settings");
            Games.CarTweaks(false, Echo);
            int n = Sys.RestorePowerBackup(Say);
            EnsureNormalBrightnessAndDcDefaults(false);
            DisableAcSleepAndHibernate(false);
            Sys.Ac(Sys.SUB_PROC, Sys.IDLEDISABLE, 0, false);
            Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, ScreenSeconds(), false);
            Sys.Pow("/setactive SCHEME_CURRENT");
            Sys.Pow("/overlaysetactive " + Sys.OVERLAY_BAL);
            Cfg.Profile = "bal";
            Ok("Restored " + n + " backed-up power parameter(s) and verified safe AC timeouts");
        }

        void Both(string sub, string setting, int v)
        {
            Sys.Ac(sub, setting, v);
            Sys.Dc(sub, setting, v);
        }

        void EcoCharge()
        {
            if (!Sys.HasBios)
            {
                Say("");
                Warn("On a weak charger, battery charging draws extra power alongside system load.");
                return;
            }

            string val = Sys.BiosGet("PrimaryBattChargeCfg");
            Say("");
            Say("  Battery charging (BIOS setting): " + (val.Length > 0 ? val : "could not read"));

            int dash = val.IndexOf('-');
            int stop = 0;
            if (val.StartsWith("Custom", StringComparison.OrdinalIgnoreCase) && dash > 0)
                int.TryParse(val.Substring(dash + 1).Trim(), out stop);

            if (stop > 0)
            {
                Say("     Charging stops at " + stop + "%. Above that threshold the adapter powers only the laptop.");
            }
            else
            {
                Warn("Charge threshold is not limited in BIOS. Can be capped at 80% in BIOS:");
                Say("     F2 -> Power -> Battery Configuration -> Custom (e.g. 50-80%).");
            }
        }

        int ScreenSeconds()
        {
            screenMinutes = Cfg.ScreenMinutes;
            return screenMinutes * 60;
        }

        const int DcScreenSeconds = 300;
        const int CarBrightness = 50;

        // ------------------------------------------------------------ Tweak Execution

        void ApplyRecommended(Action<string> _)
        {
            var sel = new List<Tweak>();
            foreach (var t in tweaks) if (t.Default) sel.Add(t);
            ApplyList(sel);
        }

        void ApplyList(List<Tweak> sel)
        {
            if (sel.Count == 0) { Warn("Nothing to apply"); return; }

            var head = new List<Tweak>();
            var tail = new List<Tweak>();
            foreach (var x in sel) (x.RunFirst ? head : tail).Add(x);
            head.AddRange(tail);
            sel = head;

            Head("Applying (" + sel.Count + ")");
            int done = 0, fail = 0, restartCount = 0;
            foreach (var t in sel)
            {
                Say("");
                Say("· " + t.Title);
                try
                {
                    bool wasApplied = t.Check != null && t.Check() == TweakState.Applied;
                    t.Apply(Say);
                    t.RefreshState();
                    if (t.Check != null && t.State != TweakState.Applied)
                    {
                        fail++;
                        Warn("Target state was not verified (administrator privileges or hardware support required).");
                    }
                    else
                    {
                        if (t.RequiresRestart && !wasApplied)
                        {
                            t.PendingRestart = true;
                            restartCount++;
                        }
                        done++;
                    }
                }
                catch (Exception e) { Bad(e.Message); fail++; }
            }
            Say("");
            if (fail == 0)
            {
                Ok("Done: " + done + " of " + sel.Count + " verified." +
                   (restartCount > 0 ? " (" + restartCount + " item(s) require a Windows restart to take full effect.)" : ""));
            }
            else Warn("Succeeded " + done + ", failed " + fail + " - details above.");
        }

        void RevertList(List<Tweak> sel)
        {
            if (sel.Count == 0) { Warn("Nothing to revert"); return; }
            Head("Reverting (" + sel.Count + ")");
            int done = 0, fail = 0;
            foreach (var t in sel)
            {
                Say("");
                Say("· " + t.Title);
                if (t.Revert == null) { Warn("Revert is not applicable for one-shot maintenance actions"); continue; }
                try
                {
                    t.Revert(Say);
                    t.RefreshState();
                    if (t.Check != null && t.State == TweakState.Applied)
                    {
                        fail++;
                        Warn("Revert could not be verified.");
                    }
                    else
                    {
                        if (t.RequiresRestart) t.PendingRestart = true;
                        done++;
                    }
                }
                catch (Exception e) { Bad(e.Message); fail++; }
            }
            Say("");
            if (fail == 0)
                Ok("Reverted " + done + " item(s).");
            else
                Warn("Reverted " + done + ", failed " + fail + ".");
            Warn("Deleted temporary files and WinSxS compression are one-way actions (use rstrui.exe for full system restore).");
        }

        // ------------------------------------------------------------ Diagnostics

        void Diagnose(Action<string> _)
        {
            HwInfo.ProbeWmi();
            WarmSlowChecksSync();
            Head("System & Architecture");
            Say("  OS          : " + HwInfo.OsCaption + " (Build " + HwInfo.OsBuild + ")");
            Say("  Model       : " + (HwInfo.Manufacturer + " " + HwInfo.Model).Trim());
            DateTime boot = DateTime.MinValue;
            try { boot = System.Management.ManagementDateTimeConverter.ToDateTime(Mo1("Win32_OperatingSystem", "LastBootUpTime")); }
            catch { }
            if (boot != DateTime.MinValue)
            {
                TimeSpan up = DateTime.Now - boot;
                Say(string.Format("  Uptime      : {0} d {1} h {2} min", up.Days, up.Hours, up.Minutes));
                if (up.TotalDays > 7) Warn("More than 7 days without a reboot - kernel & driver changes wait for restart");
            }

            Head("Processor, Graphics & Display");
            Say("  Processor   : " + HwInfo.CpuName + " (" + HwInfo.PhysicalCores + "C / " + HwInfo.LogicalCores + "T" +
                (HwInfo.IsHybridCpu ? ", Hybrid P+E architecture" : ", homogeneous cores") + ")");
            Say("  BIOS        : " + Mo1("Win32_BIOS", "SMBIOSBIOSVersion"));
            string gpuVendor = HwInfo.HasNvidiaGpu ? "NVIDIA" : HwInfo.HasAmdGpu ? "AMD" : "Intel";
            Say("  Graphics    : " + HwInfo.GpuName + " (" + gpuVendor +
                (HwInfo.HasDiscreteGpu ? ", Discrete GPU" : ", Shared-TDP Integrated GPU") +
                ", driver " + Mo1("Win32_VideoController", "DriverVersion") + ")");
            Say("  Display     : " + HwInfo.MonitorWidth + "x" + HwInfo.MonitorHeight + " @ " +
                HwInfo.MonitorRefreshHz + " Hz (max supported: " + HwInfo.MonitorMaxRefreshHz + " Hz)");
            if (HwInfo.MonitorMaxRefreshHz > HwInfo.MonitorRefreshHz)
            {
                Warn("Display is running at " + HwInfo.MonitorRefreshHz + " Hz, but supports up to " +
                     HwInfo.MonitorMaxRefreshHz + " Hz (Settings -> System -> Display -> Advanced display).");
            }
            else Ok("Display is running at its maximum refresh rate (" + HwInfo.MonitorRefreshHz + " Hz)");

            Head("Memory");
            int sticks = 0;
            try
            {
                foreach (System.Management.ManagementObject m in
                         new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory").Get())
                {
                    double gb = Convert.ToDouble(m["Capacity"]) / 1073741824.0;
                    Say(string.Format("  {0,-8} {1,4:N0} GB   {2} MT/s   {3}",
                        m["DeviceLocator"], gb, m["ConfiguredClockSpeed"], ("" + m["PartNumber"]).Trim()));
                    sticks++;
                }
            }
            catch (Exception e) { Bad("not read: " + e.Message); }
            if (sticks >= 2) Ok("Dual-channel memory active (" + sticks + " DIMMs @ " + HwInfo.RamSpeedMts + " MT/s) - full memory bandwidth for iGPU");
            else if (sticks == 1) Bad("Single-channel memory (1 DIMM). Adding a second matching SO-DIMM boosts integrated GPU FPS by 20-35%");

            Head("Drive & Storage");
            try
            {
                var sc = new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                sc.Connect();
                var q = new System.Management.ObjectQuery("SELECT * FROM MSFT_PhysicalDisk");
                foreach (System.Management.ManagementObject d in
                         new System.Management.ManagementObjectSearcher(sc, q).Get())
                {
                    Say("  " + d["FriendlyName"] + " (" + HwInfo.DriveType + ")");
                    try
                    {
                        foreach (System.Management.ManagementObject rc in d.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            if (rc["Wear"] != null) Say("     cell wear   : " + rc["Wear"] + "%");
                            if (rc["Temperature"] != null)
                            {
                                int tC = Convert.ToInt32(rc["Temperature"]);
                                Say("     temperature : " + tC + " C");
                                if (tC >= 70) Warn("     SSD temperature >= 70 C - thermal throttling threshold");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { Say("  (SMART data unavailable)"); }

            var drv = new DriveInfo("C");
            double freeGb = drv.AvailableFreeSpace / 1073741824.0;
            double totGb = drv.TotalSize / 1073741824.0;
            Say(string.Format("  Free C:     : {0:N0} GB of {1:N0} ({2:N0}%)", freeGb, totGb, freeGb / totGb * 100));
            if (freeGb / totGb < 0.15) Bad("Less than 15% free on C: - SSD write amplification increases");
            string trim = Sys.Run("fsutil.exe", "behavior query DisableDeleteNotify");
            Say("  TRIM        : " + (trim.Contains("= 0") ? "Enabled" : "Check manually"));

            if (HwInfo.IsLaptop)
            {
                Head("Battery");
                try
                {
                    string st = Mo1("Win32_Battery", "EstimatedChargeRemaining");
                    if (st.Length > 0) Say("  Charge      : " + st + "%");
                    var sc = new System.Management.ManagementScope(@"\\.\root\wmi");
                    sc.Connect();
                    double design = 0, full = 0;
                    foreach (System.Management.ManagementObject b in new System.Management.ManagementObjectSearcher(
                             sc, new System.Management.ObjectQuery("SELECT * FROM BatteryStaticData")).Get())
                        design = Convert.ToDouble(b["DesignedCapacity"]);
                    foreach (System.Management.ManagementObject b in new System.Management.ManagementObjectSearcher(
                             sc, new System.Management.ObjectQuery("SELECT * FROM BatteryFullChargedCapacity")).Get())
                        full = Convert.ToDouble(b["FullChargedCapacity"]);
                    if (design > 0 && full > 0)
                    {
                        double wear = (1 - full / design) * 100;
                        Say(string.Format("  Capacity    : {0:N0} of {1:N0} mWh", full, design));
                        if (wear >= 30)
                        {
                            Warn(string.Format("Battery wear {0:N0}% ({1:N0}% of design capacity remaining)", wear, 100 - wear));
                            Say("     Use «Economy» on battery and cap charging at 80% in BIOS (PrimaryBattChargeCfg).");
                        }
                        else Ok(string.Format("Wear {0:N0}%", wear));
                    }
                    else Say("  Wear        : data unavailable (run powercfg /batteryreport)");
                }
                catch { Say("  (battery data unavailable)"); }
            }

            Head("Power Plan & Active Parameters");
            Say("  Scheme      : " + Sys.Pow("/getactivescheme").Trim());
            string ovr = (string)Sys.GetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes",
                                            "ActiveOverlayAcPowerScheme");
            Say("  Overlay     : " + (ovr == Sys.OVERLAY_MAX ? "Best performance"
                                     : ovr == Sys.OVERLAY_ECO ? "Best power efficiency"
                                     : "Balanced"));
            Say("  Power source: " + (Sys.OnBattery() ? "On battery (DC)" : "Plugged in (AC)"));
            Say("  PC-Tune mode: " + Cfg.ProfileTitle);
            Say("");
            Say(string.Format("  {0,-20}{1,-14}{2}", "Parameter", "on AC", "on battery"));
            Pair("Energy Pref (EPP)", Sys.SUB_PROC, Sys.PERFEPP, "%");
            Pair("Frequency cap", Sys.SUB_PROC, Sys.PROCMAX, "%");
            Pair("Turbo boost", Sys.SUB_PROC, Sys.BOOST, "boost");
            Pair("Min unparked cores", Sys.SUB_PROC, Sys.CPMINCORES, "%");
            Pair("CPU C-States", Sys.SUB_PROC, Sys.IDLEDISABLE, "cstates");
            Pair("Screen brightness", Sys.SUB_VIDEO, Sys.BRIGHTNESS, "%");
            Pair("Screen timeout", Sys.SUB_VIDEO, Sys.VIDEOIDLE, "sec");
            Pair("Sleep timeout", Sys.SUB_SLEEP, Sys.STANDBYIDLE, "sec");
            Pair("Hibernate timeout", Sys.SUB_SLEEP, Sys.HIBERNATEIDLE, "sec");

            int acIdle = PowIdx(Sys.SUB_PROC, Sys.IDLEDISABLE, true);
            if (acIdle == 1)
                Warn("CPU C-States are disabled on AC (IDLEDISABLE=1) - causes idle heat and reduces single-core Turbo & iGPU TDP. Press «Plugged in» or «Gaming» to fix.");

            int acBr = PowIdx(Sys.SUB_VIDEO, Sys.BRIGHTNESS, true);
            if (acBr > 0 && acBr < 50 && Cfg.Profile != "eco")
                Warn("AC brightness index is stuck at " + acBr + "% from Economy mode - press «Plugged in» or «Gaming» to restore.");

            int acVid = PowIdx(Sys.SUB_VIDEO, Sys.VIDEOIDLE, true);
            if (acVid > 0 && acVid <= 1800 && (Cfg.Profile == "perf" || Cfg.Profile == "game" || Cfg.Profile == "comp"))
                Warn("Screen on AC turns off after " + (acVid / 60) + " min in performance mode - set «Screen on AC» to «never» on the Power tab.");

            if (Hib()) Ok("Hibernation is enabled - prevents Modern Standby battery drain when the lid is closed");
            else if (HwInfo.IsLaptop)
                Warn("Hibernation is disabled on a Modern Standby laptop - enable «Hibernation on lid close» in Manual Setup to prevent overnight drain.");

            Head("Device State");
            int bad = 0;
            try
            {
                foreach (System.Management.ManagementObject p in new System.Management.ManagementObjectSearcher(
                         "SELECT Name,ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0").Get())
                {
                    int code = 0;
                    try { code = Convert.ToInt32(p["ConfigManagerErrorCode"]); } catch { }
                    string line = p["Name"] + " — " + DevCode(code);
                    if (code == 22 || code == 45) Say("  " + line);
                    else { Bad(line); bad++; }
                }
            }
            catch { }
            if (bad == 0) Ok("No devices with driver errors");

            Head("Verified Optimization States");
            RefreshTweaks();
            foreach (var t in tweaks)
            {
                if (t.Check == null) continue;
                string badge = t.State == TweakState.Applied ? "[+]"
                             : t.State == TweakState.Unsupported ? "[—]"
                             : "[ ]";
                string status = t.State == TweakState.Applied ? "Applied"
                              : t.State == TweakState.Unsupported ? "Unsupported on this hardware"
                              : "Not applied";
                if (t.PendingRestart) status += " (Restart required)";
                Say(string.Format("  {0} {1,-48} : {2}", badge, t.Title, status));
            }

            Head("BIOS / Firmware Recommendations (Detected -> Recommended -> Reason)");
            Say("  · Memory Channel & Speed : Detected " + (HwInfo.IsDualChannel ? "Dual-Channel" : "Single-Channel") +
                " @ " + HwInfo.RamSpeedMts + " MT/s -> Recommended: Dual-Channel 3200 MT/s -> Maximizes Intel Iris Xe shared memory bandwidth.");
            if (Sys.HasBios)
            {
                string cstates = Sys.BiosGet("CStatesCtrl");
                Say("  · BIOS CPU C-States      : Detected " + (cstates.Length > 0 ? cstates : "Enabled") +
                    " -> Recommended: Enabled -> Allows idle cores to enter C6 so active cores and Iris Xe iGPU receive full 15-28W package turbo budget.");
                string smm = Sys.BiosGet("SmmSecurityMitigation");
                Say("  · SMM Security Mitigation: Detected " + (smm.Length > 0 ? smm : "Unknown") +
                    " -> Recommended: Enabled for security / Disabled only for micro-stutter testing.");
                string batCfg = Sys.BiosGet("PrimaryBattChargeCfg");
                Say("  · Battery Charge Mode    : Detected " + (batCfg.Length > 0 ? batCfg : "Adaptive") +
                    " -> Recommended: Custom (50-80%) or Primarily AC -> Reduces charger heat and preserves battery longevity.");
            }
            object fso = Sys.GetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride");
            bool mitOff = fso != null && Convert.ToInt32(fso) == 3;
            Say("  · CPU Speculative Mitig. : Detected " + (mitOff ? "Disabled (FeatureSettingsOverride=3)" : "Enabled (Windows default)") +
                " -> Recommended: Enabled for daily security; revertible in Manual Setup.");
        }

        static bool MemComp()
        {
            if (memCompCache.HasValue) return memCompCache.Value;
            string s = Sys.Run("powershell.exe",
                "-NoProfile -NonInteractive -Command \"(Get-MMAgent).MemoryCompression\"");
            bool on = s.IndexOf("True", StringComparison.OrdinalIgnoreCase) >= 0;
            memCompCache = on;
            return on;
        }

        static bool Hib()
        {
            object v = Sys.GetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled");
            try { return v != null && Convert.ToInt64(v) == 1; } catch { return false; }
        }

        void Bios(string opt, string val)
        {
            if (!Sys.HasBios) { Warn("Dell Command Configure is not installed - " + opt + " skipped"); return; }
            string was = Sys.BiosGet(opt);
            if (was.Length == 0) { Warn(opt + ": not supported by this system model"); return; }
            if (string.Equals(was, val, StringComparison.OrdinalIgnoreCase))
            {
                lock (biosCache) { biosCache[opt] = val; }
                Ok(opt + " already " + val);
                return;
            }
            if (Sys.BiosSet(opt, val))
            {
                lock (biosCache) { biosCache[opt] = val; }
                Ok(opt + ": " + was + " -> " + val);
            }
            else Bad(opt + ": could not write, left " + was);
        }

        static string Mo1(string cls, string prop)
        {
            try
            {
                foreach (System.Management.ManagementObject m in
                         new System.Management.ManagementObjectSearcher("SELECT " + prop + " FROM " + cls).Get())
                {
                    object v = m[prop];
                    if (v != null) return v.ToString();
                }
            }
            catch { }
            return "";
        }

        static int PowIdx(string sub, string setting, bool ac)
        {
            return Sys.PowIdx(sub, setting, ac);
        }

        static string DevCode(int c)
        {
            switch (c)
            {
                case 1:  return "device is misconfigured";
                case 10: return "does not start";
                case 12: return "not enough free resources";
                case 18: return "driver reinstall needed";
                case 22: return "disabled manually (not a fault)";
                case 28: return "driver not installed";
                case 31: return "Windows cannot load the driver";
                case 43: return "stopped because of a problem";
                case 45: return "not connected now (not a fault)";
                default: return "code " + c;
            }
        }

        static string Fmt(int v, string unit)
        {
            if (v < 0) return "—";
            switch (unit)
            {
                case "sec": return v == 0 ? "Never" : (v / 60) + " min";
                case "%": return v + "%";
                case "cstates": return v == 0 ? "Enabled" : "Disabled (C0 lock)";
                case "boost":
                    if (v == 0) return "Off";
                    if (v == 1) return "Enabled";
                    if (v == 2) return "Aggressive";
                    if (v == 3) return "Aggressive at Guaranteed";
                    return v.ToString();
                default: return v.ToString();
            }
        }

        void Pair(string label, string sub, string setting, string unit)
        {
            Say(string.Format("  {0,-20}{1,-14}{2}", label,
                Fmt(PowIdx(sub, setting, true), unit),
                Fmt(PowIdx(sub, setting, false), unit)));
        }

        static int RegInt(string path, string name, int defVal)
        {
            object v = Sys.GetReg(path, name);
            if (v == null) return defVal;
            try { return Convert.ToInt32(v); } catch { return defVal; }
        }

        static string RegStr(string path, string name, string defVal)
        {
            object v = Sys.GetReg(path, name);
            return v == null ? defVal : v.ToString();
        }

        // ------------------------------------------------------------ Tweaks Catalog

        void BuildTweaks()
        {
            const string G1 = "System responsiveness";
            const string G2 = "Interface and graphics";
            const string G3 = "Maintenance";
            const string G5 = "Memory and background activity";
            const string G6 = "Laptop BIOS";
            const string G4 = "Speed at the cost of protection";

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "Global timer resolution",
                Impact = "MEDIUM IMPACT",
                RequiresRestart = true,
                Detail = "Windows 11 made high-resolution timer requests per-process by default. Restoring global timer resolution behavior keeps frame pacing consistent in older DX9/DX11 games and audio engines.",
                Check = () => RegInt(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 0) == 1
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    if (Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord))
                        Ok("Verified GlobalTimerResolutionRequests = 1 (takes effect after restart)");
                    else Bad("Failed to verify GlobalTimerResolutionRequests");
                },
                Revert = l =>
                {
                    Sys.DelReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests");
                    Ok("Restored default per-process timer behavior");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "MMCSS: priority to the active program",
                Impact = "MEDIUM IMPACT",
                Detail = "Reduces background CPU reservation from 20% to 10%, disables MMCSS network packet throttling, and assigns High GPU (8) and CPU (6) scheduling priority to the Games task profile.",
                Check = () =>
                {
                    const string mm = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
                    return RegInt(mm, "SystemResponsiveness", 20) == 10 &&
                           RegInt(mm + @"\Tasks\Games", "Priority", 2) == 6 &&
                           RegInt(mm + @"\Tasks\Games", "GPU Priority", 2) == 8
                           ? TweakState.Applied : TweakState.NotApplied;
                },
                Apply = l =>
                {
                    const string mm = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
                    Sys.SetRegVerify(mm, "SystemResponsiveness", 10, RegistryValueKind.DWord);
                    Sys.SetRegVerify(mm, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
                    Sys.SetRegVerify(mm + @"\Tasks\Games", "GPU Priority", 8, RegistryValueKind.DWord);
                    Sys.SetRegVerify(mm + @"\Tasks\Games", "Priority", 6, RegistryValueKind.DWord);
                    Sys.SetRegVerify(mm + @"\Tasks\Games", "Scheduling Category", "High", RegistryValueKind.String);
                    Sys.SetRegVerify(mm + @"\Tasks\Games", "SFIO Priority", "High", RegistryValueKind.String);
                    Ok("Verified MMCSS SystemResponsiveness=10, NetworkThrottlingIndex=0xFFFFFFFF, Games Priority=High");
                },
                Revert = l =>
                {
                    const string mm = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
                    Sys.SetRegVerify(mm, "SystemResponsiveness", 20, RegistryValueKind.DWord);
                    Sys.SetRegVerify(mm, "NetworkThrottlingIndex", 10, RegistryValueKind.DWord);
                    Sys.SetRegVerify(mm + @"\Tasks\Games", "GPU Priority", 2, RegistryValueKind.DWord);
                    Sys.SetRegVerify(mm + @"\Tasks\Games", "Priority", 2, RegistryValueKind.DWord);
                    Sys.SetRegVerify(mm + @"\Tasks\Games", "Scheduling Category", "Medium", RegistryValueKind.String);
                    Sys.SetRegVerify(mm + @"\Tasks\Games", "SFIO Priority", "Normal", RegistryValueKind.String);
                    Ok("Restored default Windows MMCSS values (including NetworkThrottlingIndex=10)");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "Foreground CPU quantum priority boost",
                Impact = "MEDIUM IMPACT",
                Detail = "Sets Win32PrioritySeparation to 0x26 (short variable quanta with 3:1 foreground boost). Reduces input latency and prevents background threads from stalling the foreground game.",
                Check = () => RegInt(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 2) == 0x26
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    if (Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, RegistryValueKind.DWord))
                        Ok("Verified Win32PrioritySeparation = 0x26");
                    else Bad("Could not write Win32PrioritySeparation");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 2, RegistryValueKind.DWord);
                    Ok("Restored default Win32PrioritySeparation = 2");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "Disable Game DVR and background screen recording",
                Impact = "HIGH IMPACT",
                Detail = "Disables Xbox Game Bar background video ring-buffer capture (GameDVR_Enabled=0, AppCaptureEnabled=0). Prevents GPU encoder overhead and frametime spikes in fullscreen and borderless games.",
                Check = () => RegInt(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 1) == 0 &&
                              RegInt(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1) == 0
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord);
                    Ok("Verified Game DVR and background capture disabled");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 1, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1, RegistryValueKind.DWord);
                    Sys.DelReg(@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode");
                    Ok("Game DVR re-enabled");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "Enable Windows game mode",
                Impact = "MEDIUM IMPACT",
                Detail = "Suppresses Windows Update driver installations, notifications, and background thread scheduling contention while a game is in focus.",
                Check = () => RegInt(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1) == 1 &&
                              RegInt(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", 1) == 1
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord);
                    Ok("Verified Windows Game Mode enabled");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 0, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", 0, RegistryValueKind.DWord);
                    Ok("Windows Game Mode disabled");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "Disable the Nagle algorithm (network latency)",
                Impact = "MEDIUM IMPACT",
                Detail = "Sets TcpAckFrequency=1 and TCPNoDelay=1 on active physical Ethernet/Wi-Fi adapters with an IPv4 gateway so small game packets are sent immediately without 40-200 ms Nagle coalescing delays.",
                Check = () =>
                {
                    var guids = ActiveIfGuids();
                    if (guids.Count == 0) return TweakState.NotApplied;
                    foreach (string g in guids)
                    {
                        string p = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + g;
                        if (RegInt(p, "TcpAckFrequency", 0) == 1 && RegInt(p, "TCPNoDelay", 0) == 1)
                            return TweakState.Applied;
                    }
                    return TweakState.NotApplied;
                },
                Apply = l =>
                {
                    var guids = ActiveIfGuids();
                    if (guids.Count == 0) { Warn("No active physical network adapter found"); return; }
                    foreach (string g in guids)
                    {
                        string p = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + g;
                        Sys.SetRegVerify(p, "TcpAckFrequency", 1, RegistryValueKind.DWord);
                        Sys.SetRegVerify(p, "TCPNoDelay", 1, RegistryValueKind.DWord);
                        RememberNagleGuid(g);
                    }
                    Sys.RunAdmin("netsh.exe", "int tcp set global autotuninglevel=normal");
                    Ok("Verified TcpAckFrequency=1 & TCPNoDelay=1 on " + guids.Count + " active adapter(s)");
                },
                Revert = l =>
                {
                    var guids = AllNagleGuids();
                    foreach (string g in guids)
                    {
                        string p = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + g;
                        Sys.DelReg(p, "TcpAckFrequency");
                        Sys.DelReg(p, "TCPNoDelay");
                    }
                    ClearNagleGuids();
                    Ok("Restored default TCP acknowledgement coalescing on all adapters");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "Network adapter low-latency mode (Wi-Fi MIMO SMPS & Packet Coalescing off)",
                Impact = "HIGH IMPACT",
                Detail = "Disables Wi-Fi Packet Coalescing (*PacketCoalescing=0) and Spatial Multiplexing Power Save (MIMOPowerSaveMode=3 / No SMPS), plus Ethernet Energy-Efficient Ethernet (EEE) and Ultra Low Power mode, eliminating 2-15 ms radio wake and packet batching jitter.",
                Check = () => CheckNicLowLatency(),
                Apply = l =>
                {
                    int n = ApplyNicLowLatency(true);
                    if (n > 0) Ok("Verified low-latency driver parameters on " + n + " physical network adapter(s)");
                    else Warn("No compatible Intel/Realtek physical network adapter subkeys found");
                },
                Revert = l =>
                {
                    int n = ApplyNicLowLatency(false);
                    Ok("Restored default network adapter power-saving parameters (" + n + " adapter(s))");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "High MSI interrupt priority for GPU, USB & Network controllers",
                Impact = "MEDIUM IMPACT",
                RequiresRestart = true,
                Detail = "Sets DevicePriority=3 (High) under Interrupt Management\\Affinity Policy on MSI-capable Display, USB xHCI (mouse/keyboard), and Network controllers so frame delivery and input interrupts preempt background I/O.",
                Check = () => CheckMsiHighPriority(),
                Apply = l =>
                {
                    int n = ApplyMsiHighPriority(true);
                    Ok("Verified High MSI interrupt priority (DevicePriority=3) on " + n + " controller(s) (takes effect after restart)");
                },
                Revert = l =>
                {
                    int n = ApplyMsiHighPriority(false);
                    Ok("Restored default MSI interrupt priority on " + n + " controller(s) (takes effect after restart)");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "Traffic accounting service (NDU) to manual",
                Impact = "SMALL IMPACT",
                Default = false,
                RequiresRestart = true,
                Detail = "Sets the Windows Network Data Usage Monitoring driver (Ndu) to demand start. Only affects Task Manager per-app network usage history.",
                Check = () => RegInt(@"HKLM\SYSTEM\CurrentControlSet\Services\Ndu", "Start", 2) == 3
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l => { Sys.RunAdmin("sc.exe", "config Ndu start= demand"); Ok("Set Ndu to manual start"); },
                Revert = l => { Sys.RunAdmin("sc.exe", "config Ndu start= auto"); Ok("Restored Ndu to automatic start"); }
            });

            tweaks.Add(new Tweak
            {
                Group = G1,
                Title = "Disable Fast Startup",
                Impact = "SMALL IMPACT",
                Default = false,
                Detail = "Disables hybrid kernel hibernation on shutdown (HiberbootEnabled=0) so 'Shut down' performs a clean kernel and driver reset. Only relevant when hibernation is enabled.",
                Check = () => RegInt(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1) == 0
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, RegistryValueKind.DWord);
                    Ok("Disabled Fast Startup - shutdown now performs a clean kernel restart");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1, RegistryValueKind.DWord);
                    Ok("Re-enabled Fast Startup");
                }
            });

            // ---------------------------------------------------------- Interface and graphics

            tweaks.Add(new Tweak
            {
                Group = G2,
                Title = "Hardware GPU scheduling (HAGS)",
                Impact = "MEDIUM IMPACT",
                RequiresRestart = true,
                HardwareNote = "Detected: " + HwInfo.GpuName,
                Detail = "Offloads GPU frame queue scheduling from the CPU to the graphics processor (HwSchMode=2), reducing render queue latency. Reboot required.",
                Check = () => RegInt(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1) == 2
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord);
                    Ok("Enabled HAGS (takes effect after restart)");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1, RegistryValueKind.DWord);
                    Ok("Disabled HAGS (takes effect after restart)");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G2,
                Title = "Optimizations for windowed games (DXGI Flip Model)",
                Impact = "HIGH IMPACT",
                HardwareNote = HwInfo.IsWin11_22H2Plus ? "Supported (Windows 11 Build " + HwInfo.OsBuild + ")" : "Requires Windows 11 22H2+",
                Detail = "Upgrades legacy DirectX 10/11 windowed and borderless games (such as Lineage II and RuneLite) to use independent flip presentation, bypassing DWM composition latency.",
                Check = () =>
                {
                    if (!HwInfo.IsWin11_22H2Plus) return TweakState.Unsupported;
                    string s = RegStr(@"HKCU\Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "");
                    return s.IndexOf("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase) >= 0
                           ? TweakState.Applied : TweakState.NotApplied;
                },
                Apply = l =>
                {
                    SetDirectXUserGlobalSetting("SwapEffectUpgradeEnable", "1");
                    Ok("Enabled DXGI Flip-Model upgrade for windowed games");
                },
                Revert = l =>
                {
                    SetDirectXUserGlobalSetting("SwapEffectUpgradeEnable", "0");
                    Ok("Disabled DXGI Flip-Model upgrade");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G2,
                Title = "Disable Intel Iris Xe Panel Self-Refresh (PSR2) & dynamic refresh drops (DRRS)",
                Impact = "HIGH IMPACT",
                RequiresRestart = true,
                HardwareNote = HwInfo.HasIntelGpu && HwInfo.IsLaptop ? "Detected: " + HwInfo.GpuName : "Intel laptop iGPU only",
                Detail = "Sets PSR2Disable=1 and Psr2DrrsEnable=0 in the Intel display driver key. Prevents the laptop panel from dropping below 100 Hz on static frames and eliminates 15-40 ms panel wake micro-stutters when moving the camera or mouse.",
                Check = () =>
                {
                    if (!HwInfo.HasIntelGpu || !HwInfo.IsLaptop) return TweakState.Unsupported;
                    var keys = FindIntelDisplayKeys();
                    if (keys.Count == 0) return TweakState.Unsupported;
                    foreach (string p in keys)
                        if (RegInt(p, "PSR2Disable", 0) != 1 || RegInt(p, "Psr2DrrsEnable", 1) != 0)
                            return TweakState.NotApplied;
                    return TweakState.Applied;
                },
                Apply = l =>
                {
                    var keys = FindIntelDisplayKeys();
                    foreach (string p in keys)
                    {
                        Sys.SetRegVerify(p, "PSR2Disable", 1, RegistryValueKind.DWord);
                        Sys.SetRegVerify(p, "Psr2DrrsEnable", 0, RegistryValueKind.DWord);
                    }
                    Ok("Verified Intel PSR2Disable=1 & Psr2DrrsEnable=0 (locks full panel refresh; restart required)");
                },
                Revert = l =>
                {
                    var keys = FindIntelDisplayKeys();
                    foreach (string p in keys)
                    {
                        Sys.SetRegVerify(p, "PSR2Disable", 0, RegistryValueKind.DWord);
                        Sys.SetRegVerify(p, "Psr2DrrsEnable", 1, RegistryValueKind.DWord);
                    }
                    Ok("Restored default Intel PSR2 and DRRS panel power saving");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G2,
                Title = "Fix DWM Multiplane Overlay (MPO) flicker / micro-stutter",
                Impact = "SITUATIONAL",
                Default = false,
                RequiresRestart = true,
                Detail = "Sets OverlayTestMode=5 in HKLM\\SOFTWARE\\Microsoft\\Windows\\Dwm. Recommended if you see brief screen flicker or stutter when alt-tabbing between a borderless game and hardware-accelerated apps (Discord/browsers). Keep off if you have no flicker.",
                Check = () => RegInt(@"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 0) == 5
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, RegistryValueKind.DWord);
                    Ok("Set DWM OverlayTestMode = 5 (takes effect after restart)");
                },
                Revert = l =>
                {
                    Sys.DelReg(@"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");
                    Ok("Restored default DWM Multiplane Overlay behavior");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G2,
                Title = "Raw 1:1 mouse input (disable pointer acceleration)",
                Impact = "SITUATIONAL",
                Default = false,
                Detail = "Disables Windows 'Enhance pointer precision' curve (MouseSpeed=0, Thresholds=0) so cursor distance depends strictly on physical mouse movement.",
                Check = () => RegStr(@"HKCU\Control Panel\Mouse", "MouseSpeed", "1") == "0" &&
                              RegStr(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "6") == "0"
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String);
                    Sys.SetRegVerify(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String);
                    Sys.SetRegVerify(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String);
                    SystemParametersInfo(SPI_SETMOUSE, 0, new int[] { 0, 0, 0 }, SPIF_SENDCHANGE);
                    Ok("Disabled pointer acceleration (1:1 linear response active immediately)");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKCU\Control Panel\Mouse", "MouseSpeed", "1", RegistryValueKind.String);
                    Sys.SetRegVerify(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "6", RegistryValueKind.String);
                    Sys.SetRegVerify(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "10", RegistryValueKind.String);
                    SystemParametersInfo(SPI_SETMOUSE, 0, new int[] { 6, 10, 1 }, SPIF_SENDCHANGE);
                    Ok("Restored standard Windows pointer precision");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G2,
                Title = "Disable window animations",
                Impact = "COSMETIC",
                Default = false,
                RequiresLogoff = true,
                Detail = "Disables window minimize/maximize animations while keeping ClearType font smoothing intact. Takes full effect after signing in again.",
                Check = () => RegStr(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "1") == "0"
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String);
                    Ok("Window animations disabled");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 0, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "1", RegistryValueKind.String);
                    Ok("Window animations re-enabled");
                }
            });

            // ---------------------------------------------------------- Memory and background activity

            tweaks.Add(new Tweak
            {
                Group = G5,
                Title = "Enable hibernation and send the lid close to it",
                Impact = "HIGH IMPACT",
                Default = false,
                HardwareNote = HwInfo.IsLaptop ? "Recommended for Modern Standby laptops" : "Desktop (not needed)",
                Detail = "Prevents overnight battery drain on Modern Standby laptops by enabling hibernation and setting lid-close on battery (and critical battery at 7%) to hibernate.",
                Check = () => Hib() && PowIdx(Sys.SUB_BUTTONS, Sys.LIDACTION, false) == 2
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.RunAdmin("powercfg.exe", "/hibernate on");
                    Sys.Pow("-attributes " + Sys.SUB_BUTTONS + " " + Sys.LIDACTION + " -ATTRIB_HIDE");
                    Sys.Dc(Sys.SUB_BUTTONS, Sys.LIDACTION, 2);
                    Sys.Dc(Sys.SUB_BATTERY, Sys.BATACTIONCRIT, 2);
                    Sys.Dc(Sys.SUB_BATTERY, Sys.BATLEVELCRIT, 7);
                    Sys.Pow("/setactive SCHEME_CURRENT");
                    Ok("Hibernation enabled; lid close on battery and 7% critical battery now hibernate");
                },
                Revert = l =>
                {
                    Sys.Dc(Sys.SUB_BUTTONS, Sys.LIDACTION, 1);
                    Sys.Dc(Sys.SUB_BATTERY, Sys.BATACTIONCRIT, 1);
                    Sys.Pow("/setactive SCHEME_CURRENT");
                    Sys.RunAdmin("powercfg.exe", "/hibernate off");
                    Ok("Hibernation disabled and hiberfil.sys removed");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G5,
                Title = "Enable memory compression",
                Impact = "SITUATIONAL",
                Default = false,
                Detail = "Compresses inactive pages in RAM instead of paging to disk. Helpful when multitasking with many browser tabs alongside a game; adds slight CPU overhead on 4-core CPUs.",
                Check = () => !memCompCache.HasValue ? TweakState.Unknown
                            : memCompCache.Value ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.RunAdmin("powershell.exe", "-NoProfile -NonInteractive -Command \"Enable-MMAgent -mc\"");
                    memCompCache = null;
                    if (MemComp()) Ok("Memory compression enabled");
                    else Bad("Could not enable memory compression");
                },
                Revert = l =>
                {
                    Sys.RunAdmin("powershell.exe", "-NoProfile -NonInteractive -Command \"Disable-MMAgent -mc\"");
                    memCompCache = null;
                    if (!MemComp()) Ok("Memory compression disabled");
                    else Bad("Could not disable memory compression");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G5,
                Title = "Do not share Windows updates with other computers",
                Impact = "MEDIUM IMPACT",
                Detail = "Disables peer-to-peer Windows Update Delivery Optimization uploads (DODownloadMode=0) so background P2P transfers never spike disk or network latency.",
                Check = () => RegInt(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1) == 0
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, RegistryValueKind.DWord);
                    Ok("Verified Delivery Optimization P2P sharing disabled");
                },
                Revert = l =>
                {
                    Sys.DelReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode");
                    Ok("Delivery Optimization policy removed");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G5,
                Title = "Forbid Store apps to run in the background",
                Impact = "SITUATIONAL",
                Default = false,
                Detail = "Prevents UWP / Microsoft Store apps from waking in the background. Unticked by default because Store apps like Teams, Claude, or MagicPods stop delivering background notifications.",
                Check = () => RegInt(@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0) == 1
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0, RegistryValueKind.DWord);
                    Ok("Store background apps restricted");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0, RegistryValueKind.DWord);
                    Sys.SetRegVerify(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 1, RegistryValueKind.DWord);
                    Ok("Store background apps allowed");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G5,
                Title = "Disable the print service",
                Impact = "SMALL IMPACT",
                Default = false,
                Detail = "Stops and disables the Print Spooler (Spooler) service if you never use physical or PDF printers.",
                Check = () => RegInt(@"HKLM\SYSTEM\CurrentControlSet\Services\Spooler", "Start", 2) == 4
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l => { Sys.StopService("Spooler"); Sys.RunAdmin("sc.exe", "config Spooler start= disabled"); Ok("Print Spooler stopped and disabled"); },
                Revert = l => { Sys.RunAdmin("sc.exe", "config Spooler start= auto"); Sys.StartService("Spooler"); Ok("Print Spooler restored and started"); }
            });

            // ---------------------------------------------------------- Laptop BIOS (Dell CCTK)

            tweaks.Add(new Tweak
            {
                Group = G6,
                Title = "Dell Thermal Management: Ultra Performance (max PL1/PL2 package TDP)",
                Impact = "HIGH IMPACT",
                HardwareNote = Sys.HasBios ? "Dell CCTK detected" : "Requires Dell Command Configure",
                Detail = "Sets Dell BIOS ThermalManagement to UltraPerformance so the Embedded Controller raises sustained CPU/iGPU package power limits (PL1/PL2) and fan curve headroom.",
                Check = () => CheckBios("ThermalManagement", "UltraPerformance"),
                Apply = l => Bios("ThermalManagement", "UltraPerformance"),
                Revert = l => Bios("ThermalManagement", "Optimized")
            });

            tweaks.Add(new Tweak
            {
                Group = G6,
                Title = "Disable Absolute (Computrace)",
                Impact = "HARDWARE / BIOS",
                RequiresRestart = true,
                HardwareNote = Sys.HasBios ? "Dell CCTK detected" : "Requires Dell Command Configure",
                Detail = "Sets the corporate Computrace firmware persistence module to DisableAbsolute (reversible; never uses PermanentlyDisabled).",
                Check = () => CheckBios("Absolute", "DisableAbsolute"),
                Apply = l => Bios("Absolute", "DisableAbsolute"),
                Revert = l => Bios("Absolute", "EnableAbsolute")
            });

            tweaks.Add(new Tweak
            {
                Group = G6,
                Title = "Disable network boot and BIOS internet link",
                Impact = "HARDWARE / BIOS",
                RequiresRestart = true,
                Detail = "Disables BIOSConnect, FOTA, HTTPS boot, UEFI network stack, and PXE boot to speed up POST and reduce firmware attack surface.",
                Check = () => CheckBios2("BIOSConnect", "Disabled", "UefiNwStack", "Disabled"),
                Apply = l =>
                {
                    Bios("BIOSConnect", "Disabled");
                    Bios("FOTA", "Disabled");
                    Bios("HttpsBoot", "Disabled");
                    Bios("UefiNwStack", "Disabled");
                    Bios("EmbNic1", "Enabled");
                },
                Revert = l =>
                {
                    Bios("UefiNwStack", "Enabled");
                    Bios("HttpsBoot", "Enabled");
                    Bios("FOTA", "Enabled");
                    Bios("BIOSConnect", "Enabled");
                    Bios("EmbNic1", "EnabledPxe");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G6,
                Title = "Disable the LTE modem and NFC",
                Impact = "HARDWARE / BIOS",
                RequiresRestart = true,
                Detail = "Powers off unused WWAN LTE modem and NFC controller at the PCIe/USB bus level in BIOS.",
                Check = () => CheckBios2("WirelessWwan", "Disabled", "Nfc", "Disabled"),
                Apply = l => { Bios("WirelessWwan", "Disabled"); Bios("Nfc", "Disabled"); },
                Revert = l => { Bios("WirelessWwan", "Enabled"); Bios("Nfc", "Enabled"); }
            });

            tweaks.Add(new Tweak
            {
                Group = G6,
                Title = "Remove the chassis intrusion boot block",
                Impact = "HARDWARE / BIOS",
                RequiresRestart = true,
                Detail = "Clears and disables the chassis intrusion sensor boot block triggered after opening the bottom cover for maintenance.",
                Check = () => CheckBios2("BlockBootUntilChasIntrusionClr", "Disabled", "ChasIntrusion", "Disabled"),
                Apply = l => { Bios("BlockBootUntilChasIntrusionClr", "Disabled"); Bios("ChasIntrusion", "Disabled"); },
                Revert = l => { Bios("ChasIntrusion", "SilentEnable"); Bios("BlockBootUntilChasIntrusionClr", "Enabled"); }
            });

            tweaks.Add(new Tweak
            {
                Group = G6,
                Title = "Disable Dell telemetry in firmware",
                Impact = "HARDWARE / BIOS",
                RequiresRestart = true,
                Detail = "Sets BIOS TelemetryAccessLvl from Full to Disabled.",
                Check = () => CheckBios("TelemetryAccessLvl", "Disabled"),
                Apply = l => Bios("TelemetryAccessLvl", "Disabled"),
                Revert = l => Bios("TelemetryAccessLvl", "Full")
            });

            tweaks.Add(new Tweak
            {
                Group = G6,
                Title = "Disable deep core sleep in BIOS (C-States)",
                Impact = "SITUATIONAL",
                Default = false,
                Risky = true,
                RequiresRestart = true,
                Detail = "Not recommended on 15-28W thin laptops with integrated graphics: disabling C-States keeps idle cores hot and reduces Turbo Boost and iGPU TDP headroom.",
                Check = () => CheckBios("CStatesCtrl", "Disabled"),
                Apply = l => Bios("CStatesCtrl", "Disabled"),
                Revert = l => Bios("CStatesCtrl", "Enabled")
            });

            tweaks.Add(new Tweak
            {
                Group = G6,
                Title = "Disable virtualization (VT-x and VT-d)",
                Impact = "SITUATIONAL",
                Default = false,
                Risky = true,
                RequiresRestart = true,
                Detail = "Disables hardware virtualization extensions in BIOS. Breaks WSL2, Docker, Windows Sandbox, and Android emulators with negligible gaming benefit when VBS is already off.",
                Check = () => CheckBios("Virtualization", "Disabled"),
                Apply = l => { Bios("Virtualization", "Disabled"); Bios("VtForDirectIo", "Disabled"); },
                Revert = l => { Bios("Virtualization", "Enabled"); Bios("VtForDirectIo", "Enabled"); }
            });

            // ---------------------------------------------------------- Maintenance

            tweaks.Add(new Tweak
            {
                Group = G3,
                Title = "Create a restore point",
                Impact = "MAINTENANCE",
                RunFirst = true,
                Detail = "Creates a Windows System Restore checkpoint before applying changes (roll back anytime via rstrui.exe).",
                Apply = l =>
                {
                    Sys.SetReg(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                    string ps = "Enable-ComputerRestore -Drive 'C:\\'; Checkpoint-Computer -Description 'PC-Tune' -RestorePointType MODIFY_SETTINGS";
                    int code;
                    string o = Sys.RunAdminEx("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + ps + "\"", out code);
                    if (code != 0 || (o.Length > 0 && o.ToLowerInvariant().Contains("error")))
                        Bad(o.Trim().Length > 0 ? o.Trim() : "Could not create System Restore point");
                    else
                        Ok("System Restore point created");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G3,
                Title = "Cleanup: temporary files and update cache",
                Impact = "MAINTENANCE",
                Detail = "Cleans temporary files older than 24 hours, downloaded update cache, and crash dumps. Never touches the Recycle Bin or browser passwords/sessions.",
                Slow = true,
                Apply = l =>
                {
                    long bytes = 0;
                    foreach (var ct in Clean.Build())
                    {
                        if (!ct.Advise) continue;
                        bytes += Clean.Sweep(ct, delegate (string s) { Say("  " + s); }).Bytes;
                    }
                    Ok(string.Format("Freed {0:N2} GB", bytes / 1073741824.0));
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G3,
                Title = "Drive TRIM",
                Impact = "MAINTENANCE",
                Detail = "Runs SSD TRIM (defrag C: /L) to inform the NVMe controller of free blocks and maintain sustained write speed.",
                Apply = l =>
                {
                    int code;
                    string o = Sys.RunAdminEx("defrag.exe", "C: /L", out code);
                    if (code == 0) Ok("SSD TRIM completed");
                    else Bad(o.Trim().Length > 0 ? o.Trim() : "Drive TRIM failed (exit code " + code + ")");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G3,
                Title = "Compress the WinSxS component store",
                Impact = "MAINTENANCE",
                Detail = "Reclaims 3-8 GB by cleaning superseded Windows component versions (/StartComponentCleanup /ResetBase). Installed updates become permanent.",
                Default = false,
                Slow = true,
                Apply = l =>
                {
                    int code;
                    string o = Sys.RunAdminEx("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase", out code);
                    if (o.Trim().Length > 0) Say(o.Trim());
                    if (code == 0) Ok("WinSxS cleanup completed");
                    else Bad("DISM WinSxS cleanup exited with code " + code);
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G3,
                Title = "Windows integrity check (DISM + sfc)",
                Impact = "MAINTENANCE",
                Detail = "Repairs corrupted system files against the official Windows component store image.",
                Default = false,
                Slow = true,
                Apply = l =>
                {
                    int c1, c2;
                    Say("  Running DISM /RestoreHealth...");
                    Sys.RunAdminEx("dism.exe", "/Online /Cleanup-Image /RestoreHealth", out c1);
                    if (c1 == 0) Ok("DISM finished"); else Warn("DISM exited with code " + c1);
                    Say("  Running sfc /scannow...");
                    Sys.RunAdminEx("sfc.exe", "/scannow", out c2);
                    if (c2 == 0) Ok("SFC finished. Details: %WINDIR%\\Logs\\CBS\\CBS.log");
                    else Warn("SFC exited with code " + c2 + ". Details: %WINDIR%\\Logs\\CBS\\CBS.log");
                }
            });

            // ---------------------------------------------------------- Speed at the cost of protection

            tweaks.Add(new Tweak
            {
                Group = G4,
                Title = "Disable virtualization-based security (VBS)",
                Impact = "HIGH IMPACT",
                Risky = true,
                Default = false,
                RequiresRestart = true,
                Detail = "Disables Windows Hypervisor & Core Isolation / Memory Integrity overhead (3-10% CPU/frametime gain in CPU-bound games). Trade-off: WSL2, Docker Desktop, and Windows Sandbox will not start while disabled.",
                Check = () => RegInt(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 1) == 0
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, RegistryValueKind.DWord);
                    Sys.RunAdmin("bcdedit.exe", "/set hypervisorlaunchtype off");
                    Ok("VBS and hypervisor launch disabled (takes effect after restart)");
                },
                Revert = l =>
                {
                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 1, RegistryValueKind.DWord);
                    Sys.RunAdmin("bcdedit.exe", "/set hypervisorlaunchtype auto");
                    Ok("VBS and hypervisor launch re-enabled (takes effect after restart)");
                }
            });

            tweaks.Add(new Tweak
            {
                Group = G4,
                Title = "Telemetry to minimum",
                Impact = "SMALL IMPACT",
                Risky = true,
                Default = false,
                Detail = "Sets AllowTelemetry=0 in Windows DataCollection policy.",
                Check = () => RegInt(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1) == 0
                              ? TweakState.Applied : TweakState.NotApplied,
                Apply = l => { Sys.SetRegVerify(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord); Ok("AllowTelemetry = 0"); },
                Revert = l => { Sys.DelReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry"); Ok("Telemetry policy removed"); }
            });

            tweaks.Add(new Tweak
            {
                Group = G4,
                Title = "Disable statistics-gathering tasks in the scheduler",
                Impact = "SMALL IMPACT",
                Risky = true,
                Default = false,
                Detail = "Disables Microsoft Compatibility Appraiser, ProgramDataUpdater, Consolidator, and UsbCeip scheduled telemetry tasks.",
                Check = () => CheckTelemetryTasks(),
                Apply = l =>
                {
                    int n = SetTelemetryTasks(false);
                    if (n > 0) Ok("Disabled " + n + " telemetry scheduled task(s)");
                    else Warn("Could not disable telemetry scheduled tasks");
                },
                Revert = l =>
                {
                    int n = SetTelemetryTasks(true);
                    if (n > 0) Ok("Re-enabled " + n + " telemetry scheduled task(s)");
                    else Warn("Could not re-enable telemetry scheduled tasks");
                }
            });

            string[] order = { G1, G2, G5, G6, G3, G4 };
            var sorted = new List<Tweak>();
            foreach (string g in order)
                foreach (Tweak x in tweaks)
                    if (x.Group == g) sorted.Add(x);
            foreach (Tweak x in tweaks)
                if (Array.IndexOf(order, x.Group) < 0) sorted.Add(x);
            if (sorted.Count == tweaks.Count)
            {
                tweaks.Clear();
                tweaks.AddRange(sorted);
            }
            RefreshTweaks();
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // Returns active physical Ethernet/Wi-Fi adapters with an IPv4 gateway,
        // falling back to any operational non-loopback non-virtual adapter.
        static List<string> ActiveIfGuids()
        {
            var primary = new List<string>();
            var fallback = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    string desc = (ni.Description ?? "").ToLowerInvariant();
                    if (desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") || desc.Contains("tap-"))
                        continue;

                    bool hasGw = false;
                    try
                    {
                        var ip = ni.GetIPProperties();
                        if (ip != null && ip.GatewayAddresses != null && ip.GatewayAddresses.Count > 0)
                            hasGw = true;
                    }
                    catch { }

                    if (hasGw) primary.Add(ni.Id);
                    else fallback.Add(ni.Id);
                }
            }
            catch { }
            return primary.Count > 0 ? primary : fallback;
        }

        const string NagleBackupKey = @"Software\PCTune\NagleInterfaces";

        static void RememberNagleGuid(string guid)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(NagleBackupKey))
                    if (k != null) k.SetValue(guid, 1, RegistryValueKind.DWord);
            }
            catch { }
        }

        static List<string> AllNagleGuids()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string g in ActiveIfGuids()) set.Add(g);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(NagleBackupKey))
                {
                    if (k != null)
                        foreach (string name in k.GetValueNames())
                            if (!string.IsNullOrEmpty(name)) set.Add(name);
                }
            }
            catch { }
            return new List<string>(set);
        }

        static void ClearNagleGuids()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(NagleBackupKey, false); } catch { }
        }

        const string NicClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

        static TweakState CheckNicLowLatency()
        {
            try
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(NicClassKey))
                {
                    if (baseKey == null) return TweakState.NotApplied;
                    bool foundAny = false, allApplied = true;
                    foreach (string sub in baseKey.GetSubKeyNames())
                    {
                        if (sub.Length != 4 || !char.IsDigit(sub[0])) continue;
                        using (var k = baseKey.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string desc = (k.GetValue("DriverDesc") ?? "").ToString();
                            if (desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (k.GetValue("*PacketCoalescing") != null)
                            {
                                foundAny = true;
                                if ((k.GetValue("*PacketCoalescing") ?? "").ToString() != "0") allApplied = false;
                            }
                            if (k.GetValue("MIMOPowerSaveMode") != null)
                            {
                                foundAny = true;
                                if ((k.GetValue("MIMOPowerSaveMode") ?? "").ToString() != "3") allApplied = false;
                            }
                            if (k.GetValue("EEELinkAdvertisement") != null)
                            {
                                foundAny = true;
                                if ((k.GetValue("EEELinkAdvertisement") ?? "").ToString() != "0") allApplied = false;
                            }
                        }
                    }
                    return foundAny && allApplied ? TweakState.Applied : TweakState.NotApplied;
                }
            }
            catch { return TweakState.NotApplied; }
        }

        static int ApplyNicLowLatency(bool enable)
        {
            int touched = 0;
            try
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(NicClassKey))
                {
                    if (baseKey == null) return 0;
                    foreach (string sub in baseKey.GetSubKeyNames())
                    {
                        if (sub.Length != 4 || !char.IsDigit(sub[0])) continue;
                        string fullPath = @"HKLM\" + NicClassKey + @"\" + sub;
                        using (var k = baseKey.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string desc = (k.GetValue("DriverDesc") ?? "").ToString();
                            if (desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            bool hit = false;
                            if (k.GetValue("*PacketCoalescing") != null)
                            {
                                Sys.SetRegVerify(fullPath, "*PacketCoalescing", enable ? "0" : "1", RegistryValueKind.String);
                                hit = true;
                            }
                            if (k.GetValue("MIMOPowerSaveMode") != null)
                            {
                                Sys.SetRegVerify(fullPath, "MIMOPowerSaveMode", enable ? "3" : "0", RegistryValueKind.String);
                                hit = true;
                            }
                            if (k.GetValue("EEELinkAdvertisement") != null)
                            {
                                Sys.SetRegVerify(fullPath, "EEELinkAdvertisement", enable ? "0" : "1", RegistryValueKind.String);
                                hit = true;
                            }
                            if (k.GetValue("ULPMode") != null)
                            {
                                Sys.SetRegVerify(fullPath, "ULPMode", enable ? "0" : "1", RegistryValueKind.String);
                                hit = true;
                            }
                            if (k.GetValue("*SelectiveSuspend") != null)
                            {
                                Sys.SetRegVerify(fullPath, "*SelectiveSuspend", enable ? "0" : "1", RegistryValueKind.String);
                                hit = true;
                            }
                            if (hit) touched++;
                        }
                    }
                }
            }
            catch { }
            return touched;
        }

        static List<string> FindMsiControllerPaths()
        {
            var list = new List<string>();
            string[] targetClasses = {
                "{4d36e968-e325-11ce-bfc1-08002be10318}", // Display
                "{4d36e972-e325-11ce-bfc1-08002be10318}", // Net
                "{36fc9e60-c465-11cf-8056-444553540000}"  // USB
            };
            try
            {
                using (var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI"))
                {
                    if (pci == null) return list;
                    foreach (string dev in pci.GetSubKeyNames())
                    {
                        using (var dk = pci.OpenSubKey(dev))
                        {
                            if (dk == null) continue;
                            foreach (string inst in dk.GetSubKeyNames())
                            {
                                using (var ik = dk.OpenSubKey(inst))
                                {
                                    if (ik == null) continue;
                                    string cls = (ik.GetValue("ClassGUID") ?? "").ToString();
                                    bool match = false;
                                    foreach (string tc in targetClasses)
                                        if (string.Equals(cls, tc, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                                    if (!match) continue;
                                    using (var msi = ik.OpenSubKey(@"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties"))
                                    {
                                        if (msi == null) continue;
                                        object sup = msi.GetValue("MSISupported");
                                        if (sup != null && Convert.ToInt32(sup) == 1)
                                            list.Add(@"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\" + dev + @"\" + inst + @"\Device Parameters\Interrupt Management\Affinity Policy");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        static TweakState CheckMsiHighPriority()
        {
            var paths = FindMsiControllerPaths();
            if (paths.Count == 0) return TweakState.Unsupported;
            foreach (string p in paths)
                if (RegInt(p, "DevicePriority", 0) != 3) return TweakState.NotApplied;
            return TweakState.Applied;
        }

        static int ApplyMsiHighPriority(bool enable)
        {
            var paths = FindMsiControllerPaths();
            int n = 0;
            foreach (string p in paths)
            {
                if (enable)
                {
                    if (Sys.SetRegVerify(p, "DevicePriority", 3, RegistryValueKind.DWord)) n++;
                }
                else
                {
                    Sys.DelReg(p, "DevicePriority");
                    n++;
                }
            }
            return n;
        }

        static void SetDirectXUserGlobalSetting(string tokenKey, string tokenVal)
        {
            const string p = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
            string cur = RegStr(p, "DirectXUserGlobalSettings", "");
            var parts = new List<string>();
            bool replaced = false;
            foreach (string raw in cur.Split(';'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith(tokenKey + "=", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(tokenKey + "=" + tokenVal);
                    replaced = true;
                }
                else
                {
                    parts.Add(t);
                }
            }
            if (!replaced)
                parts.Add(tokenKey + "=" + tokenVal);
            string next = string.Join(";", parts.ToArray()) + ";";
            Sys.SetRegVerify(p, "DirectXUserGlobalSettings", next, RegistryValueKind.String);
        }

        const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        static List<string> FindIntelDisplayKeys()
        {
            var list = new List<string>();
            try
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey))
                {
                    if (baseKey != null)
                    {
                        foreach (string sub in baseKey.GetSubKeyNames())
                        {
                            if (sub.Length != 4 || !char.IsDigit(sub[0])) continue;
                            using (var k = baseKey.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string desc = (k.GetValue("DriverDesc") ?? "").ToString();
                                string prov = (k.GetValue("ProviderName") ?? "").ToString();
                                if (desc.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    prov.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    list.Add(@"HKLM\" + DisplayClassKey + @"\" + sub);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            if (list.Count == 0 && HwInfo.HasIntelGpu)
                list.Add(@"HKLM\" + DisplayClassKey + @"\0000");
            return list;
        }

        static readonly string[,] TelemetryTaskList = {
            {"Consolidator", @"\Microsoft\Windows\Customer Experience Improvement Program\"},
            {"UsbCeip", @"\Microsoft\Windows\Customer Experience Improvement Program\"},
            {"ProgramDataUpdater", @"\Microsoft\Windows\Application Experience\"},
            {"Microsoft Compatibility Appraiser", @"\Microsoft\Windows\Application Experience\"}
        };

        static TweakState CheckTelemetryTasks()
        {
            try
            {
                string sysTasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
                int existing = 0, disabled = 0;
                for (int i = 0; i < TelemetryTaskList.GetLength(0); i++)
                {
                    string rel = (TelemetryTaskList[i, 1] + TelemetryTaskList[i, 0]).TrimStart('\\');
                    string file = Path.Combine(sysTasks, rel);
                    if (!File.Exists(file)) continue;
                    existing++;
                    string xml = File.ReadAllText(file);
                    if (xml.IndexOf("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase) >= 0)
                        disabled++;
                }
                if (existing == 0) return TweakState.Unsupported;
                return disabled == existing ? TweakState.Applied : TweakState.NotApplied;
            }
            catch { return TweakState.NotApplied; }
        }

        static int SetTelemetryTasks(bool enable)
        {
            int count = 0;
            try
            {
                string sysTasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
                for (int i = 0; i < TelemetryTaskList.GetLength(0); i++)
                {
                    string full = TelemetryTaskList[i, 1] + TelemetryTaskList[i, 0];
                    string file = Path.Combine(sysTasks, full.TrimStart('\\'));
                    if (!File.Exists(file)) continue;
                    Sys.RunAdmin("schtasks.exe", "/Change /TN \"" + full + "\" " + (enable ? "/Enable" : "/Disable"));
                    string xml = File.ReadAllText(file);
                    bool isOff = xml.IndexOf("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (enable ? !isOff : isOff) count++;
                }
            }
            catch { }
            return count;
        }
    }
}
