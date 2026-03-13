using System.Net;
using System.Net.Sockets;

namespace DriveChill.Utils;

public static class UrlSecurity
{
    [Obsolete("Use TryValidateOutboundHttpUrlAsync for non-blocking DNS resolution")]
    public static bool TryValidateOutboundHttpUrl(
        string url,
        bool allowPrivateTargets,
        out string? reason)
    {
        reason = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = "Invalid URL";
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            reason = "URL must start with http:// or https://";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            reason = "URL hostname is required";
            return false;
        }

        var host = uri.Host.Trim();
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            reason = "localhost is not allowed";
            return false;
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (SocketException)
        {
            reason = "Hostname cannot be resolved";
            return false;
        }
        catch (ArgumentException)
        {
            reason = "Invalid hostname";
            return false;
        }

        if (addresses.Length == 0)
        {
            reason = "Hostname has no routable address";
            return false;
        }

        foreach (var ip in addresses)
        {
            if (IPAddress.IsLoopback(ip))
            {
                reason = "Loopback targets are not allowed";
                return false;
            }

            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
            {
                reason = "Non-routable targets are not allowed";
                return false;
            }

            if (IsUnspecified(ip) || IsReserved(ip) || IsMulticast(ip))
            {
                reason = "Non-routable targets are not allowed";
                return false;
            }

            if (!allowPrivateTargets && IsPrivate(ip))
            {
                reason = "Private network targets are not allowed";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Async version of <see cref="TryValidateOutboundHttpUrl"/> that uses
    /// non-blocking DNS resolution via <see cref="Dns.GetHostAddressesAsync"/>.
    /// </summary>
    public static async Task<(bool Valid, string? Reason)> TryValidateOutboundHttpUrlAsync(
        string url,
        bool allowPrivateTargets)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, "Invalid URL");

        if (uri.Scheme is not ("http" or "https"))
            return (false, "URL must start with http:// or https://");

        if (string.IsNullOrWhiteSpace(uri.Host))
            return (false, "URL hostname is required");

        var host = uri.Host.Trim();
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return (false, "localhost is not allowed");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return (false, "Hostname cannot be resolved");
        }
        catch (ArgumentException)
        {
            return (false, "Invalid hostname");
        }

        if (addresses.Length == 0)
            return (false, "Hostname has no routable address");

        foreach (var ip in addresses)
        {
            if (IPAddress.IsLoopback(ip))
                return (false, "Loopback targets are not allowed");

            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
                return (false, "Non-routable targets are not allowed");

            if (IsUnspecified(ip) || IsReserved(ip) || IsMulticast(ip))
                return (false, "Non-routable targets are not allowed");

            if (!allowPrivateTargets && IsPrivate(ip))
                return (false, "Private network targets are not allowed");
        }

        return (true, null);
    }

    public static string RedactUrlForLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url ?? "";

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri.ToString();
    }

    private static bool IsMulticast(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] >= 224 && bytes[0] <= 239;
        }
        return ip.IsIPv6Multicast;
    }

    private static bool IsUnspecified(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return ip.Equals(IPAddress.Any);
        return ip.Equals(IPAddress.IPv6Any);
    }

    private static bool IsReserved(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        // 240.0.0.0/4 reserved + 255.255.255.255 broadcast
        return b[0] >= 240;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        // Unwrap IPv4-mapped IPv6 addresses (e.g., ::ffff:192.168.1.1) so the
        // IPv4 private-range checks below are not bypassed via the IPv6 path.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var b6 = ip.GetAddressBytes();
            // fc00::/7 unique local addresses
            return (b6[0] & 0xFE) == 0xFC;
        }
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true; // 10.0.0.0/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16/12
        if (b[0] == 192 && b[1] == 168) return true; // 192.168/16
        if (b[0] == 169 && b[1] == 254) return true; // link-local 169.254/16
        return false;
    }
}
