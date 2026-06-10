using System.Threading.Tasks;

namespace BiosVersionBot.Networking
{
    public interface INetworkDiagnosticService
    {
        Task<NetworkDiagnosticResult> IsHostActiveAsync(string computerName);
    }
}
