// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models.GPS;

namespace AgOpenWeb.Services.Implement;

/// <summary>
/// Listens on UDP <see cref="Port"/> for the GPS on a trailed implement (fork only): an AiO board in its "Implement" role
/// sends its <c>$PANDA</c> / <c>$PAOGI</c> there and nothing to the modules' 9999, so the app's steering never hears it.
/// The fixes wait in a queue for the host loop (<see cref="TryTake"/>); nothing here touches the app's state.
/// <para>The sentences carry no signature (nothing in this protocol does), so anything on the tractor's network can
/// send them. What is kept out here: a second sender while the first is heard (two boards left in the Implement role
/// would be drawn as one implement jumping between two places), the same fix a second time (a datagram that came by
/// two links), and more fixes a second than any receiver sends (a flood would fill the file and keep the host loop
/// busy). Each is counted, for Network IO.</para>
/// </summary>
public sealed class ImplementGpsReceiver : IDisposable
{
    /// <summary>The port the board sends to (renatorgr/Boards, the firmware's <c>zImplement.ino</c>).</summary>
    public const int Port = 9990;
    private const int MaxQueued = 100;    // 10 s at 10 Hz; the host loop takes them at every fix
    /// <summary>The sender followed is kept this long after its last fix; then the next one heard is followed.</summary>
    public const long SenderHoldMs = 5000;
    /// <summary>More fixes than this in a second are not taken (receivers send 10, at most 20).</summary>
    public const int MaxPerSecond = 50;

    private readonly ConcurrentQueue<(ImplementGpsFix Fix, long ArrivedMs)> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private long _datagrams, _sentences, _refused, _repeated, _fromOthers, _lost, _otherMs;
    private volatile string? _sender;
    private volatile string? _otherSender;
    private volatile string? _error;
    private volatile string? _lastType;
    // read and written by Take only (the receive loop, one thread)
    private IPAddress? _followed;
    private long _followedMs;
    private int _lastTimeCs = -1;
    private double _lastLat, _lastLon;
    private bool _lastPaogi;
    private long _windowMs;
    private int _windowCount;

    /// <summary>Opens the port and starts listening. False (and <see cref="Error"/>) when it can't be opened.</summary>
    public bool Start()
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // no ReuseAddress: another program on the port is said ("could not be opened"), not shared
            s.Bind(new IPEndPoint(IPAddress.Any, Port));
            _socket = s;
            _ = Task.Run(() => ReceiveLoopAsync(s, _stop.Token));
            _error = null;
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _error = $"UDP {Port} could not be opened: {ex.Message}";
            return false;
        }
    }

    private async Task ReceiveLoopAsync(Socket s, CancellationToken token)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult got;
            try { got = await s.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }   // a port unreachable from a send of ours on Windows
            Interlocked.Increment(ref _datagrams);
            Take(buffer.AsSpan(0, got.ReceivedBytes), Environment.TickCount64, (got.RemoteEndPoint as IPEndPoint)?.Address);
        }
    }

    /// <summary>One datagram: one sentence, or several, each on its own line. <paramref name="from"/> is who sent it
    /// (null: not looked at). Public for tests.</summary>
    public void Take(ReadOnlySpan<byte> datagram, long nowMs, IPAddress? from = null)
    {
        while (datagram.Length > 0)
        {
            int nl = datagram.IndexOf((byte)'\n');
            var line = nl < 0 ? datagram : datagram[..nl];
            datagram = nl < 0 ? ReadOnlySpan<byte>.Empty : datagram[(nl + 1)..];
            if (line.Length < 2) continue;
            Interlocked.Increment(ref _sentences);
            if (!ImplementGpsSentence.TryParse(line, out var fix))
            {
                Interlocked.Increment(ref _refused);
                continue;
            }
            if (from != null)
            {
                // one sender at a time: the first heard, until it is silent for SenderHoldMs
                if (_followed == null || nowMs - _followedMs > SenderHoldMs) _followed = from;
                if (!from.Equals(_followed))
                {
                    _otherSender = from.ToString();
                    Interlocked.Exchange(ref _otherMs, nowMs);
                    Interlocked.Increment(ref _fromOthers);
                    continue;
                }
                _followedMs = nowMs;
                _sender = from.ToString();
            }
            // the same fix again (the datagram came by two links)
            if (fix.TimeCentiseconds >= 0 && fix.TimeCentiseconds == _lastTimeCs && fix.IsPaogi == _lastPaogi
                && fix.Latitude == _lastLat && fix.Longitude == _lastLon)
            {
                Interlocked.Increment(ref _repeated);
                continue;
            }
            _lastTimeCs = fix.TimeCentiseconds; _lastPaogi = fix.IsPaogi; _lastLat = fix.Latitude; _lastLon = fix.Longitude;
            if (nowMs - _windowMs >= 1000 || nowMs < _windowMs) { _windowMs = nowMs; _windowCount = 0; }
            if (++_windowCount > MaxPerSecond)
            {
                Interlocked.Increment(ref _lost);
                continue;
            }
            _lastType = fix.IsPaogi ? "PAOGI" : "PANDA";
            _queue.Enqueue((fix, nowMs));
            // the host loop did not take them for 10 s: the oldest go, counted
            while (_queue.Count > MaxQueued && _queue.TryDequeue(out _)) Interlocked.Increment(ref _lost);
        }
    }

    /// <summary>The next fix heard, with when (<see cref="Environment.TickCount64"/>).</summary>
    public bool TryTake(out ImplementGpsFix fix, out long arrivedMs)
    {
        if (_queue.TryDequeue(out var item)) { fix = item.Fix; arrivedMs = item.ArrivedMs; return true; }
        fix = default; arrivedMs = 0; return false;
    }

    /// <summary>Datagrams received.</summary>
    public long Datagrams => Interlocked.Read(ref _datagrams);
    /// <summary>Lines in them.</summary>
    public long Sentences => Interlocked.Read(ref _sentences);
    /// <summary>Lines that were not a <c>$PANDA</c> / <c>$PAOGI</c> with a right checksum.</summary>
    public long Refused => Interlocked.Read(ref _refused);
    /// <summary>Fixes that came a second time and were taken once.</summary>
    public long Repeated => Interlocked.Read(ref _repeated);
    /// <summary>Fixes from another address while the sender followed was heard.</summary>
    public long FromOthers => Interlocked.Read(ref _fromOthers);
    /// <summary>That other address, while it is still sending (its last fix within <see cref="SenderHoldMs"/>); else null.</summary>
    public string? OtherSenderAt(long nowMs)
        => _otherSender is { } o && nowMs - Interlocked.Read(ref _otherMs) <= SenderHoldMs ? o : null;
    /// <summary>Fixes not taken: more than <see cref="MaxPerSecond"/> a second, or the host loop left them 10 s.</summary>
    public long Lost => Interlocked.Read(ref _lost);
    /// <summary>The address followed: where the last fix taken came from.</summary>
    public string? Sender => _sender;
    /// <summary>Why the port is not open; null when it is.</summary>
    public string? Error => _error;
    /// <summary>PANDA or PAOGI, the last sentence read.</summary>
    public string? LastType => _lastType;

    public void Dispose()
    {
        _stop.Cancel();
        try { _socket?.Dispose(); } catch (ObjectDisposedException) { }
        _socket = null;
    }
}
