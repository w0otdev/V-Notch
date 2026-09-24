using System.IO;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

// Launch targets are fixed here; search text and saved history never become commands.
internal static class SpotlightSystemCatalog
{
    internal sealed record Entry(string Key, SpotlightResultKind Kind, string Target,
        string Glyph, string Keywords, bool RequiresConfirmation = false)
    {
        public SpotlightSearchItem CreateItem() => new($"system:{Key}", Kind,
            GetLabel($"spotlight.system.{Key}", Keywords.Split(';')[0]), Kind switch
            {
                SpotlightResultKind.Settings => GetLabel("spotlight.system.settings", "Windows Settings"),
                SpotlightResultKind.SystemAction => GetLabel("spotlight.system.actions", "System actions"),
                _ => GetLabel("spotlight.system.tools", "Windows tools")
            }, Target, Kind == SpotlightResultKind.Application ? Target : null);
    }

    internal static IReadOnlyList<Entry> Entries { get; } = Array.AsReadOnly(new[]
    {
        Tool("registry", "regedit.exe", "Registry Editor;regedit;trinh sua so dang ky", windowsRoot: true),
        Tool("taskManager", "Taskmgr.exe", "Task Manager;taskmgr;quan ly tac vu"),
        Tool("resourceMonitor", "resmon.exe", "Resource Monitor;resmon;giam sat tai nguyen"),
        Tool("controlPanel", "control.exe", "Control Panel;bang dieu khien"),
        Tool("terminal", "cmd.exe", "Command Prompt;cmd;dong lenh"),
        Tool("powershell", @"WindowsPowerShell\v1.0\powershell.exe", "Windows PowerShell;powershell"),
        Tool("services", "services.msc", "Services;dich vu"),
        Tool("deviceManager", "devmgmt.msc", "Device Manager;devmgmt;quan ly thiet bi"),
        Tool("diskManager", "diskmgmt.msc", "Disk Management;diskmgmt;quan ly o dia"),
        Tool("eventViewer", "eventvwr.msc", "Event Viewer;eventvwr;nhat ky su kien"),
        Tool("remoteDesktop", "mstsc.exe", "Remote Desktop;mstsc;ket noi may tinh tu xa"),
        Setting("settingsHome", "", "Windows Settings;settings;cai dat windows"),
        Setting("display", "display", "Display;screen;resolution;brightness;man hinh;do phan giai;do sang"),
        Setting("sound", "sound", "Sound;audio;volume;am thanh;loa;am luong"),
        Setting("bluetooth", "bluetooth", "Bluetooth"),
        Setting("wifi", "network-wifi", "Wi-Fi;wifi;wireless;mang khong day"),
        Setting("network", "network-status", "Network;internet;mang"),
        Setting("vpn", "network-vpn", "VPN"),
        Setting("apps", "appsfeatures", "Installed apps;uninstall apps;ung dung da cai;go ung dung"),
        Setting("defaultApps", "defaultapps", "Default apps;ung dung mac dinh"),
        Setting("startup", "startupapps", "Startup apps;ung dung khoi dong"),
        Setting("storage", "storagesense", "Storage;disk space;dung luong;luu tru"),
        Setting("power", "powersleep", "Power;sleep;battery;nguon;pin;che do ngu"),
        Setting("notifications", "notifications", "Notifications;thong bao"),
        Setting("personalization", "personalization", "Personalization;themes;background;ca nhan hoa;hinh nen;chu de"),
        Setting("taskbar", "taskbar", "Taskbar;thanh tac vu"),
        Setting("dateTime", "dateandtime", "Date and time;clock;ngay gio;dong ho"),
        Setting("language", "regionlanguage", "Language;region;ngon ngu;khu vuc"),
        Setting("accounts", "yourinfo", "Accounts;user;tai khoan;nguoi dung"),
        Setting("privacy", "privacy", "Privacy;quyen rieng tu"),
        Setting("update", "windowsupdate", "Windows Update;cap nhat windows"),
        Setting("recovery", "recovery", "Recovery;reset PC;khoi phuc;dat lai may tinh"),
        Setting("about", "about", "About PC;system information;thong tin may tinh;gioi thieu"),
        new Entry("recycleBin", SpotlightResultKind.SystemAction, "shell:RecycleBinFolder", "\uE74D",
            "Open Recycle Bin;recycle bin;trash;thung rac;mo thung rac"),
        Action("lock", "\uE72E", "Lock computer;lock;khoa may", false),
        Action("restart", "\uE777", "Restart computer;restart;reboot;khoi dong lai"),
        Action("shutdown", "\uE7E8", "Shut down;shutdown;power off;tat may"),
        Action("signOut", "\uE8AC", "Sign out;log out;logoff;dang xuat"),
        Action("hibernate", "\uE708", "Hibernate;ngu dong"),
        Action("advancedRestart", "\uE777", "Restart with advanced boot options;advanced startup;khoi dong nang cao")
    });

    private static readonly IReadOnlyDictionary<string, Entry> ByTarget =
        Entries.ToDictionary(entry => entry.Target, StringComparer.OrdinalIgnoreCase);

    internal static Entry? Find(string target) => ByTarget.GetValueOrDefault(target);

    private static string GetLabel(string key, string fallback)
    {
        string text = Loc.Get(key);
        return string.IsNullOrWhiteSpace(text) || text == key ? fallback : text;
    }

    internal static bool IsKnownTarget(SpotlightSearchItem item) => Find(item.Target)?.Kind == item.Kind;

    internal static bool RequiresConfirmation(SpotlightSearchItem item) =>
        IsKnownTarget(item) && Find(item.Target)!.RequiresConfirmation;

    private static Entry Tool(string key, string file, string keywords, bool windowsRoot = false) =>
        new(key, SpotlightResultKind.Application, Path.Combine(windowsRoot
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : Environment.SystemDirectory, file), "\uE713", keywords);

    private static Entry Setting(string key, string page, string keywords) =>
        new(key, SpotlightResultKind.Settings, "ms-settings:" + page, "\uE713", keywords);

    private static Entry Action(string key, string glyph, string keywords, bool confirm = true) =>
        new(key, SpotlightResultKind.SystemAction, "vnotch-action:" + key, glyph, keywords, confirm);
}
