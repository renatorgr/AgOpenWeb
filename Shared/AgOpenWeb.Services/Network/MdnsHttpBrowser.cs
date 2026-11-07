// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Network;

/// <summary>
/// The quick half of the web cards' "Search the network" (Plans/ADD_ON_CARDS_PLAN.md): asks the host's
/// networks by multicast DNS for the web pages that announce themselves (DNS-SD <c>_http._tcp</c>, RFC 6763:
/// an AiO board's page, a XIAO's, a sprayer or seed drill module's, a printer's) and has them in about a
/// second, each with the name its maker gave it. <see cref="WebPageScanner"/> then sweeps every address for
/// the pages that announce nothing. The question goes out from an ordinary port, so responders answer it by
/// unicast (RFC 6762 section 6.7): no port 5353 to share with the system's own mDNS and no multicast group to
/// join. Where multicast is not allowed (iOS without the entitlement) the question fails quietly and the
/// sweep alone finds the pages.
/// </summary>
public static class MdnsHttpBrowser
{
    public const string ServiceType = "_http._tcp";
    internal const string ServiceName = ServiceType + ".local";
    internal const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly TimeSpan FirstListen = TimeSpan.FromMilliseconds(900), MoreListen = TimeSpan.FromMilliseconds(500);
    private const int MaxMessage = 9000;

    /// <summary>
    /// Asks every IPv4 network in <paramref name="local"/> for the pages announcing themselves and returns
    /// them, by name, in about one to two seconds. A page on one of the host's own addresses gets
    /// <c>{host}</c> in its URL, as the sweep gives it. Never throws for a network that refuses: it
    /// returns what it heard.
    /// </summary>
    public static Task<List<FoundWebPage>> BrowseAsync(IEnumerable<LocalNetworkAddress> local, ISet<IPAddress> own, CancellationToken ct) =>
        Task.Run(() => Browse(local, own, ct), ct);

    private static List<FoundWebPage> Browse(IEnumerable<LocalNetworkAddress> local, ISet<IPAddress> own, CancellationToken ct)
    {
        var answers = new Answers();
        var sockets = OpenSockets(local);
        try
        {
            if (sockets.Count == 0) return [];
            // The instances first; then what their answers left out (an instance's SRV and TXT, its host's
            // address), twice at most, for a responder that sends the records one question at a time.
            Ask(sockets, [(ServiceName, TypePtr)], answers, FirstListen, ct);
            for (int round = 0; round < 2 && !ct.IsCancellationRequested; round++)
            {
                var missing = answers.Missing();
                if (missing.Count == 0) break;
                Ask(sockets, missing, answers, MoreListen, ct);
            }
        }
        finally
        {
            foreach (var s in sockets) s.Socket.Dispose();
        }
        return answers.Pages(own);
    }

