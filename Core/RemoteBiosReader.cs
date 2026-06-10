using System;
using System.Management;

namespace BiosVersionBot.Core
{
    public static class RemoteBiosReader
    {
        public static string ReadBiosVersion(string computerName)
        {
            if (string.IsNullOrWhiteSpace(computerName))
                throw new ArgumentException("Pusta nazwa komputera.", nameof(computerName));

            var options = new ConnectionOptions
            {
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy,
                Timeout = TimeSpan.FromSeconds(20)
            };

            var scope = new ManagementScope($@"\\{computerName}\root\cimv2", options);
            scope.Connect();

            var query = new ObjectQuery("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var collection = searcher.Get();

            foreach (ManagementObject obj in collection)
            {
                using (obj)
                {
                    string? value = obj["SMBIOSBIOSVersion"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }

            throw new InvalidOperationException("WMI nie zwróciło SMBIOSBIOSVersion.");
        }
    }
}
