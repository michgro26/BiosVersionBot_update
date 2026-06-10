using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace BiosVersionBot.Networking
{
    public sealed class NetworkDiagnosticService : INetworkDiagnosticService
    {
        private const int SmbPort = 445;
        private const int TimeoutMs = 3000;

        public async Task<NetworkDiagnosticResult> IsHostActiveAsync(string computerName)
        {
            if (string.IsNullOrWhiteSpace(computerName))
                return new NetworkDiagnosticResult(false, "Pusta nazwa hosta");

            computerName = computerName.Trim();

            try
            {
                using var client = new TcpClient();

                var connectTask = client.ConnectAsync(computerName, SmbPort);
                var timeoutTask = Task.Delay(TimeoutMs);

                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask != connectTask)
                    return new NetworkDiagnosticResult(false, $"TCP {SmbPort} timeout po {TimeoutMs} ms");

                await connectTask;

                if (client.Connected)
                    return new NetworkDiagnosticResult(true, $"TCP {SmbPort} open");

                return new NetworkDiagnosticResult(false, $"TCP {SmbPort} closed");
            }
            catch (SocketException ex)
            {
                return new NetworkDiagnosticResult(false, $"TCP {SmbPort} SocketException: {ex.SocketErrorCode}");
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult(false, $"TCP {SmbPort} Exception: {ex.Message}");
            }
        }
    }
}