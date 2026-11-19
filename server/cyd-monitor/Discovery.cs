using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CydMonitor;

/// <summary>
/// Answers the device's "where is the server" broadcast.
///
/// The alternative would be mDNS, but advertising a service from Windows needs
/// Bonjour installed, which is a dependency for the user to chase for no gain.
/// A four-byte probe answered with a four-byte reply needs nothing on either
/// side and survives the host's DHCP lease changing, which it will.
/// </summary>
internal sealed class Discovery : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();

    public Discovery(int port)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        // The probe arrives at the broadcast address, which another instance
        // (or a stale socket from a crashed one) may also be bound to.
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        byte[] reply = Encoding.ASCII.GetBytes("CYDMON!");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(ct);
                if (r.Buffer.Length >= 7 &&
                    Encoding.ASCII.GetString(r.Buffer, 0, 7) == "CYDMON?")
                {
                    await _udp.SendAsync(reply, r.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { /* transient; keep listening */ }
            catch (ObjectDisposedException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
