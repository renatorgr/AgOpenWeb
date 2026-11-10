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
/// an AiO board's page, a XIAO's, a sprayer or seed drill module's, a printer's) and has them in a second or
/// two, each with the name its maker gave it. <see cref="WebPageScanner"/> then sweeps every address for
/// the pages that announce nothing.
/// <para>The question is asked both ways a module may answer, and more than once, since a multicast
/// question is sent over Wi-Fi once, unacknowledged: from an ordinary port, which a responder answers by
/// unicast (RFC 6762 section 6.7), and from port 5353 shared with the system's own mDNS, which it answers
/// to the group, as it does for the operating system. A page the sweep found whose module was not heard
/// is then asked for its name directly, at its own address (<see cref="AskHostsAsync"/>): no multicast
/// in that, for a network that keeps multicast off its Wi-Fi. Where multicast is not allowed (iOS
/// without the entitlement) the first question fails quietly.</para>
/// </summary>
public static class MdnsHttpBrowser
{
    public const string ServiceType = "_http._tcp";
    internal const string ServiceName = ServiceType + ".local";
    internal const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;
    internal const int MdnsPort = 5353;
    private static readonly IPAddress GroupAddress = IPAddress.Parse("224.0.0.251");
    // When a question is sent, in ms from its first sending, and how long its answers are awaited. A Wi-Fi
    // access point holds a multicast frame for its next beacons (up to 0.3 s) and a responder waits up to
    // 0.2 s before it answers, so the last sending leaves 0.7 s.
    private static readonly int[] FirstAsks = [0, 250, 700], MoreAsks = [0, 250], DirectAsks = [0, 200];
    private const int FirstListen = 1400, MoreListen = 600, DirectListen = 500;
    private const int MaxMessage = 9000;

    /// <summary>
    /// Asks every IPv4 network in <paramref name="local"/> for the pages announcing themselves and returns
    /// them, by name, in about one and a half to three seconds. A page on one of the host's own addresses
    /// gets <c>{host}</c> in its URL, as the sweep gives it. Never throws for a network that refuses: it
    /// returns what it heard. <paramref name="note"/> gets one line on what was asked and who answered,
    /// for the log.
    /// </summary>
    public static Task<List<FoundWebPage>> BrowseAsync(IEnumerable<LocalNetworkAddress> local, ISet<IPAddress> own,
        CancellationToken ct, Action<string>? note = null) =>
        Task.Run(() => Browse(local, own, ct, note), ct);

    /// <summary>
    /// Asks each of <paramref name="hosts"/> at its own address (a question sent straight to a responder's
    /// port 5353 is answered straight back, RFC 6762 section 5.5) for the pages it announces: the names of
    /// the modules the sweep found and the multicast question did not reach. Half a second.
    /// </summary>
    public static Task<List<FoundWebPage>> AskHostsAsync(IEnumerable<LocalNetworkAddress> local, ISet<IPAddress> own,
        IReadOnlyCollection<IPAddress> hosts, CancellationToken ct, Action<string>? note = null) =>
        Task.Run(() => AskHosts(local, own, hosts, MdnsPort, false, ct, note), ct);

    /// <summary>The addresses of the pages in <paramref name="swept"/> that no page in
    /// <paramref name="announced"/> is at: the hosts to ask for their name directly.</summary>
    public static List<IPAddress> Unnamed(IEnumerable<FoundWebPage> announced, IEnumerable<FoundWebPage> swept)
    {
        var named = new HashSet<IPAddress>(announced.Select(HostOf).OfType<IPAddress>());
        return swept.Select(HostOf).OfType<IPAddress>().Where(a => !named.Contains(a)).Distinct().ToList();
    }

    private static IPAddress? HostOf(FoundWebPage page) =>
        Uri.TryCreate(page.Url, UriKind.Absolute, out var u) && u.HostNameType == UriHostNameType.IPv4
            && IPAddress.TryParse(u.Host, out var a) ? a : null;

    private static List<FoundWebPage> Browse(IEnumerable<LocalNetworkAddress> local, ISet<IPAddress> own, CancellationToken ct, Action<string>? note)
    {
        var answers = new Answers();
        using var link = new Link(local, false, shared: MdnsPort);
        if (link.Own.Count == 0) return [];
        var heard = new Heard();
        // The instances first; then what their answers left out (an instance's SRV and TXT, its host's
        // address), twice at most, for a responder that sends the records one question at a time.
        Ask(link, Answers.Query([(ServiceName, TypePtr)]), null, MdnsPort, FirstAsks, FirstListen, answers, heard, null, ct);
        for (int round = 0; round < 2 && !ct.IsCancellationRequested; round++)
        {
            var missing = answers.Missing();
            if (missing.Count == 0) break;
            Ask(link, Answers.Query(missing), null, MdnsPort, MoreAsks, MoreListen, answers, heard, () => answers.Missing().Count == 0, ct);
        }
        var pages = answers.Pages(own);
        note?.Invoke($"mDNS: asked {ServiceName} on {string.Join(", ", link.Own.Select(o => o.Net.Ip))} "
            + $"(own port{(link.Shared != null ? " and port 5353" : "; port 5353 not available")}); {heard}; {Listed(pages)}");
        return pages;
    }