    // One socket per IPv4 address of the host, so the question goes out on each network; the address and
    // mask tell which answers came from that network.
    private static List<(Socket Socket, uint Address, uint Mask)> OpenSockets(IEnumerable<LocalNetworkAddress> local)
    {
        var list = new List<(Socket, uint, uint)>();
        foreach (var a in local)
        {
            if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                s.Bind(new IPEndPoint(a.Address, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.Address.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                uint mask = a.PrefixLength is >= 1 and <= 32 ? uint.MaxValue << (32 - a.PrefixLength) : 0xFFFFFF00;
                list.Add((s, Answers.ToUInt(a.Address), mask));
            }
            catch (SocketException) { s.Dispose(); }
        }
        return list;
    }

    // Sends the questions on every socket and reads the answers until the time is up.
    private static void Ask(List<(Socket Socket, uint Address, uint Mask)> sockets, IReadOnlyCollection<(string Name, ushort Type)> questions,
        Answers answers, TimeSpan listen, CancellationToken ct)
    {
        var query = Answers.Query(questions);
        foreach (var s in sockets)
        {
            try { s.Socket.SendTo(query, Group); }
            catch (SocketException) { }   // no multicast on this network
        }
        var buffer = new byte[MaxMessage];
        long until = Environment.TickCount64 + (long)listen.TotalMilliseconds;
        while (!ct.IsCancellationRequested)
        {
            long left = until - Environment.TickCount64;
            if (left <= 0) break;
            var ready = sockets.Select(s => s.Socket).ToList();
            try { Socket.Select(ready, null, null, (int)Math.Min(left, 100) * 1000); }
            catch (SocketException) { break; }
            foreach (var sock in ready)
            {
                int n;
                try { n = sock.Receive(buffer); }
                catch (SocketException) { continue; }
                var via = sockets.Find(s => s.Socket == sock);
                answers.Read(buffer.AsSpan(0, n), via.Address, via.Mask);
            }
        }
    }

    /// <summary>
    /// What the answers said so far: the instances of the service, each instance's host and port (SRV) and
    /// text (TXT), and each host's addresses (A); and the pages they describe. Separate from the sockets so
    /// tests can feed it messages.
    /// </summary>
    internal sealed class Answers
    {
        private sealed class Instance
        {
            public string Label = "";      // the instance's own name, for the operator
            public string Target = "";     // the host name of its SRV record, "" until heard
            public int Port;
            public Dictionary<string, string> Txt = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Regex PathRx = new(@"^/[!-~]{0,200}$", RegexOptions.CultureInvariant);

        private readonly Dictionary<string, Instance> _instances = new(StringComparer.OrdinalIgnoreCase);   // by full name
        // Each host's addresses, each marked when it is on the network that heard it: a host on several
        // networks answers with all of them.
        private readonly Dictionary<string, Dictionary<string, bool>> _addresses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The questions the answers so far leave open: SRV and TXT for an instance without a host,
        /// A for a host without an address.</summary>
        public List<(string Name, ushort Type)> Missing()
        {
            var list = new List<(string, ushort)>();
            foreach (var (name, i) in _instances)
            {
                if (i.Target.Length == 0) { list.Add((name, TypeSrv)); list.Add((name, TypeTxt)); }
                else if (!_addresses.ContainsKey(i.Target) && !list.Contains((i.Target, TypeA))) list.Add((i.Target, TypeA));
            }
            return list;
        }

        /// <summary>The pages the answers describe completely (an instance with a host that has an address),
        /// by name, each URL once. A page on one of <paramref name="own"/> gets <c>{host}</c>.</summary>
        public List<FoundWebPage> Pages(ISet<IPAddress> own)
        {
            var list = new List<FoundWebPage>();
            foreach (var i in _instances.Values)
            {
                if (i.Target.Length == 0 || i.Port == 0 || !_addresses.TryGetValue(i.Target, out var ips) || ips.Count == 0) continue;
                string ip = Pick(ips);
                string path = i.Txt.TryGetValue("path", out var p) && PathRx.IsMatch(p) ? p : "/";   // RFC 6763 section 6.4
                string url = "http://" + (own.Contains(IPAddress.Parse(ip)) ? "{host}" : ip) + (i.Port == 80 ? "" : ":" + i.Port) + path;
                list.Add(new FoundWebPage(Name(i.Label), url));
            }
            return list.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Url, StringComparer.Ordinal)
                .DistinctBy(p => p.Url, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The address on the network that heard the host; else any, the same each time.
        private static string Pick(Dictionary<string, bool> ips)
        {
            foreach (var (ip, here) in ips.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (here) return ip;
            return ips.Keys.Min(StringComparer.Ordinal)!;
        }

        // An instance's name as a bookmark name: spaces collapsed, at most WebCardBookmarks.MaxName characters.
        private static string Name(string label)
        {
            var t = Regex.Replace(label, @"\s+", " ").Trim();
            return t.Length > WebCardBookmarks.MaxName ? t[..WebCardBookmarks.MaxName].TrimEnd() : t;
        }

        /// <summary>Takes one message's records in. Anything that is not a DNS response, or is cut short, is ignored.</summary>
        public void Read(ReadOnlySpan<byte> m, uint viaAddress, uint viaMask)
        {
            try { ReadRecords(m, viaAddress, viaMask); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException or ArgumentException) { }
        }

        private void ReadRecords(ReadOnlySpan<byte> m, uint viaAddress, uint viaMask)
        {
            if (m.Length < 12 || (m[2] & 0x80) == 0) return;   // not a response
            int questions = m[4] << 8 | m[5];
            int records = (m[6] << 8 | m[7]) + (m[8] << 8 | m[9]) + (m[10] << 8 | m[11]);
            int p = 12;
            for (int q = 0; q < questions; q++) { ReadLabels(m, ref p); p += 4; }
            var found = new List<(string Owner, ushort Type, uint Ttl, int Data, int End)>();
            for (int r = 0; r < records; r++)
            {
                string owner = string.Join('.', ReadLabels(m, ref p));
                ushort type = (ushort)(m[p] << 8 | m[p + 1]);
                uint ttl = (uint)(m[p + 4] << 24 | m[p + 5] << 16 | m[p + 6] << 8 | m[p + 7]);
                int length = m[p + 8] << 8 | m[p + 9];
                p += 10;
                if (p + length > m.Length) return;
                found.Add((owner, type, ttl, p, p + length));
                p += length;
            }
            // The instances first, whatever the order in the message, so their SRV, TXT and A records are kept.
            foreach (var (owner, type, ttl, data, _) in found)
            {
                if (type != TypePtr || !owner.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)) continue;
                int at = data;
                var labels = ReadLabels(m, ref at);
                string name = string.Join('.', labels);
                if (ttl == 0) { _instances.Remove(name); continue; }   // a goodbye
                if (!_instances.ContainsKey(name))
                    _instances[name] = new Instance { Label = labels.Count > 3 ? string.Join('.', labels.Take(labels.Count - 3)) : name };
            }
            foreach (var (owner, type, ttl, data, end) in found)
            {
                int at = data;
                switch (type)
                {
                    case TypeSrv when _instances.TryGetValue(owner, out var i):
                        i.Port = m[at + 4] << 8 | m[at + 5];
                        at += 6;
                        var target = string.Join('.', ReadLabels(m, ref at));
                        i.Target = target == "." || target.Length == 0 ? "" : target;
                        break;
                    case TypeTxt when _instances.TryGetValue(owner, out var i):
                    {
                        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        while (at < end)
                        {
                            int n = m[at++];
                            if (at + n > end) break;
                            var entry = Encoding.UTF8.GetString(m.Slice(at, n));
                            at += n;
                            int eq = entry.IndexOf('=');
                            if (entry.Length > 0 && eq != 0) txt[eq < 0 ? entry : entry[..eq]] = eq < 0 ? "" : entry[(eq + 1)..];
                        }
                        i.Txt = txt;
                        break;
                    }
                    case TypeA when end - data == 4 && ttl > 0:
                    {
                        uint a = (uint)(m[at] << 24 | m[at + 1] << 16 | m[at + 2] << 8 | m[at + 3]);
                        if (!_addresses.TryGetValue(owner, out var ips)) _addresses[owner] = ips = new Dictionary<string, bool>(StringComparer.Ordinal);
                        bool here = viaMask != 0 && (a & viaMask) == (viaAddress & viaMask);
                        string ip = $"{m[at]}.{m[at + 1]}.{m[at + 2]}.{m[at + 3]}";
                        if (here || !ips.ContainsKey(ip)) ips[ip] = here;
                        break;
                    }
                }
            }
        }

        // A name's labels, following compression pointers; the position moves past the name as written in place.
        private static List<string> ReadLabels(ReadOnlySpan<byte> m, ref int p)
        {
            var labels = new List<string>();
            int at = p, jumps = 0;
            bool jumped = false;
            while (true)
            {
                int n = m[at];
                if (n == 0) { at++; break; }
                if ((n & 0xC0) == 0xC0)
                {
                    if (++jumps > 20) throw new FormatException("name pointer loop");
                    int target = (n & 0x3F) << 8 | m[at + 1];
                    if (!jumped) { p = at + 2; jumped = true; }
                    at = target;
                    continue;
                }
                labels.Add(Encoding.UTF8.GetString(m.Slice(at + 1, n)));
                at += 1 + n;
            }
            if (!jumped) p = at;
            return labels;
        }

        /// <summary>A standard DNS query (RFC 1035) with these questions, class IN.</summary>
        public static byte[] Query(IReadOnlyCollection<(string Name, ushort Type)> questions)
        {
            var q = new List<byte>(512) { 0, 0, 0, 0, (byte)(questions.Count >> 8), (byte)questions.Count, 0, 0, 0, 0, 0, 0 };
            foreach (var (name, type) in questions)
            {
                foreach (var label in name.Split('.'))
                {
                    var b = Encoding.UTF8.GetBytes(label);
                    q.Add((byte)b.Length);
                    q.AddRange(b);
                }
                q.Add(0);
                q.Add((byte)(type >> 8)); q.Add((byte)type);
                q.Add(0); q.Add(1);
            }
            return q.ToArray();
        }

        internal static uint ToUInt(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
        }
    }
}
