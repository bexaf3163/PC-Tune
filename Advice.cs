// Startup and service classification knowledge base: identifies background programs,
// classifies items into Recommended to disable / Optional / Keep / System / Unknown,
// detects orphaned entries pointing to missing executables, and estimates live RAM impact.
using System;
using System.IO;

namespace PCTune
{
    enum StCategory
    {
        RecommendedDisable,
        Optional,
        Keep,
        System,
        Unknown
    }

    static class Advice
    {
        // Order matters: first match wins.
        // Columns: keyword | description | category ("Rec"/"Opt"/"Keep"/"Sys")
        static readonly string[][] Table =
        {
            // ---- Security and core system: System (Protected)
            new[] { "securityhealth",   "Windows Security tray icon",                                              "Sys"  },
            new[] { "windowsdefender",  "Microsoft Defender Antivirus protection",                                 "Sys"  },
            new[] { "spdriverinstall",  "MediaTek SP driver installer shortcut",                                   "Rec"  },

            // ---- Laptop hardware services: System (Protected)
            new[] { "dynamic tuning",   "Intel Dynamic Tuning thermal & power controller (required for CPU/iGPU TDP)", "Sys" },
            new[] { "credential vault", "Broadcom Credential Vault (required for fingerprint & smart-card login)", "Sys"  },
            new[] { "content protection","Intel HDCP content protection (required for external displays & DRM video)", "Sys" },
            new[] { "firmwareswitch",   "Fibocom WWAN LTE modem firmware service",                                 "Sys"  },
            new[] { "dynamic application loader", "Intel DAL processor host interface",                            "Sys"  },
            new[] { "management engine","Intel Management Engine WMI provider",                                    "Sys"  },

            // ---- Dell OEM telemetry & support bundle (~380 MB RAM): Recommended to disable
            new[] { "dell.techhub",     "Dell TechHub telemetry, analytics & instrumentation (~350 MB RAM). Hardware/BIOS profiles work without it", "Rec" },
            new[] { "delltechhub",      "Dell TechHub telemetry, analytics & instrumentation (~350 MB RAM)",       "Rec"  },
            new[] { "dell client management", "Dell Update background agent (~70 MB RAM). Manual Dell Update still works", "Rec" },
            new[] { "dell.update",      "Dell background update sub-agent (~45 MB RAM). Manual updates still work","Rec"  },
            new[] { "dellsupportassist","Dell SupportAssist background diagnostics & telemetry",                   "Rec"  },
            new[] { "supportassist",    "Dell SupportAssist background diagnostics & telemetry",                   "Rec"  },
            new[] { "dell",             "Dell background utility service",                                         "Rec"  },

            // ---- Redundant / frequent background updaters: Recommended to disable
            new[] { "skillbrains\\updater", "Lightshot background update check (runs every 4 hours)",              "Rec"  },
            new[] { "ubisoft connect background update", "Ubisoft Connect daily background update check (updates on launch anyway)", "Rec" },
            new[] { "git for windows",  "Git for Windows daily background updater",                                "Rec"  },
            new[] { "softlanding",      "Windows promotional & tip delivery scheduled task",                       "Rec"  },
            new[] { "sustainabilitytelemetry", "Windows sustainability telemetry task",                            "Rec"  },

            // ---- Core audio drivers: Keep
            new[] { "waves",            "Waves MaxxAudio processing & headphone jack detection",                   "Keep" },
            new[] { "rtkaud",           "Realtek HD Audio Universal Service & jack detection",                     "Keep" },
            new[] { "realtek",          "Realtek audio driver service",                                            "Keep" },
            new[] { "nahimic",          "Nahimic audio processing service",                                        "Keep" },
            new[] { "intel(r) audio",   "Intel Smart Sound Technology audio service (required for mic & HDMI audio)", "Keep" },
            new[] { "graphics control panel", "Intel Graphics Control Panel driver service",                       "Keep" },

            // ---- Browser scheduled updaters: Keep (security updates)
            new[] { "bravesoftwareupdatetask", "Brave Browser scheduled security update check",                    "Keep" },
            new[] { "microsoftedgeupdatetask", "Microsoft Edge scheduled security update check",                   "Keep" },
            new[] { "brave update",     "Brave Update background service (can use Manual on-demand mode)",         "Rec"  },
            new[] { "bravesoftware\\update", "Brave Update background service (can use Manual on-demand mode)",    "Rec"  },
            new[] { "microsoft edge update", "Microsoft Edge Update service (can use Manual on-demand mode)",      "Rec"  },
            new[] { "googleupdate",     "Google Chrome scheduled security updater",                                "Keep" },

            // ---- eID smart-card utilities & PC-Tune: Keep
            new[] { "open-eid",         "Estonian Open-EID smart-card updater (needed for e-ID authentication)",   "Keep" },
            new[] { "id-updater",       "Estonian Open-EID smart-card updater (needed for e-ID authentication)",   "Keep" },
            new[] { "digidoc",          "DigiDoc smart-card signing utility",                                      "Keep" },
            new[] { "pc-tune",          "PC-Tune power profile, tray & floating widget at sign-in",                "Keep" },

            // ---- Optional user applications, launchers, overlays & peripherals (Ask user / Optional)
            new[] { "onedrive",         "Microsoft OneDrive cloud file synchronization",                           "Opt"  },
            new[] { "dropbox",          "Dropbox cloud file synchronization",                                      "Opt"  },
            new[] { "yandexdisk",       "Yandex.Disk cloud file synchronization",                                  "Opt"  },
            new[] { "lightshot",        "Lightshot PrtScrn screenshot hotkey utility (~16 MB RAM)",                "Opt"  },
            new[] { "punto",            "Automatic keyboard layout switcher",                                      "Opt"  },
            new[] { "openai.codex",     "ChatGPT / OpenAI Codex desktop app autostart (~2 GB RAM when active)",    "Opt"  },
            new[] { "chatgpt",          "ChatGPT / OpenAI Codex desktop app autostart (~2 GB RAM when active)",    "Opt"  },
            new[] { "cowork",           "Claude Cowork VM background service (~25 MB RAM)",                        "Opt"  },
            new[] { "claude",           "Claude desktop application autostart",                                    "Opt"  },
            new[] { "wallpaper engine", "Wallpaper Engine background service",                                     "Opt"  },
            new[] { "lamparray",        "Logitech LampArray Windows Dynamic Lighting service (polls CPU in background)", "Opt" },
            new[] { "mobile connectivity","Samsung USB Mobile Connectivity driver service (only needed when phone is plugged in)", "Opt" },
            new[] { "graphics software","Intel Graphics Software control panel & PresentMon ETW service (~20 MB RAM)", "Opt" },
            new[] { "thunderbolt",      "Thunderbolt Peer-to-Peer Shortcut service",                               "Opt"  },
            new[] { "management and security", "Intel Management and Security Status tray notification task",      "Opt"  },
            new[] { "apple",            "Apple Devices iPhone/iPad USB sync background helper",                    "Opt"  },
            new[] { "windows terminal", "Windows Terminal login startup task",                                     "Opt"  },
            new[] { "magicpods",        "MagicPods wireless earbuds battery & popup service (~108 MB RAM)",        "Opt"  },
            new[] { "steam.exe",        "Steam game launcher background autostart (~200 MB RAM when running)",     "Opt"  },
            new[] { "jagex",            "Jagex Launcher autostart for Old School RuneScape",                       "Opt"  },
            new[] { "epicgames",        "Epic Games Launcher autostart",                                           "Opt"  },
            new[] { "ubisoft",          "Ubisoft Connect game launcher",                                           "Opt"  },
            new[] { "battle.net",       "Blizzard Battle.net game launcher",                                       "Opt"  },
            new[] { "eadesktop",        "EA Desktop game launcher",                                                "Opt"  },
            new[] { "gog galaxy",       "GOG Galaxy game launcher",                                                "Opt"  },
            new[] { "discord",          "Discord voice & chat autostart (~250 MB RAM when running)",               "Opt"  },
            new[] { "telegram",         "Telegram Desktop messenger autostart",                                    "Opt"  },
            new[] { "whatsapp",         "WhatsApp Desktop messenger autostart",                                    "Opt"  },
            new[] { "spotify",          "Spotify music player autostart (~180 MB RAM when running)",               "Opt"  },
            new[] { "steelseries",      "SteelSeries GG peripheral utility (onboard mouse DPI profiles work without it)", "Opt" },
            new[] { "logitech",         "Logitech G HUB peripheral utility",                                       "Opt"  },
            new[] { "razer",            "Razer Synapse peripheral utility",                                        "Opt"  },
            new[] { "corsair",          "Corsair iCUE peripheral utility",                                         "Opt"  },
            new[] { "teams",            "Microsoft Teams background autostart",                                    "Opt"  },
            new[] { "zoom",             "Zoom video conferencing autostart",                                       "Opt"  },
            new[] { "adobe",            "Adobe background update & cloud helper",                                  "Rec"  },
            new[] { "acrotray",         "Adobe Acrobat tray assistant",                                            "Rec"  },
            new[] { "java",             "Java background update checker",                                          "Rec"  }
        };

