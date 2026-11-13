// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Network;

/// <summary>Why the watcher asks its subscribers to look at the network.</summary>
public enum NetworkCheckReason
{
    /// <summary>An interface gained or lost an address (settled for a moment first).</summary>
    AddressChanged,
    /// <summary>The fallback beat: catches a change no event reported.</summary>
    Heartbeat,
}

/// <summary>The order subscribers run in on each check: the modules first, then
/// corrections, then uploads, so they come up in the same order every time.</summary>
public enum NetworkStage
{
    Modules = 0,
    Corrections = 1,
    Uploads = 2,
}

/// <summary>
/// The one place that notices the network changing (#385). Services subscribe instead of
/// polling on timers of their own; a check only wakes them (find the modules, retry the
/// caster, send queued reports) and never tears down a working connection.
/// </summary>
public interface INetworkWatcher
{
    void Subscribe(NetworkStage stage, Action<NetworkCheckReason> onCheck);

    /// <summary>A platform that hears about network changes itself (Android, iOS) reports
    /// them here.</summary>
    void NotifyChanged();
}

/// <summary>
/// Hears <see cref="NetworkChange.NetworkAddressChanged"/> where the platform has it, plus
/// whatever a head reports through <see cref="NotifyChanged"/>. Changes come in bursts (one
/// Wi-Fi join fires several), so a check runs once they have settled for
/// <see cref="DefaultSettle"/>. A <see cref="DefaultHeartbeat"/> beat runs a check either way,
/// for platforms that report nothing.
/// Given the computer's links, it also logs them at the start and each time they differ
/// (on an event or a beat), so a bug report says which link went away and came back: the
/// modules' data stopped and resumed with a Windows address change while the modules still
/// heard the app (2026-11-13).
/// </summary>
public sealed class NetworkWatcher : INetworkWatcher, IDisposable
{
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(30);

    private readonly ILogger<NetworkWatcher> _logger;
    private readonly TimeSpan _settle;
    private readonly Timer _settleTimer;
    private readonly Timer _heartbeat;
    private readonly bool _systemEvents;
    private readonly object _gate = new();
    private readonly List<(NetworkStage Stage, Action<NetworkCheckReason> OnCheck)> _subscribers = new();
    private readonly ILocalNetworkInfoProvider? _links;
    private readonly object _linksGate = new();
    private string? _lastLinks;

    public NetworkWatcher(ILogger<NetworkWatcher>? logger = null, ILocalNetworkInfoProvider? links = null)
        : this(logger, systemEvents: true, DefaultSettle, DefaultHeartbeat, links) { }

    internal NetworkWatcher(ILogger<NetworkWatcher>? logger, bool systemEvents, TimeSpan settle, TimeSpan heartbeat,
        ILocalNetworkInfoProvider? links = null)
    {
        _logger = logger ?? NullLogger<NetworkWatcher>.Instance;
        _links = links;
        NoteLinks();
        _settle = settle;
        _settleTimer = new Timer(_ => Check(NetworkCheckReason.AddressChanged));
        _heartbeat = new Timer(_ => Check(NetworkCheckReason.Heartbeat), null, heartbeat, heartbeat);
        if (systemEvents)
        {
            try
            {
                NetworkChange.NetworkAddressChanged += OnSystemChange;
                _systemEvents = true;
            }
            catch (Exception ex) // iOS and Android: their heads report changes instead
            {
                _logger.LogDebug("[Network] no system change events: {Message}", ex.Message);
            }
        }
    }

    public void Subscribe(NetworkStage stage, Action<NetworkCheckReason> onCheck)
    {
        lock (_gate)
        {
            _subscribers.Add((stage, onCheck));
        }
    }

    public void NotifyChanged()
    {
        NoteLinks();
        _settleTimer.Change(_settle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Log the computer's IPv4 links when they differ from the last ones logged
    /// (the first call logs them as they are at the start).</summary>
    internal void NoteLinks()
    {
        if (_links is null) return;
        string now;
        try { now = DescribeLinks(_links.GetIPv4Addresses()); }
        catch (Exception ex)
        {
            _logger.LogDebug("[Network] links not read: {Message}", ex.Message);
            return;
        }
        lock (_linksGate)
        {
            if (now == _lastLinks) return;
            if (_lastLinks is null)
                _logger.LogInformation("[Network] links at the start: {Links}", now);
            else
                _logger.LogInformation("[Network] links now: {Links} (were: {Was})", now, _lastLinks);
            _lastLinks = now;
        }
    }

    /// <summary>"Ethernet 192.168.5.10/24, Wi-Fi 192.168.0.21/24", in name order; "none"
    /// when the computer has no IPv4 link up.</summary>
    internal static string DescribeLinks(IReadOnlyList<LocalNetworkAddress> addresses)
    {
        if (addresses.Count == 0) return "none";
        return string.Join(", ", addresses
            .Select(a => $"{(string.IsNullOrWhiteSpace(a.InterfaceName) ? "?" : a.InterfaceName)} {a.Address}/{a.PrefixLength}")
            .OrderBy(t => t, StringComparer.Ordinal));
    }

    private void OnSystemChange(object? sender, EventArgs e) => NotifyChanged();

    /// <summary>Run every subscriber, in stage order (then the order they subscribed), one
    /// check at a time.</summary>
    internal void Check(NetworkCheckReason reason)
    {
        NoteLinks();
        lock (_gate)
        {
            if (reason == NetworkCheckReason.AddressChanged)
            {
                string? links;
                lock (_linksGate) links = _lastLinks;
                if (links is null)
                    _logger.LogInformation("[Network] addresses changed");
                else
                    _logger.LogInformation("[Network] addresses changed; links: {Links}", links);
            }
            foreach (var (stage, onCheck) in _subscribers.OrderBy(s => s.Stage).ToList())
            {
                try { onCheck(reason); }
                catch (Exception ex) { _logger.LogWarning(ex, "[Network] {Stage} check failed", stage); }
            }
        }
    }

    public void Dispose()
    {
        if (_systemEvents)
        {
            try { NetworkChange.NetworkAddressChanged -= OnSystemChange; } catch { /* already gone */ }
        }
        _settleTimer.Dispose();
        _heartbeat.Dispose();
    }
}
