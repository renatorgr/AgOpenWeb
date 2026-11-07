// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using AgOpenWeb.Models;
using AgOpenWeb.Services.Network;
using static AgOpenWeb.Services.Network.MdnsHttpBrowser;

namespace AgOpenWeb.Services.Tests;

/// <summary>The web cards' "Search the network", the mDNS half: what the answers of the pages announcing
/// themselves (<c>_http._tcp</c>) are turned into, what is asked for, and how both halves are put together.</summary>
[TestFixture]
public class MdnsHttpBrowserTests
{
    private const string Sprayer = "Sprayer-1A2B._http._tcp.local";
    private static readonly HashSet<IPAddress> NoOwn = [];
    // The answers came in on 192.168.5.10/24.
    private const uint Via = 0xC0A8050A, Mask = 0xFFFFFF00;

    private static Answers Heard(params byte[][] messages)
    {
        var a = new Answers();
        foreach (var m in messages) a.Read(m, Via, Mask);
        return a;
    }

    // ── What a page's answers become ─────────────────────────────────────

    [Test]
    public void AnAnnouncedPage_IsListedWithItsNameAndAddress()
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 80, "sprayer.local")
            .Txt(Sprayer, "path=/").A("sprayer.local", "192.168.5.123").Bytes);

        Assert.That(a.Pages(NoOwn), Is.EqualTo(new[] { new FoundWebPage("Sprayer-1A2B", "http://192.168.5.123/") }));
        Assert.That(a.Missing(), Is.Empty, "nothing left to ask");
    }

    [Test]
    public void AnotherPortAndAPath_AreInTheAddress()
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 8080, "sprayer.local")
            .Txt(Sprayer, "v=1", "path=/ui").A("sprayer.local", "192.168.5.123").Bytes);

        Assert.That(a.Pages(NoOwn)[0].Url, Is.EqualTo("http://192.168.5.123:8080/ui"));
    }

    [TestCase("path=ui")]
    [TestCase("path=/a b")]
    [TestCase("path=")]
    [TestCase("name=Sprayer")]
    public void APathThatIsNoPath_LeavesTheRoot(string txt)
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 80, "sprayer.local")
            .Txt(Sprayer, txt).A("sprayer.local", "192.168.5.123").Bytes);

        Assert.That(a.Pages(NoOwn)[0].Url, Is.EqualTo("http://192.168.5.123/"));
    }

    [Test]
    public void APageOnTheHostItself_GetsHostInItsAddress()
    {
        var a = Heard(new Response().Ptr(ServiceName, "HAT._http._tcp.local").Srv("HAT._http._tcp.local", 8081, "aow-hat.local")
            .A("aow-hat.local", "192.168.5.10").Bytes);

        var pages = a.Pages(new HashSet<IPAddress> { IPAddress.Parse("192.168.5.10") });
        Assert.That(pages, Is.EqualTo(new[] { new FoundWebPage("HAT", "http://{host}:8081/") }));
    }

    [Test]
    public void TheRecords_MayComeInAnyOrderAndInSeveralMessages()
    {
        var a = Heard(
            new Response().A("sprayer.local", "192.168.5.123").Txt(Sprayer, "path=/").Srv(Sprayer, 80, "sprayer.local").Ptr(ServiceName, Sprayer).Bytes,
            new Response().Ptr(ServiceName, "aio._http._tcp.local").Bytes,
            new Response().Srv("aio._http._tcp.local", 80, "aio.local").Bytes,
            new Response().A("aio.local", "192.168.5.126").Bytes);

        Assert.That(a.Pages(NoOwn), Is.EqualTo(new[] {
            new FoundWebPage("aio", "http://192.168.5.126/"), new FoundWebPage("Sprayer-1A2B", "http://192.168.5.123/") }));
    }

    [Test]
    public void WhatTheAnswersLeftOut_IsAskedFor()
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Bytes);
        Assert.That(a.Missing(), Is.EqualTo(new[] { (Sprayer, TypeSrv), (Sprayer, TypeTxt) }));
        Assert.That(a.Pages(NoOwn), Is.Empty, "no host yet");

        a.Read(new Response().Srv(Sprayer, 80, "sprayer.local").Txt(Sprayer).Bytes, Via, Mask);
        Assert.That(a.Missing(), Is.EqualTo(new[] { ("sprayer.local", TypeA) }));
        Assert.That(a.Pages(NoOwn), Is.Empty, "no address yet");

        a.Read(new Response().A("sprayer.local", "192.168.5.123").Bytes, Via, Mask);
        Assert.That(a.Missing(), Is.Empty);
        Assert.That(a.Pages(NoOwn), Has.Count.EqualTo(1));
    }

    [Test]
    public void AGoodbye_TakesThePageOut()
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 80, "sprayer.local").A("sprayer.local", "192.168.5.123").Bytes);
        Assert.That(a.Pages(NoOwn), Has.Count.EqualTo(1));

        a.Read(new Response().Ptr(ServiceName, Sprayer, ttl: 0).Bytes, Via, Mask);
        Assert.That(a.Pages(NoOwn), Is.Empty);
    }

    [Test]
    public void AHostOnSeveralNetworks_IsListedAtItsAddressOnTheAskingOne()
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 80, "sprayer.local")
            .A("sprayer.local", "10.0.0.5").A("sprayer.local", "192.168.5.123").A("sprayer.local", "192.168.4.1").Bytes);

        Assert.That(a.Pages(NoOwn)[0].Url, Is.EqualTo("http://192.168.5.123/"));
    }

    [Test]
    public void AHostOnAnotherNetworkOnly_IsStillListed()
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 80, "sprayer.local").A("sprayer.local", "10.0.0.5").Bytes);

        Assert.That(a.Pages(NoOwn)[0].Url, Is.EqualTo("http://10.0.0.5/"));
    }

    [Test]
    public void CompressedNames_AreFollowed()
    {
        // The A record's owner "sprayer.local" is the first name in the message (offset 12); the SRV
        // target points at it, as responders write it.
        var a = Heard(new Response().A("sprayer.local", "192.168.5.123").Ptr(ServiceName, Sprayer)
            .Raw(Sprayer, TypeSrv, [0, 0, 0, 0, 0, 80, 0xC0, 12]).Bytes);

        Assert.That(a.Pages(NoOwn), Is.EqualTo(new[] { new FoundWebPage("Sprayer-1A2B", "http://192.168.5.123/") }));
    }

    [Test]
    public void TheInstanceName_IsABookmarkName()
    {
        var dotted = new[] { "Barn 2.Sprayer", "_http", "_tcp", "local" };
        var spaced = new[] { " Seed  drill\tmodule ", "_http", "_tcp", "local" };
        var longOne = new[] { new string('x', 60), "_http", "_tcp", "local" };
        var a = Heard(new Response()
            .Ptr(ServiceName, dotted).Srv(dotted, 80, "a.local").A("a.local", "192.168.5.1")
            .Ptr(ServiceName, spaced).Srv(spaced, 80, "b.local").A("b.local", "192.168.5.2")
            .Ptr(ServiceName, longOne).Srv(longOne, 80, "c.local").A("c.local", "192.168.5.3").Bytes);

        var names = a.Pages(NoOwn).Select(p => p.Title).ToList();
        Assert.That(names, Does.Contain("Barn 2.Sprayer"));
        Assert.That(names, Does.Contain("Seed drill module"));
        Assert.That(names.Single(n => n.StartsWith('x')), Has.Length.EqualTo(WebCardBookmarks.MaxName));
    }

    [Test]
    public void TwoInstancesOfOnePage_AreListedOnce()
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 80, "sprayer.local")
            .Ptr(ServiceName, "sprayer._http._tcp.local").Srv("sprayer._http._tcp.local", 80, "sprayer.local")
            .A("sprayer.local", "192.168.5.123").Bytes);

        Assert.That(a.Pages(NoOwn), Has.Count.EqualTo(1));
    }

    [Test]
    public void AnInstanceWithoutAService_IsNotAPage()
    {
        var a = Heard(new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 0, "sprayer.local").A("sprayer.local", "192.168.5.123").Bytes);
        Assert.That(a.Pages(NoOwn), Is.Empty);
    }

    [Test]
    public void RubbishAndCutMessages_AreIgnored()
    {
        var whole = new Response().Ptr(ServiceName, Sprayer).Srv(Sprayer, 80, "sprayer.local").A("sprayer.local", "192.168.5.123").Bytes;
        var a = Heard(
            [],
            [0x80, 0x81, 0x7F, 0xFE, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08],   // a PGN, not DNS
            whole[..(whole.Length / 2)],
            new Response().Query().Bytes,                                                    // someone else's question
            Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray());

        Assert.That(a.Pages(NoOwn), Is.Empty);
    }

    // ── The question ─────────────────────────────────────────────────────

    [Test]
    public void TheQuestion_AsksForTheInstancesOfTheService()
    {
        var q = Answers.Query([(ServiceName, TypePtr)]);

        Assert.That(q[..12], Is.EqualTo(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }), "a query with one question");
        Assert.That(q[12..], Is.EqualTo("\x05_http\x04_tcp\x05local\0".Select(c => (byte)c).Concat(new byte[] { 0, 12, 0, 1 })));
    }

    // ── The whole search ─────────────────────────────────────────────────

    [Test]
    public async Task WithoutANetwork_NothingIsFoundAndNothingBreaks()
    {
        var none = await MdnsHttpBrowser.BrowseAsync([], NoOwn, CancellationToken.None);
        var loopback = await MdnsHttpBrowser.BrowseAsync([new(IPAddress.Loopback, 8)], NoOwn, CancellationToken.None);

        Assert.That(none, Is.Empty);
        Assert.That(loopback, Is.Empty, "the loopback is never asked");
    }

    [Test]
    public void BothHalves_ListAPageOnce_UnderItsAnnouncedName()
    {
        var announced = new List<FoundWebPage> { new("sprayer", "http://192.168.5.123/") };
        var swept = new List<FoundWebPage> { new("Sprayer 8 m", "http://192.168.5.123/"), new("AiO Teensy", "http://192.168.5.126/"), new("", "http://192.168.5.1/") };

        var all = WebPageScanner.Merge(announced, swept);

        Assert.That(all.Select(p => p.Url), Is.EqualTo(new[] { "http://192.168.5.123/", "http://192.168.5.126/", "http://192.168.5.1/" }));
        Assert.That(all[0].Title, Is.EqualTo("sprayer"), "the announced name stays");
    }

    // ── A responder's message, built by hand (RFC 1035) ──────────────────

    private sealed class Response
    {
        private readonly List<byte> _b = [0, 0, 0x84, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        private int _answers;

        public byte[] Bytes => _b.ToArray();

        public Response Query() { _b[2] = 0; return this; }

        public Response Ptr(string owner, string instance, uint ttl = 120) => Ptr(owner, instance.Split('.'), ttl);
        public Response Ptr(string owner, string[] instance, uint ttl = 120) => Raw(owner, TypePtr, Name(instance), ttl);

        public Response Srv(string owner, int port, string target) => Srv(owner.Split('.'), port, target);
        public Response Srv(string[] owner, int port, string target) =>
            Raw(owner, TypeSrv, [0, 0, 0, 0, (byte)(port >> 8), (byte)port, .. Name(target.Split('.'))]);

        public Response Txt(string owner, params string[] entries)
        {
            var data = new List<byte>();
            foreach (var e in entries) { var u = Encoding.UTF8.GetBytes(e); data.Add((byte)u.Length); data.AddRange(u); }
            if (data.Count == 0) data.Add(0);
            return Raw(owner, TypeTxt, data.ToArray());
        }

        public Response A(string owner, string ip, uint ttl = 120) => Raw(owner, TypeA, IPAddress.Parse(ip).GetAddressBytes(), ttl);

        public Response Raw(string owner, ushort type, byte[] data, uint ttl = 120) => Raw(owner.Split('.'), type, data, ttl);
        public Response Raw(string[] owner, ushort type, byte[] data, uint ttl = 120)
        {
            _b.AddRange(Name(owner));
            _b.AddRange([(byte)(type >> 8), (byte)type, 0x80, 1, (byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl,
                (byte)(data.Length >> 8), (byte)data.Length]);
            _b.AddRange(data);
            _answers++;
            _b[6] = (byte)(_answers >> 8); _b[7] = (byte)_answers;
            return this;
        }

        private static byte[] Name(string[] labels)
        {
            var b = new List<byte>();
            foreach (var l in labels) { var u = Encoding.UTF8.GetBytes(l); b.Add((byte)u.Length); b.AddRange(u); }
            b.Add(0);
            return b.ToArray();
        }
    }
}
