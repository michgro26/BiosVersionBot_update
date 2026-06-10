using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace BiosVersionBot.Networking
{
    public sealed class NetworkDiagnosticService : INetworkDiagnosticService
    {
        public async Task<NetworkDiagnosticResult> IsHostActiveAsync(string computerName)
        {
            if (string.IsNullOrWhiteSpace(computerName))
                return new NetworkDiagnosticResult(false, "Pusta nazwa hosta");

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(computerName, 1500);
                if (reply.Status == IPStatus.Success)
                    return new NetworkDiagnosticResult(true, "Ping OK");

                return new NetworkDiagnosticResult(false, $"Ping: {reply.Status}");
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult(false, $"PingException: {ex.Message}");
            }
        }
    }
}