        public static void Fill(StItem it)
        {
            bool preLocked = it.Locked;

            if (it.IsOrphaned)
            {
                it.Category = StCategory.RecommendedDisable;
                it.Locked = false;
                it.Advise = it.Enabled;
                it.What = "Orphaned entry (target executable no longer exists): " + it.MissingTarget;
                return;
            }

            string hay = (it.Name + " " + it.Id + " " + it.Cmd).ToLowerInvariant();
            foreach (var row in Table)
            {
                if (hay.IndexOf(row[0], StringComparison.Ordinal) < 0) continue;
                string desc = row[1];
                if (it.EstRamMb >= 5 && desc.IndexOf("MB", StringComparison.OrdinalIgnoreCase) < 0)
                    desc += " (~" + it.EstRamMb + " MB RAM)";
                it.What = desc;

                string cat = row[2];
                if (preLocked || cat == "Sys")
                {
                    it.Category = StCategory.System;
                    it.Locked = true;
                    it.Advise = false;
                }
                else if (cat == "Rec")
                {
                    it.Category = StCategory.RecommendedDisable;
                    it.Locked = false;
                    it.Advise = it.Enabled;
                }
                else if (cat == "Keep")
                {
                    it.Category = StCategory.Keep;
                    it.Locked = false;
                    it.Advise = false;
                }
                else
                {
                    it.Category = StCategory.Optional;
                    it.Locked = false;
                    it.Advise = false;
                }
                return;
            }

            it.Category = preLocked ? StCategory.System : StCategory.Unknown;
            it.Locked = preLocked;
            it.Advise = false;
            string unk = preLocked
                ? "Protected system component"
                : "Unrecognized entry — review path before disabling";
            if (it.EstRamMb >= 5) unk += " (~" + it.EstRamMb + " MB RAM)";
            it.What = unk;
        }

