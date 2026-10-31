using System;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Keyboard actions that can be rebound from the in-game controls panel.</summary>
    public enum ControlAction
    {
        OpenChat, Forward, Reverse, TurnLeft, TurnRight, PitchUp, PitchDown, Brake,
        Warp, Fire, NextHostile, Follow, Gate, Dock, Launch, Chart, Equipment, ResetCamera,
        AbilityOne, AbilityTwo, AbilityThree, AbilityFour, AbilityFive, AbilitySix, TurboWeapons,
        NextAttacker, Galaxy,
        AbilitySeven, AbilityEight, AbilityNine, AbilityTen, AbilityEleven, AbilityTwelve,
        AbilityThirteen, AbilityFourteen, AbilityFifteen, AbilitySixteen, AbilitySeventeen, AbilityEighteen,
        AbilityNineteen, AbilityTwenty, AbilityTwentyOne, AbilityTwentyTwo, AbilityTwentyThree, AbilityTwentyFour,
        NearestHostile, NearestNonEnemy, TargetSelf
    }

    /// <summary>Persist one unique keyboard key for each flight action.</summary>
    public sealed class KeyBindings
    {
        private const string PreferencePrefix = "42andBeyond.key.";
        private static readonly KeyCode[] Defaults =
        {
            KeyCode.Return, KeyCode.W, KeyCode.S, KeyCode.LeftArrow, KeyCode.RightArrow,
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.Backspace, KeyCode.Q, KeyCode.F,
            KeyCode.D, KeyCode.Space, KeyCode.G, KeyCode.O, KeyCode.L,
            KeyCode.Tab, KeyCode.I, KeyCode.C,
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3,
            KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.B, KeyCode.Y, KeyCode.M,
            KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0, KeyCode.Minus, KeyCode.Equals,
            KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6,
            KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12, KeyCode.E, KeyCode.X, KeyCode.BackQuote
        };
        public static readonly ControlAction[] Actions = (ControlAction[])Enum.GetValues(typeof(ControlAction));
        private readonly KeyCode[] keys = (KeyCode[])Defaults.Clone();

        /// <summary>Load valid saved keys while keeping every action reachable.</summary>
        public KeyBindings()
        {
            foreach (ControlAction action in Actions)
            {
                int index = (int)action;
                int fallback = (action == ControlAction.Galaxy || action >= ControlAction.AbilitySeven) ? (int)keys[index] : (int)Defaults[index];
                int stored = PlayerPrefs.GetInt(PreferencePrefix + action, fallback);
                if (action == ControlAction.Follow && stored == (int)KeyCode.R ||
                    action == ControlAction.Brake && (stored == (int)KeyCode.Space || stored == (int)KeyCode.X))
                {
                    stored = (int)Defaults[index];
                    PlayerPrefs.SetInt(PreferencePrefix + action, stored); PlayerPrefs.Save();
                }
                if (action == ControlAction.Equipment && stored == (int)KeyCode.E)
                {
                    stored = (int)KeyCode.I;
                    PlayerPrefs.SetInt(PreferencePrefix + action, stored);
                    PlayerPrefs.Save();
                }
                if(action==ControlAction.TurnLeft&&stored==(int)KeyCode.A || action==ControlAction.TurnRight&&stored==(int)KeyCode.D)
                {
                    stored=(int)(action==ControlAction.TurnLeft?KeyCode.LeftArrow:KeyCode.RightArrow);
                    PlayerPrefs.SetInt(PreferencePrefix+action,stored);PlayerPrefs.Save();
                }
                if (action == ControlAction.NextHostile && stored == (int)KeyCode.T)
                {
                    stored = (int)KeyCode.D;
                    PlayerPrefs.SetInt(PreferencePrefix + action, stored); PlayerPrefs.Save();
                }
                KeyCode key = (KeyCode)stored;
                if (!Enum.IsDefined(typeof(KeyCode), key) || key == KeyCode.None || key == KeyCode.Escape)
                    continue;
                int duplicate = Array.IndexOf(keys, key);
                if (duplicate >= 0 && duplicate != index) keys[duplicate] = keys[index];
                keys[index] = key;
            }
        }

        /// <returns>KeyCode: The assigned key for a flight action.</returns>
        public KeyCode Get(ControlAction action) { return keys[(int)action]; }

        /// <returns>bool: Whether the assigned key was pressed this frame.</returns>
        public bool Pressed(ControlAction action) { return Input.GetKeyDown(Get(action)); }

        /// <returns>bool: Whether the assigned key is being held.</returns>
        public bool Held(ControlAction action) { return Input.GetKey(Get(action)); }

        /// <returns>string: A compact label for the assigned key.</returns>
        public string Display(ControlAction action)
        {
            KeyCode key = Get(action);
            if (key == KeyCode.Return) return "Enter";
            if (key == KeyCode.Space) return "Space";
            if (key == KeyCode.LeftArrow) return "←";
            if (key == KeyCode.RightArrow) return "→";
            if (key == KeyCode.UpArrow) return "↑";
            if (key == KeyCode.DownArrow) return "↓";
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return ((int)key - (int)KeyCode.Alpha0).ToString();
            return key.ToString();
        }

        /// <returns>string: The readable action name shown in the controls panel.</returns>
        public static string Label(ControlAction action)
        {
            if (action >= ControlAction.AbilitySeven && action <= ControlAction.AbilityTwentyFour)
                return "Shortcut slot " + ((int)action - (int)ControlAction.AbilitySeven + 7);
            switch (action)
            {
                case ControlAction.OpenChat: return "Open chat";
                case ControlAction.Forward: return "Forward thrust";
                case ControlAction.Reverse: return "Reverse thrust";
                case ControlAction.TurnLeft: return "Turn left";
                case ControlAction.TurnRight: return "Turn right";
                case ControlAction.PitchUp: return "Pitch up";
                case ControlAction.PitchDown: return "Pitch down";
                case ControlAction.Brake: return "Brake / cancel follow";
                case ControlAction.Warp: return "Warp to destination";
                case ControlAction.Fire: return "Fire / stop weapons";
                case ControlAction.TurboWeapons: return "Turbo Weapons device";
                case ControlAction.NextHostile: return "Next selectable object";
                case ControlAction.NearestHostile: return "Nearest enemy";
                case ControlAction.NearestNonEnemy: return "Nearest non-enemy";
                case ControlAction.TargetSelf: return "Target your ship";
                case ControlAction.NextAttacker: return "Next attacking MOB";
                case ControlAction.Follow: return "Follow target";
                case ControlAction.Gate: return "Activate gate";
                case ControlAction.Dock: return "Dock at station";
                case ControlAction.Launch: return "Launch from station";
                case ControlAction.Chart: return "Sector chart";
                case ControlAction.Galaxy: return "Galaxy travel";
                case ControlAction.Equipment: return "Inventory";
                case ControlAction.ResetCamera: return "Reset camera";
                case ControlAction.AbilityOne: return "Shortcut slot 1";
                case ControlAction.AbilityTwo: return "Shortcut slot 2";
                case ControlAction.AbilityThree: return "Shortcut slot 3";
                case ControlAction.AbilityFour: return "Shortcut slot 4";
                case ControlAction.AbilityFive: return "Shortcut slot 5";
                case ControlAction.AbilitySix: return "Shortcut slot 6";
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        /// <returns>ControlAction: The rebindable action for one of the 24 shortcut slots.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The slot is outside the supported bar.</exception>
        public static ControlAction ShortcutAction(int index)
        {
            if (index < 0 || index >= 24) throw new ArgumentOutOfRangeException(nameof(index));
            return index < 6 ? (ControlAction)((int)ControlAction.AbilityOne + index) :
                (ControlAction)((int)ControlAction.AbilitySeven + index - 6);
        }

        /// <summary>Assign a key, swapping with its previous action to avoid conflicts.</summary>
        public void Bind(ControlAction action, KeyCode key)
        {
            if (!Enum.IsDefined(typeof(KeyCode), key) || key == KeyCode.None || key == KeyCode.Escape)
                throw new ArgumentOutOfRangeException(nameof(key));
            int index = (int)action;
            int duplicate = Array.IndexOf(keys, key);
            if (duplicate >= 0 && duplicate != index)
            {
                keys[duplicate] = keys[index];
                PlayerPrefs.SetInt(PreferencePrefix + Actions[duplicate], (int)keys[duplicate]);
            }
            keys[index] = key;
            PlayerPrefs.SetInt(PreferencePrefix + action, (int)key);
            PlayerPrefs.Save();
        }

        /// <summary>Restore and save the default keyboard layout.</summary>
        public void Reset()
        {
            Array.Copy(Defaults, keys, keys.Length);
            foreach (ControlAction action in Actions)
                PlayerPrefs.SetInt(PreferencePrefix + action, (int)keys[(int)action]);
            PlayerPrefs.Save();
        }
    }
}
