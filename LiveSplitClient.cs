using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace RedAllianceSpeedrun
{
    // TCP client for the LiveSplit Server component (https://github.com/LiveSplit/LiveSplit.Server).
    // User installs the Server component into LiveSplit, picks a port (default 16834), clicks
    // "Start Server". We connect lazily and send newline-terminated commands:
    //
    //   starttimer / split / unsplit / skipsplit / reset
    //   pause / resume / pausegametime / unpausegametime
    //   initgametime
    //   setgametime <h:mm:ss.fff>
    //
    // Connection failures are silently swallowed and retried on the next send (debounced
    // by ReconnectCooldown). Mod never blocks game thread on socket I/O — sends are short
    // and use a write timeout.
    internal static class LiveSplitClient
    {
        private const float ReconnectCooldown = 2f;

        private static TcpClient _tcp;
        private static StreamWriter _writer;
        private static readonly object _lock = new object();
        private static float _lastFailureTime = -999f;
        private static bool _gameTimeInitialized;

        public static bool IsConnected
        {
            get { lock (_lock) { return _tcp != null && _tcp.Connected; } }
        }

        public static void Disconnect()
        {
            lock (_lock)
            {
                try { _writer?.Dispose(); } catch { }
                try { _tcp?.Close(); } catch { }
                _writer = null;
                _tcp = null;
                _gameTimeInitialized = false;
            }
        }

        public static void Send(string command)
        {
            if (!Plugin.LiveSplitEnabled) return;
            if (string.IsNullOrEmpty(command)) return;

            lock (_lock)
            {
                if (!EnsureConnected()) return;
                try
                {
                    _writer.Write(command);
                    _writer.Write("\r\n");
                    _writer.Flush();
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning("[livesplit] send failed: " + e.Message);
                    DropConnectionLocked();
                }
            }
        }

        public static void StartTimer()
        {
            Send("starttimer");
            _gameTimeInitialized = false; // re-init game time on next SetGameTime
        }

        public static void Split() => Send("split");
        public static void Reset() => Send("reset");
        public static void Pause() => Send("pause");
        public static void Resume() => Send("resume");

        public static void SetGameTime(double seconds)
        {
            if (!Plugin.LiveSplitEnabled) return;
            if (!_gameTimeInitialized)
            {
                Send("initgametime");
                _gameTimeInitialized = true;
            }
            if (seconds < 0) seconds = 0;
            int totalMs = (int)Math.Round(seconds * 1000.0);
            int ms = totalMs % 1000;
            int totalSec = totalMs / 1000;
            int sec = totalSec % 60;
            int totalMin = totalSec / 60;
            int min = totalMin % 60;
            int hr = totalMin / 60;
            Send($"setgametime {hr}:{min:D2}:{sec:D2}.{ms:D3}");
        }

        private static bool EnsureConnected()
        {
            if (_tcp != null && _tcp.Connected) return true;
            float now = UnityEngine.Time.unscaledTime;
            if (now - _lastFailureTime < ReconnectCooldown) return false;

            try
            {
                _tcp = new TcpClient();
                _tcp.SendTimeout = 250;
                _tcp.NoDelay = true;
                var connectTask = _tcp.BeginConnect(Plugin.LiveSplitHost, Plugin.LiveSplitPort, null, null);
                bool ok = connectTask.AsyncWaitHandle.WaitOne(250);
                if (!ok || !_tcp.Connected)
                {
                    DropConnectionLocked();
                    _lastFailureTime = now;
                    return false;
                }
                _tcp.EndConnect(connectTask);
                _writer = new StreamWriter(_tcp.GetStream()) { AutoFlush = false };
                _gameTimeInitialized = false;
                Plugin.Logger.LogInfo($"[livesplit] connected to {Plugin.LiveSplitHost}:{Plugin.LiveSplitPort}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogInfo("[livesplit] connect failed (will retry): " + e.Message);
                DropConnectionLocked();
                _lastFailureTime = now;
                return false;
            }
        }

        private static void DropConnectionLocked()
        {
            try { _writer?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            _writer = null;
            _tcp = null;
            _gameTimeInitialized = false;
        }
    }
}
