﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using LiveSplit.Model;
using LiveSplit.Options;

namespace LiveSplit.ASL
{
    public class ASLScript : IDisposable
    {
        public class Methods
        {
            private static ASLMethod no_op = new ASLMethod("");

            public ASLMethod startup = no_op;
            public ASLMethod shutdown = no_op;
            public ASLMethod init = no_op;
            public ASLMethod exit = no_op;
            public ASLMethod update = no_op;
            public ASLMethod start = no_op;
            public ASLMethod split = no_op;
            public ASLMethod reset = no_op;
            public ASLMethod isLoading = no_op;
            public ASLMethod gameTime = no_op;
            public ASLMethod onStart = no_op;
            public ASLMethod onReset = no_op;
            public ASLMethod onSplit = no_op;
            public ASLMethod onSkipSplit = no_op;
            public ASLMethod onUndoSplit = no_op;
        }

        public event EventHandler<double> RefreshRateChanged;
        public event EventHandler<string> GameVersionChanged;

        private string _game_version = string.Empty;
        public string GameVersion
        {
            get { return _game_version; }
            set
            {
                if (value != _game_version)
                    GameVersionChanged?.Invoke(this, value);
                _game_version = value;
            }
        }

        private double _refresh_rate = 1000 / 15d;
        public double RefreshRate // per sec
        {
            get { return _refresh_rate; }
            set
            {
                if (Math.Abs(value - _refresh_rate) > 0.01)
                    RefreshRateChanged?.Invoke(this, value);
                _refresh_rate = value;
            }
        }

        // public so other components (ASLVarViewer) can access
        public ASLState State { get; private set; }
        public ASLState OldState { get; private set; }
        public ExpandoObject Vars { get; }

        private bool _uses_game_time;
        private bool _init_completed;

        private ASLSettings _settings;

        private Process _game;
        private TimerModel _timer;

        private Dictionary<string, List<ASLState>> _states;

        private Methods _methods;

        public ASLScript(Methods methods, Dictionary<string, List<ASLState>> states)
        {
            _methods = methods;
            _states = states;

            _settings = new ASLSettings();
            Vars = new ExpandoObject();

            if (!_methods.start.IsEmpty)
                _settings.AddBasicSetting("start");
            if (!_methods.split.IsEmpty)
                _settings.AddBasicSetting("split");
            if (!_methods.reset.IsEmpty)
                _settings.AddBasicSetting("reset");

            _uses_game_time = !_methods.isLoading.IsEmpty || !_methods.gameTime.IsEmpty;
        }

        public void Dispose()
        {
            if (_timer != null)
            {
                _timer.CurrentState.OnStart -= _timer_CurrentState_OnStart;
                _timer.CurrentState.OnReset -= _timer_CurrentState_OnReset;
                _timer.CurrentState.OnSplit -= _timer_CurrentState_OnSplit;
                _timer.CurrentState.OnSkipSplit -= _timer_CurrentState_OnSkipSplit;
                _timer.CurrentState.OnUndoSplit -= _timer_CurrentState_OnUndoSplit;
            }
        }

        // Update the script
        public void Update(LiveSplitState state)
        {
            if (_game == null)
            {
                if (_timer == null)
                {
                    _timer = new TimerModel() { CurrentState = state };
                    _timer.CurrentState.OnStart += _timer_CurrentState_OnStart;
                    _timer.CurrentState.OnReset += _timer_CurrentState_OnReset;
                    _timer.CurrentState.OnSplit += _timer_CurrentState_OnSplit;
                    _timer.CurrentState.OnSkipSplit += _timer_CurrentState_OnSkipSplit;
                    _timer.CurrentState.OnUndoSplit += _timer_CurrentState_OnUndoSplit;
                }
                TryConnect(state);
            }
            else if (_game.HasExited)
            {
                DoExit(state);
            }
            else
            {
                if (!_init_completed)
                    DoInit(state);
                else
                    DoUpdate(state);
            }
        }

        // Run startup and return settings defined in ASL script.
        public ASLSettings RunStartup(LiveSplitState state)
        {
            Debug("Running startup");
            RunNoProcessMethod(_methods.startup, state, true);
            return _settings;
        }

        public void RunShutdown(LiveSplitState state)
        {
            Debug("Running shutdown");
            RunMethod(_methods.shutdown, state);
        }

        private void TryConnect(LiveSplitState state)
        {
            _game = null;

            var state_process = _states.Keys.Select(proccessName => new {
                // default to the state with no version specified, if it exists
                State = _states[proccessName].FirstOrDefault(s => s.GameVersion == "") ?? _states[proccessName].First(),
                Process = Process.GetProcessesByName(proccessName).OrderByDescending(x => x.StartTime)
                    .FirstOrDefault(x => !x.HasExited)
            }).FirstOrDefault(x => x.Process != null);

            if (state_process == null)
                return;

            _init_completed = false;
            _game = state_process.Process;
            State = state_process.State;

            if (State.GameVersion == "")
            {
                Debug("Connected to game: {0} (using default state descriptor)", _game.ProcessName);
            }
            else
            {
                Debug("Connected to game: {0} (state descriptor for version '{1}' chosen as default)",
                    _game.ProcessName,
                    State.GameVersion);
            }

            DoInit(state);
        }

