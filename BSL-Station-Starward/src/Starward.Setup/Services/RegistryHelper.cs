using Microsoft.Win32;
using Starward.Setup;

namespace Starward.Setup.Services;

public static class RegistryHelper
{

    public static void WriteUninstallInfo(string folder, string version, long size)
    {
        string exe = Path.Combine(folder, Branding.MainExeName);
        string setupExe = Path.Combine(folder, Branding.SetupExeName);
        using var subkey = Registry.LocalMachine.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Branding.ProductName}");
        subkey.SetValue("Publisher", "Scighost", RegistryValueKind.String);
        subkey.SetValue("DisplayName", Branding.ProductName, RegistryValueKind.String);
        subkey.SetValue("DisplayIcon", exe, RegistryValueKind.String);
        subkey.SetValue("DisplayVersion", version, RegistryValueKind.String);
        subkey.SetValue("InstallLocation", folder, RegistryValueKind.String);
        subkey.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        subkey.SetValue("InstallDate", $"{DateTime.Now:yyyyMMdd}", RegistryValueKind.String);
        subkey.SetValue("UninstallString", $"\"{setupExe}\" uninstall", RegistryValueKind.String);
        subkey.SetValue("QuietUninstallString", $"\"{setupExe}\" uninstall /S", RegistryValueKind.String);
    }


    public static void DeleteUninstallInfo()
    {
        Registry.LocalMachine.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Branding.ProductName}", false);
    }


    public static void WriteUrlProtocol(string folder)
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Branding.UrlProtocolName}", false);
        Registry.SetValue($@"HKEY_LOCAL_MACHINE\Software\Classes\{Branding.UrlProtocolName}", "", "URL:Starward Protocol");
        Registry.SetValue($@"HKEY_LOCAL_MACHINE\Software\Classes\{Branding.UrlProtocolName}", "URL Protocol", "");
        Registry.SetValue($@"HKEY_LOCAL_MACHINE\Software\Classes\{Branding.UrlProtocolName}\DefaultIcon", "", $"{Branding.MainExeName},1");
        Registry.SetValue($@"HKEY_LOCAL_MACHINE\Software\Classes\{Branding.UrlProtocolName}\Shell\Open\Command", "", $"""
            "{Path.Combine(folder, Branding.MainExeName)}" "%1"
            """);
    }


    public static void DeleteUrlProtocol()
    {
        Registry.LocalMachine.DeleteSubKeyTree($@"Software\Classes\{Branding.UrlProtocolName}", false);
    }


    public static string? GetInstallLocation()
    {
        using var subkey = Registry.LocalMachine.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Branding.ProductName}");
        return subkey?.GetValue("InstallLocation") as string;
    }


    public static void DeleteRegistrySetting()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{Branding.ProductName}", false);
    }

}