    internal static List<FoundWebPage> AskHosts(IEnumerable<LocalNetworkAddress> local, ISet<IPAddress> own,
        IReadOnlyCollection<IPAddress> hosts, int port, bool loopback, CancellationToken ct, Action<string>? note)
    {
        var answers = new Answers();
        using var link = new Link(local, loopback, shared: 0);
        // Only hosts on one of the host's own networks: the question leaves by that network's socket.
        var asked = hosts.Where(h => h.AddressFamily == AddressFamily.InterNetwork && link.For(Answers.ToUInt(h)) != null).Distinct().ToList();
        if (asked.Count == 0) return [];
        var heard = new Heard();
        Ask(link, Answers.Query([(ServiceName, TypePtr)]), asked, port, DirectAsks, DirectListen, answers, heard,
            () => asked.All(heard.From), ct);
        var missing = answers.Missing();
        if (missing.Count > 0 && !ct.IsCancellationRequested)
            Ask(link, Answers.Query(missing), asked, port, DirectAsks, DirectListen, answers, heard, () => answers.Missing().Count == 0, ct);
        var pages = answers.Pages(own);
        note?.Invoke($"mDNS: asked {string.Join(", ", asked)} directly; {heard}; {Listed(pages)}");
        return pages;
    }

    private static string Listed(List<FoundWebPage> pages) =>
        pages.Count == 0 ? "no pages" : "pages: " + string.Join(", ", pages.Select(p => $"{p.Title} {p.Url}"));

    // One of the host's IPv4 addresses and its network.
    private readonly record struct Net(IPAddress Ip, uint Address, uint Mask)
    {
        public bool Holds(uint a) => Mask != 0 && (a & Mask) == (Address & Mask);
    }

    // Who answered, and on which socket: the line for the log tells which way a module was heard.
    private sealed class Heard
    {
        private readonly SortedDictionary<uint, (bool Own, bool Shared)> _from = [];

        public void Add(uint address, bool shared)
        {
            _from.TryGetValue(address, out var w);
            _from[address] = shared ? (w.Own, true) : (true, w.Shared);
        }

        public bool From(IPAddress a) => _from.ContainsKey(Answers.ToUInt(a));

        public override string ToString() => _from.Count == 0 ? "no answers" : "answers from " + string.Join(", ", _from.Select(kv =>
            $"{kv.Key >> 24}.{kv.Key >> 16 & 255}.{kv.Key >> 8 & 255}.{kv.Key & 255} ({(kv.Value.Own ? kv.Value.Shared ? "own port, port 5353" : "own port" : "port 5353")})"));
    }

    // The sockets of one search: one per IPv4 address of the host on an ordinary port, so a question goes
    // out on each network and its unicast answers come back there; and, for a multicast search, one on the
    // mDNS port itself, shared with whatever else on the host speaks mDNS and joined to the group on every
    // address, which hears the answers sent to the group.
    private sealed class Link : IDisposable
    {
        public readonly List<(Socket Socket, Net Net)> Own = [];
        public readonly Socket? Shared;
        public readonly List<Net> SharedNets = [];