        // This is executed each time after connecting to the game (usually just once,
        // unless an error occurs before the method finishes).
        private void DoInit(LiveSplitState state)
        {
            Debug("Initializing");

            State.RefreshValues(_game);
            OldState = State;
            GameVersion = string.Empty;

            // Fetch version from init-method
            var ver = string.Empty;
            RunMethod(_methods.init, state, ref ver);

            if (ver != GameVersion)
            {
                GameVersion = ver;

                var version_state = _states.Where(kv => kv.Key.ToLower() == _game.ProcessName.ToLower())
                    .Select(kv => kv.Value)
                    .First() // states
                    .FirstOrDefault(s => s.GameVersion == ver);

                if (version_state != null)
                {
                    // This state descriptor may already be selected
                    if (version_state != State)
                    {
                        State = version_state;
                        State.RefreshValues(_game);
                        OldState = State;
                        Debug($"Switched to state descriptor for version '{GameVersion}'");
                    }
                }
                else
                {
                    Debug($"No state descriptor for version '{GameVersion}' (will keep using default one)");
                }
            }

            _init_completed = true;
            Debug("Init completed, running main methods");
        }

        private void DoExit(LiveSplitState state)
        {
            Debug("Running exit");
            _game = null;
            RunNoProcessMethod(_methods.exit, state);
        }

        // This is executed repeatedly as long as the game is connected and initialized.
        private void DoUpdate(LiveSplitState state)
        {
            OldState = State.RefreshValues(_game);

            if (!(RunMethod(_methods.update, state) ?? true))
            {
                // If Update explicitly returns false, don't run anything else
                return;
            }

            if (state.CurrentPhase == TimerPhase.Running || state.CurrentPhase == TimerPhase.Paused)
            {
                if (_uses_game_time && !state.IsGameTimeInitialized)
                    _timer.InitializeGameTime();

                var is_paused = RunMethod(_methods.isLoading, state);
                if (is_paused != null)
                    state.IsGameTimePaused = is_paused;

                var game_time = RunMethod(_methods.gameTime, state);
                if (game_time != null)
                    state.SetGameTime(game_time);

                if (RunMethod(_methods.reset, state) ?? false)
                {
                    if (_settings.GetBasicSettingValue("reset"))
                        _timer.Reset();
                }
                else if (RunMethod(_methods.split, state) ?? false)
                {
                    if (_settings.GetBasicSettingValue("split"))
                        _timer.Split();
                }
            }

            if (state.CurrentPhase == TimerPhase.NotRunning)
            {
                if (RunMethod(_methods.start, state) ?? false)
                {
                    if (_settings.GetBasicSettingValue("start"))
                        _timer.Start();
                }
            }
        }

        private void _timer_CurrentState_OnStart(object sender, EventArgs e)
        {
            TryRunMethod(_methods.onStart, _timer.CurrentState);
        }

        private void _timer_CurrentState_OnSplit(object sender, EventArgs e)
        {
            TryRunMethod(_methods.onSplit, _timer.CurrentState);
        }

        private void _timer_CurrentState_OnReset(object sender, TimerPhase value)
        {
            TryRunMethod(_methods.onReset, _timer.CurrentState);
        }

        private void _timer_CurrentState_OnSkipSplit(object sender, EventArgs e)
        {
            TryRunMethod(_methods.onSkipSplit, _timer.CurrentState);
        }

        private void _timer_CurrentState_OnUndoSplit(object sender, EventArgs e)
        {
            TryRunMethod(_methods.onUndoSplit, _timer.CurrentState);
        }

        private dynamic RunMethod(ASLMethod method, LiveSplitState state, ref string version)
        {
            var refresh_rate = RefreshRate;
            var result = method.Call(state, Vars, ref version, ref refresh_rate, _settings.Reader,
                OldState?.Data, State?.Data, _game);
            RefreshRate = refresh_rate;
            return result;
        }

        private dynamic RunMethod(ASLMethod method, LiveSplitState state)
        {
            var version = GameVersion;
            return RunMethod(method, state, ref version);
        }

        // Run method that catches and logs exceptions. Required for event handlers.
        private dynamic TryRunMethod(ASLMethod method, LiveSplitState state)
        {
            try
            {
                return RunMethod(method, state);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            return null;
        }

        // Run method without counting on being connected to the game (startup/shutdown).
        private void RunNoProcessMethod(ASLMethod method, LiveSplitState state, bool is_startup = false)
        {
            var refresh_rate = RefreshRate;
            var version = GameVersion;
            method.Call(state, Vars, ref version, ref refresh_rate,
                is_startup ? _settings.Builder : (object)_settings.Reader);
            RefreshRate = refresh_rate;
        }

        private void Debug(string output, params object[] args)
        {
            Log.Info(String.Format("[ASL/{1}] {0}",
                String.Format(output, args),
                this.GetHashCode()));
        }
    }
}
