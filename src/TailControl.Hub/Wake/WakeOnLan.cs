using System.Net;
using System.Net.Sockets;

namespace TailControl.Hub.Wake;

public static class MagicPacket
{
    public const int Length = 6 + (16 * 6);

    /// <summary>6 octeti 0xFF urmati de MAC-ul repetat de 16 ori (102 octeti).</summary>
    public static byte[] Build(ReadOnlySpan<byte> mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException("Adresa MAC trebuie sa aiba 6 octeti.", nameof(mac));
        }

        var packet = new byte[Length];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
        {
            mac.CopyTo(packet.AsSpan(6 + (i * 6)));
        }

        return packet;
    }
}

public interface IWakeOnLanSender
{
    Task SendAsync(byte[] mac, CancellationToken cancellationToken);
}

/// <summary>Trimite magic packet-ul prin UDP broadcast in LAN (de 3 ori, pentru siguranta).</summary>
public sealed class UdpWakeOnLanSender(HubOptions options) : IWakeOnLanSender
{
    private const int Repeats = 3;

    public async Task SendAsync(byte[] mac, CancellationToken cancellationToken)
    {
        var broadcast = options.ParsedWakeBroadcast
            ?? throw new InvalidOperationException("Hub:WakeBroadcastAddress nu este configurat.");

        var packet = MagicPacket.Build(mac);
        var endpoint = new IPEndPoint(broadcast, options.WakePort);

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        for (var i = 0; i < Repeats; i++)
        {
            await udp.SendAsync(packet, endpoint, cancellationToken).ConfigureAwait(false);
            if (i < Repeats - 1)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