        public static string ForService(string name, out bool advise)
        {
            string n = (name == null ? "" : name).ToLowerInvariant();
            advise = false;
            if (n.StartsWith("dell") || n.IndexOf("supportassist", StringComparison.Ordinal) >= 0)
            {
                advise = true;
                return "Dell telemetry & update bundle (~380 MB RAM across 9 processes)";
            }
            if (n == "aaerrorport")
            {
                advise = true;
                return "Orphaned Active Anticheat service (executable missing from Temp)";
            }
            if (n.IndexOf("ushupgrade", StringComparison.Ordinal) >= 0)
            {
                advise = true;
                return "Credential vault firmware update (36 MB in background; needed only during update)";
            }
            if (n == "esifsvc")
                return "Intel Dynamic Tuning thermal controller - System (Protected)";
            if (n == "hoststoragesvc")
                return "Fingerprint & credential vault - System (Protected)";
            if (n == "cplspcon")
                return "HDCP video protection - System (Protected)";
            if (n == "firmwareswitchservice")
                return "LTE modem firmware - System (Protected)";
            if (n.StartsWith("ss_conn", StringComparison.Ordinal))
                return "Optional: Samsung USB phone drivers (needed only when phone is connected via USB)";
            if (n.IndexOf("cowork", StringComparison.Ordinal) >= 0)
                return "Optional: Claude Cowork VM service (~25 MB RAM; can be set to Manual)";
            if (n.IndexOf("steelseries", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("razer", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("corsair", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("lamparray", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("logi_", StringComparison.Ordinal) >= 0)
                return "Optional: peripheral lighting/profile service (paused automatically in Game Mode)";
            if (n.IndexOf("wallpaper", StringComparison.Ordinal) >= 0)
                return "Optional: Wallpaper Engine service (paused automatically in Game Mode)";
            if (n.IndexOf("waves", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("rtkaud", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("audio", StringComparison.Ordinal) >= 0)
                return "Keep: audio driver & microphone processing";
            if (n.IndexOf("intelgraphics", StringComparison.Ordinal) >= 0)
                return "Optional: Intel Graphics Software panel & PresentMon service (~20 MB RAM)";
            if (n.IndexOf("intel", StringComparison.Ordinal) >= 0)
                return "Keep: Intel driver service";
            return "";
        }
    }
}
