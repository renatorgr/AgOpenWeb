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
/// </summary>
public sealed class ImplementGpsReceiver : IDisposable
{
    /// <summary>The port the board sends to (renatorgr/Boards, the firmware's <c>zImplement.ino</c>).</summary>
    public const int Port = 9990;
    private const int MaxQueued = 100;    // 10 s at 10 Hz; the host loop takes them at every fix

    private readonly ConcurrentQueue<(ImplementGpsFix Fix, long ArrivedMs)> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private long _datagrams, _sentences, _refused;
    private volatile string? _sender;
    private volatile string? _error;
    private volatile string? _lastType;

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
            if (got.RemoteEndPoint is IPEndPoint from) _sender = from.Address.ToString();
            Take(buffer.AsSpan(0, got.ReceivedBytes), Environment.TickCount64);
        }
    }

    /// <summary>One datagram: one sentence, or several, each on its own line. Public for tests.</summary>
    public void Take(ReadOnlySpan<byte> datagram, long nowMs)
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
            _lastType = fix.IsPaogi ? "PAOGI" : "PANDA";
            _queue.Enqueue((fix, nowMs));
            while (_queue.Count > MaxQueued && _queue.TryDequeue(out _)) { }
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
    /// <summary>The address the last datagram came from.</summary>
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
