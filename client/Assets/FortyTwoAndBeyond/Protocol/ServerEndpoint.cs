using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Validate server names on the main thread and resolve them on network workers.</summary>
    public static class ServerEndpoint
    {
        public const string DefaultHost = "192.168.47.1";

        /// Returns:
        ///     string: Trimmed server hostname or explicit IPv4 address.
        /// Raises:
        ///     ArgumentException: The input is not a hostname or usable IPv4 address.
        public static string NormalizeHost(string host)
        {
            string value = (host ?? "").Trim();
            if (IPAddress.TryParse(value, out IPAddress address))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException("This protocol requires an IPv4 server address.");
                byte first = address.GetAddressBytes()[0];
                if (first == 0 || first >= 224)
                    throw new ArgumentException("Enter a server hostname or usable IPv4 address.");
                return address.ToString();
            }
            if (value.Length > 253 || Uri.CheckHostName(value) != UriHostNameType.Dns)
                throw new ArgumentException("Enter a server hostname or IPv4 address, without a URL or port.");
            return value;
        }

        /// <summary>Resolve IPv4 only from a cancellable network worker, never Unity's main thread.</summary>
        /// Returns:
        ///     IPAddress: The explicit address or first IPv4 DNS result.
        /// Raises:
        ///     ArgumentException: The server field is invalid.
        ///     SocketException: DNS fails or the name has no IPv4 address.
        ///     TimeoutException: DNS takes longer than five seconds.
        ///     OperationCanceledException: The owning worker is stopping.
        public static IPAddress Resolve(string host, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            string value = NormalizeHost(host);
            if (IPAddress.TryParse(value, out IPAddress literal)) return literal;
            var lookup = Dns.GetHostAddressesAsync(value);
            var clock = Stopwatch.StartNew();
            while (!lookup.IsCompleted)
            {
                cancellation.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds >= 5)
                    throw new TimeoutException("DNS lookup timed out for " + value + ".");
                Thread.Sleep(25);
            }
            cancellation.ThrowIfCancellationRequested();
            IPAddress result = lookup.GetAwaiter().GetResult()
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
            if (result == null) throw new SocketException((int)SocketError.HostNotFound);
            return IPAddress.Parse(NormalizeHost(result.ToString()));
        }
    }
}
