// <copyright file="Hotkey.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Windows.Input;

    /// <summary>
    /// Global hotkey of the radial menu: modifiers (at least one of Ctrl, Alt or Win) and one key.
    /// Its text form is "Ctrl+Alt+Shift+Win+Key" and is what is stored in the settings.
    /// </summary>
    public readonly struct Hotkey
    {
        public static readonly Hotkey Default = new Hotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.C);

        private const uint ModAlt = 0x1;
        private const uint ModControl = 0x2;
        private const uint ModShift = 0x4;
        private const uint ModWin = 0x8;

        private const ModifierKeys SupportedModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

        private Hotkey(ModifierKeys modifiers, Key key)
        {
            this.Modifiers = modifiers;
            this.Key = key;
        }

        public ModifierKeys Modifiers { get; }

        public Key Key { get; }

        public uint NativeModifiers
        {
            get
            {
                uint modifiers = 0;
                modifiers |= (this.Modifiers & ModifierKeys.Alt) != 0 ? ModAlt : 0;
                modifiers |= (this.Modifiers & ModifierKeys.Control) != 0 ? ModControl : 0;
                modifiers |= (this.Modifiers & ModifierKeys.Shift) != 0 ? ModShift : 0;
                modifiers |= (this.Modifiers & ModifierKeys.Windows) != 0 ? ModWin : 0;
                return modifiers;
            }
        }

        public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(this.Key);

        /// <summary>
        /// Parses a '+' separated list, case insensitive: modifiers (Ctrl or Control, Alt, Shift, Win or Windows) and exactly
        /// one key (name of <see cref="System.Windows.Input.Key"/>, or 0 to 9). Ctrl, Alt or Win is required.
        /// </summary>
        public static bool TryParse(string text, out Hotkey hotkey)
        {
            hotkey = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            ModifierKeys modifiers = ModifierKeys.None;
            Key? key = null;
            foreach (string rawToken in text.Split('+'))
            {
                string token = rawToken.Trim();
                if (token.Length == 0)
                {
                    return false;
                }

                ModifierKeys modifier = ParseModifier(token);
                if (modifier != ModifierKeys.None)
                {
                    if ((modifiers & modifier) != 0)
                    {
                        return false;
                    }

                    modifiers |= modifier;
                    continue;
                }

                if (key.HasValue || !TryParseKey(token, out Key parsedKey))
                {
                    return false;
                }

                key = parsedKey;
            }

            if (!key.HasValue)
            {
                return false;
            }

            return TryCreate(modifiers, key.Value, out hotkey);
        }

        /// <summary>
        /// Builds a hotkey from a key press (settings capture box). Modifier-only presses and combos without Ctrl, Alt or Win are rejected.
        /// </summary>
        public static bool TryFromKeyEvent(ModifierKeys modifiers, Key key, out Hotkey hotkey)
        {
            return TryCreate(modifiers, key, out hotkey);
        }

        public override string ToString()
        {
            List<string> parts = new List<string>();
            if ((this.Modifiers & ModifierKeys.Control) != 0)
            {
                parts.Add("Ctrl");
            }

            if ((this.Modifiers & ModifierKeys.Alt) != 0)
            {
                parts.Add("Alt");
            }

            if ((this.Modifiers & ModifierKeys.Shift) != 0)
            {
                parts.Add("Shift");
            }

            if ((this.Modifiers & ModifierKeys.Windows) != 0)
            {
                parts.Add("Win");
            }

            parts.Add(GetKeyName(this.Key));
            return string.Join("+", parts);
        }

        private static bool TryCreate(ModifierKeys modifiers, Key key, out Hotkey hotkey)
        {
            hotkey = default;
            modifiers &= SupportedModifiers;

            // Shift alone would hijack normal typing.
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            {
                return false;
            }

            if (!IsUsableKey(key))
            {
                return false;
            }

            hotkey = new Hotkey(modifiers, key);
            return true;
        }

        private static bool IsUsableKey(Key key)
        {
            switch (key)
            {
                case Key.None:
                case Key.System:
                case Key.ImeProcessed:
                case Key.DeadCharProcessed:
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LWin:
                case Key.RWin:
                    return false;
            }

            return KeyInterop.VirtualKeyFromKey(key) != 0;
        }

        private static ModifierKeys ParseModifier(string token)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                return ModifierKeys.Control;
            }

            if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                return ModifierKeys.Alt;
            }

            if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                return ModifierKeys.Shift;
            }

            if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) || token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                return ModifierKeys.Windows;
            }

            return ModifierKeys.None;
        }

        private static bool TryParseKey(string token, out Key key)
        {
            key = Key.None;
            if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
            {
                key = Key.D0 + (token[0] - '0');
                return true;
            }

            // Numeric values are rejected: Enum.TryParse would accept them.
            if (char.IsDigit(token[0]) || !Enum.TryParse(token, true, out key) || !Enum.IsDefined(typeof(Key), key))
            {
                key = Key.None;
                return false;
            }

            return true;
        }

        private static string GetKeyName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
            {
                return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
            }

            return key.ToString();
        }
    }
}