        public Link(IEnumerable<LocalNetworkAddress> local, bool loopback, int shared)
        {
            foreach (var a in local)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || (!loopback && IPAddress.IsLoopback(a.Address))) continue;
                if (Own.Exists(o => o.Net.Ip.Equals(a.Address))) continue;
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    s.Bind(new IPEndPoint(a.Address, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.Address.GetAddressBytes());
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    QuietResets(s);
                    uint mask = a.PrefixLength is >= 1 and <= 32 ? uint.MaxValue << (32 - a.PrefixLength) : 0xFFFFFF00;
                    Own.Add((s, new Net(a.Address, Answers.ToUInt(a.Address), mask)));
                }
                catch (SocketException) { s.Dispose(); }
            }
            if (shared > 0 && Own.Count > 0) Shared = OpenShared(shared);
        }

        // Port 5353 is the system's (Windows' DNS client, avahi, mDNSResponder) and other programs': it is
        // taken with address reuse, as they take it, and left alone when that is refused.
        private Socket? OpenShared(int port)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(IPAddress.Any, port));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                QuietResets(s);
                foreach (var (_, net) in Own)
                {
                    try
                    {
                        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GroupAddress, net.Ip));
                        SharedNets.Add(net);
                    }
                    catch (SocketException) { }   // no multicast on this network
                }
                if (SharedNets.Count > 0) return s;
            }
            catch (SocketException) { }
            s.Dispose();
            return null;
        }

        // On Windows an "unreachable" answer to a question (a host with no mDNS) makes the socket's next
        // read fail as a reset connection; the reads here also skip a failed read.
        private static void QuietResets(Socket s)
        {
            if (!OperatingSystem.IsWindows()) return;
            try { s.IOControl(unchecked((int)0x9800000C), [0], null); }   // SIO_UDP_CONNRESET off
            catch (Exception e) when (e is SocketException or InvalidOperationException or NotSupportedException) { }
        }

        /// <summary>The socket of the network <paramref name="address"/> is on, or none.</summary>
        public (Socket Socket, Net Net)? For(uint address)
        {
            foreach (var o in Own) if (o.Net.Holds(address)) return o;
            return null;
        }

        public List<Socket> Sockets()
        {
            var list = Own.Select(o => o.Socket).ToList();
            if (Shared != null) list.Add(Shared);
            return list;
        }

        public void Dispose()
        {
            foreach (var o in Own) o.Socket.Dispose();
            Shared?.Dispose();
        }
    }

    // Sends the query at each of the times in asks, to the group on every network (or to each of hosts
    // at its own address), and reads the answers until the time is up or done says there is nothing left to wait for.
    private static void Ask(Link link, byte[] query, List<IPAddress>? hosts, int port, int[] asks, int listen,
        Answers answers, Heard heard, Func<bool>? done, CancellationToken ct)
    {
        var buffer = new byte[MaxMessage];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        long start = Environment.TickCount64;
        int sent = 0;
        while (!ct.IsCancellationRequested)
        {
            long now = Environment.TickCount64 - start;
            if (sent < asks.Length && now >= asks[sent])
            {
                // A host that answered is not asked again.
                Send(link, query, hosts?.Where(h => !heard.From(h)).ToList(), port);
                sent++;
            }
            if (now >= listen) break;
            long wait = Math.Min(listen - now, sent < asks.Length ? asks[sent] - now : long.MaxValue);
            var ready = link.Sockets();
            try { Socket.Select(ready, null, null, (int)Math.Clamp(wait, 1, 100) * 1000); }
            catch (SocketException) { break; }
            foreach (var sock in ready)
            {
                int n;
                try { n = sock.ReceiveFrom(buffer, ref from); }
                catch (SocketException) { continue; }
                uint sender = from is IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ep ? Answers.ToUInt(ep.Address) : 0;
                bool shared = sock == link.Shared;
                // The network the answer came in on: the socket's own, or for the shared one the sender's.
                var via = shared ? link.For(sender)?.Net ?? default : link.Own.Find(o => o.Socket == sock).Net;
                if (answers.Read(buffer.AsSpan(0, n), via.Address, via.Mask)) heard.Add(sender, shared);
            }
            if (done != null && done()) break;
        }
    }

    private static void Send(Link link, byte[] query, List<IPAddress>? hosts, int port)
    {
        if (hosts != null)
        {
            foreach (var h in hosts)
            {
                try { link.For(Answers.ToUInt(h))?.Socket.SendTo(query, new IPEndPoint(h, port)); }
                catch (SocketException) { }
            }
            return;
        }
        var group = new IPEndPoint(GroupAddress, port);
        foreach (var (socket, _) in link.Own)
        {
            try { socket.SendTo(query, group); }
            catch (SocketException) { }   // no multicast on this network
        }
        if (link.Shared == null) return;
        foreach (var net in link.SharedNets)
        {
            try
            {
                link.Shared.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, net.Ip.GetAddressBytes());
                link.Shared.SendTo(query, group);
            }
            catch (SocketException) { }
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

        /// <summary>Takes one message's records in. Anything that is not a DNS response, or is cut short, is
        /// ignored. True when the message said something about the service: an instance of it, an instance's
        /// host or text, or the address of an instance's host.</summary>
        public bool Read(ReadOnlySpan<byte> m, uint viaAddress, uint viaMask)
        {
            try { return ReadRecords(m, viaAddress, viaMask); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException or ArgumentException) { return false; }
        }

        private bool ReadRecords(ReadOnlySpan<byte> m, uint viaAddress, uint viaMask)
        {
            if (m.Length < 12 || (m[2] & 0x80) == 0) return false;   // not a response
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
                if (p + length > m.Length) return false;
                found.Add((owner, type, ttl, p, p + length));
                p += length;
            }
            bool about = false;
            // The instances first, whatever the order in the message, so their SRV, TXT and A records are kept.
            foreach (var (owner, type, ttl, data, _) in found)
            {
                if (type != TypePtr || !owner.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)) continue;
                about = true;
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
                        about = true;
                        i.Port = m[at + 4] << 8 | m[at + 5];
                        at += 6;
                        var target = string.Join('.', ReadLabels(m, ref at));
                        i.Target = target == "." || target.Length == 0 ? "" : target;
                        break;
                    case TypeTxt when _instances.TryGetValue(owner, out var i):
                    {
                        about = true;
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
            // An address alone counts when it is the address of an instance's host (the answer to a later question).
            return about || found.Any(f => f.Type == TypeA && _instances.Values.Any(i => i.Target.Equals(f.Owner, StringComparison.OrdinalIgnoreCase)));
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
