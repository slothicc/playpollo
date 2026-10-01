using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace PlayPollo.Server
{
    public static class ServerLocation
    {
        /// <summary>True when the host name or address refers to this PC, so the server can read files from it.</summary>
        public static bool IsLocal(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return true;
            }

            host = host.Trim().Trim('[', ']');
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IPAddress.TryParse(host, out var address))
            {
                return IPAddress.IsLoopback(address) || LocalAddresses().Any(a => a.Equals(address));
            }

            var machine = Environment.MachineName;
            return string.Equals(host, machine, StringComparison.OrdinalIgnoreCase)
                || host.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase);
        }

        private static IPAddress[] LocalAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .ToArray();
            }
            catch (NetworkInformationException)
            {
                return new IPAddress[0];
            }
        }
    }
}
