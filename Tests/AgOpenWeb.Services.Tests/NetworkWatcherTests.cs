// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Network;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #385: one watcher for network changes. Subscribers run in a fixed order (modules,
/// corrections, uploads) whatever order they subscribed in, a burst of changes gives one
/// check once it settles, and the heartbeat checks with no change at all.
/// </summary>
[TestFixture]
public class NetworkWatcherTests
{
    [Test]
    public void Subscribers_RunInStageOrder()
    {
        using var w = new NetworkWatcher(null, systemEvents: false, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        var order = new List<string>();
        w.Subscribe(NetworkStage.Uploads, _ => order.Add("uploads"));
        w.Subscribe(NetworkStage.Modules, _ => order.Add("modules"));
        w.Subscribe(NetworkStage.Corrections, _ => order.Add("corrections"));

        w.Check(NetworkCheckReason.AddressChanged);

        Assert.That(order, Is.EqualTo(new[] { "modules", "corrections", "uploads" }));
    }

    [Test]
    public void AFailingSubscriber_DoesNotStopTheOthers()
    {
        using var w = new NetworkWatcher(null, systemEvents: false, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        bool uploads = false;
        w.Subscribe(NetworkStage.Modules, _ => throw new InvalidOperationException("boom"));
        w.Subscribe(NetworkStage.Uploads, _ => uploads = true);

        w.Check(NetworkCheckReason.Heartbeat);

        Assert.That(uploads, Is.True);
    }

    [Test]
    public void ABurstOfChanges_GivesOneCheck_OnceSettled()
    {
        using var w = new NetworkWatcher(null, systemEvents: false, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
        int checks = 0;
        w.Subscribe(NetworkStage.Modules, r => { if (r == NetworkCheckReason.AddressChanged) Interlocked.Increment(ref checks); });

        for (int i = 0; i < 5; i++) { w.NotifyChanged(); Thread.Sleep(50); }
        Assert.That(Volatile.Read(ref checks), Is.EqualTo(0), "still settling");

        Thread.Sleep(600);
        Assert.That(Volatile.Read(ref checks), Is.EqualTo(1));
    }

    [Test]
    public void TheHeartbeat_ChecksWithoutAnyChange()
    {
        using var w = new NetworkWatcher(null, systemEvents: false, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        int beats = 0;
        w.Subscribe(NetworkStage.Uploads, r => { if (r == NetworkCheckReason.Heartbeat) Interlocked.Increment(ref beats); });

        Thread.Sleep(450);

        Assert.That(Volatile.Read(ref beats), Is.GreaterThanOrEqualTo(2));
    }

    // 2026-11-13: the modules' data stopped and came back with "[Network] addresses changed"
    // while the modules still heard the app; the log did not say which link it was.

    [Test]
    public void DescribeLinks_NamesEachLink_InNameOrder()
    {
        var text = NetworkWatcher.DescribeLinks(new[]
        {
            new LocalNetworkAddress(IPAddress.Parse("192.168.0.21"), 24, "Wi-Fi"),
            new LocalNetworkAddress(IPAddress.Parse("192.168.5.10"), 24, "Ethernet 2"),
            new LocalNetworkAddress(IPAddress.Parse("10.0.0.5"), 8, null),
        });

        Assert.That(text, Is.EqualTo("? 10.0.0.5/8, Ethernet 2 192.168.5.10/24, Wi-Fi 192.168.0.21/24"));
        Assert.That(NetworkWatcher.DescribeLinks(Array.Empty<LocalNetworkAddress>()), Is.EqualTo("none"));
    }

    [Test]
    public void TheLinks_AreLoggedAtTheStart_AndEachTimeTheyDiffer_NotWhenTheyStayTheSame()
    {
        var links = new FakeLinks(("Ethernet 2", "192.168.5.10"), ("Wi-Fi", "192.168.0.21"));
        var log = new CaptureLogger();
        using var w = new NetworkWatcher(log, systemEvents: false, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan, links);

        Assert.That(log.Messages, Is.EqualTo(new[]
        {
            "[Network] links at the start: Ethernet 2 192.168.5.10/24, Wi-Fi 192.168.0.21/24",
        }));

        w.NotifyChanged();                          // an event with nothing different: no line
        links.Set(("Ethernet 2", "192.168.5.10"));  // the Wi-Fi drops
        w.NotifyChanged();
        links.Set(("Ethernet 2", "192.168.5.10"), ("Wi-Fi", "192.168.0.21"));  // and comes back
        w.NotifyChanged();
        w.Check(NetworkCheckReason.Heartbeat);      // a beat with nothing different: no line

        Assert.That(log.Messages.Skip(1), Is.EqualTo(new[]
        {
            "[Network] links now: Ethernet 2 192.168.5.10/24 (were: Ethernet 2 192.168.5.10/24, Wi-Fi 192.168.0.21/24)",
            "[Network] links now: Ethernet 2 192.168.5.10/24, Wi-Fi 192.168.0.21/24 (were: Ethernet 2 192.168.5.10/24)",
        }));
    }

    [Test]
    public void TheSettledCheck_SaysTheLinksItSees()
    {
        var links = new FakeLinks(("Ethernet 2", "192.168.5.10"));
        var log = new CaptureLogger();
        using var w = new NetworkWatcher(log, systemEvents: false, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan, links);

        links.Set();  // the cable is pulled; nothing reported it, the check finds it
        w.Check(NetworkCheckReason.AddressChanged);

        Assert.That(log.Messages.Skip(1), Is.EqualTo(new[]
        {
            "[Network] links now: none (were: Ethernet 2 192.168.5.10/24)",
            "[Network] addresses changed; links: none",
        }));
    }

    [Test]
    public void WithoutTheLinks_TheWatcherLogsAsBefore()
    {
        var log = new CaptureLogger();
        using var w = new NetworkWatcher(log, systemEvents: false, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);

        w.Check(NetworkCheckReason.AddressChanged);

        Assert.That(log.Messages, Is.EqualTo(new[] { "[Network] addresses changed" }));
    }

    private sealed class FakeLinks : ILocalNetworkInfoProvider
    {
        private volatile LocalNetworkAddress[] _now = Array.Empty<LocalNetworkAddress>();
        public FakeLinks(params (string Name, string Ip)[] links) => Set(links);
        public void Set(params (string Name, string Ip)[] links) =>
            _now = links.Select(l => new LocalNetworkAddress(IPAddress.Parse(l.Ip), 24, l.Name)).ToArray();
        public IReadOnlyList<LocalNetworkAddress> GetIPv4Addresses() => _now;
    }

    private sealed class CaptureLogger : ILogger<NetworkWatcher>
    {
        private readonly List<string> _messages = new();
        public IReadOnlyList<string> Messages { get { lock (_messages) return _messages.ToList(); } }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            lock (_messages) _messages.Add(formatter(state, ex));
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
