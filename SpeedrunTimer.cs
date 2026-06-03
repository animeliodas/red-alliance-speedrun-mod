using System;

namespace RedAllianceSpeedrun
{
    // Speedrun timer with configurable pause conditions.
    //   - RTA mode: never pauses. Runs from speedrun start until the credits scene load.
    //   - IGT mode: pauses while a scene is loading (SceneManagerScript.LoadingLevel) and
    //     while the pause menu is up (PauseMenuScript.Paused).
    // Both reset on TAB press, both start once the post-TAB scene load completes, both
    // stop when the credits scene loads.
    internal sealed class SpeedrunTimer
    {
        private double _elapsedSeconds;
        private bool _running;
        private readonly bool _pauseDuringLoad;
        private readonly bool _pauseDuringMenu;
        public readonly string Label;

        public SpeedrunTimer(string label, bool pauseDuringLoad, bool pauseDuringMenu)
        {
            Label = label;
            _pauseDuringLoad = pauseDuringLoad;
            _pauseDuringMenu = pauseDuringMenu;
        }

        public double Elapsed => _elapsedSeconds;
        public bool Running => _running;

        public void Reset(double initialSeconds)
        {
            _elapsedSeconds = initialSeconds;
            _running = false;
        }

        public void Start() => _running = true;
        public void Stop() => _running = false;

        public void Tick(double dt, bool isLoading, bool isPaused)
        {
            if (!_running) return;
            if (_pauseDuringLoad && isLoading) return;
            if (_pauseDuringMenu && isPaused) return;
            _elapsedSeconds += dt;
        }

        // HH:MM:SS.mmm — drops the hour segment if under 1h, otherwise zero-pads it.
        public string Format()
        {
            double s = _elapsedSeconds;
            if (s < 0) s = 0;
            int totalMs = (int)Math.Round(s * 1000.0);
            int ms = totalMs % 1000;
            int totalSec = totalMs / 1000;
            int sec = totalSec % 60;
            int totalMin = totalSec / 60;
            int min = totalMin % 60;
            int hr = totalMin / 60;
            if (hr > 0)
                return $"{hr}:{min:D2}:{sec:D2}.{ms:D3}";
            return $"{min:D2}:{sec:D2}.{ms:D3}";
        }
    }
}
