using Plugin.NaughtyDog.Crypto;
using System.Net;

namespace Plugin.NaughtyDog.Ticket;

/// <summary>
/// The ticket check a Naughty Dog game makes before it goes online: the
/// client sends its raw NP ticket in one datagram and wants it echoed back,
/// sealed with the net crypt.
/// </summary>
/// <remarks>
/// [C 1.09 0x447c28] NetInit sends the ticket (sceNpManagerGetTicket) to the
/// net config's ticket host/port (+0x10/+0x14, then +0x2c), up to five tries per
/// address; a reply shorter than 0x14 bytes is a retry. 0x467fa8 decrypts the
/// reply and checks its trailing HMAC (error 0x73610ca9 if bad), then the payload
/// must equal the ticket byte for byte, or the game shows "the server couldn't
/// verify the ticket". The original server presumably checked the ticket's
/// signature first; RPCN signs with its own key, so this one does not.
/// </remarks>
public sealed class TicketService(Func<int, NdCrypt?> cryptForPort, bool logTraffic)
{
    /// <summary>The largest reply the client reads (its buffer is 0x3ff bytes).</summary>
    public const int MaxReply = 0x3ff;

    /// <summary>The reply to a datagram that arrived on <paramref name="port"/>, or null to send none.</summary>
    public byte[]? Answer(int port, EndPoint remote, ReadOnlySpan<byte> ticket)
    {
        if (cryptForPort(port) is not { } crypt)
        {
            Log.Warning("[naughtydog/ticket] {Remote}: datagram on port {Port}, which no title's ticket check uses", remote, port);
            return null;
        }
        if (ticket.Length + NdCrypt.HmacSize > MaxReply)
        {
            Log.Warning("[naughtydog/ticket] {Remote}: {Length}-byte ticket; the echo would not fit the client's buffer", remote, ticket.Length);
            return null;
        }
        if (logTraffic)
            Log.Debug("[naughtydog/ticket] {Remote}: ticket {Length}B {Head}", remote, ticket.Length,
                Convert.ToHexString(ticket[..Math.Min(32, ticket.Length)]));
        return crypt.SealMessage(ticket);
    }
}
