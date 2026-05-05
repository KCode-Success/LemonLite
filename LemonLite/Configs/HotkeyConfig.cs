using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LemonLite.Configs;

public class HotkeyConfig
{
    public HotkeyBinding PlayPause { get; set; } = new() { Modifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, Key = 0x20 };
    public HotkeyBinding PlayNext { get; set; } = new() { Modifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, Key = 0x27 };
    public HotkeyBinding PlayPrevious { get; set; } = new() { Modifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, Key = 0x25 };

    public bool EnableGlobalHotkeys { get; set; } = false;
}

public class HotkeyBinding
{
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.None;
    public int Key { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Modifiers == HotkeyModifiers.None && Key == 0;

    public string ToDisplayString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (Key != 0) parts.Add(KeyToString(Key));
        return string.Join(" + ", parts);
    }

    public static string KeyToString(int vk)
    {
        return vk switch
        {
            0x03 => "Break",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0C => "Clear",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x29 => "Select",
            0x2A => "Print",
            0x2B => "Execute",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x2F => "Help",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
            0x6A => "Num*",
            0x6B => "Num+",
            0x6C => "NumEnter",
            0x6D => "Num-",
            0x6E => "Num.",
            0x6F => "Num/",
            >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xA0 => "LShift",
            0xA1 => "RShift",
            0xA2 => "LCtrl",
            0xA3 => "RCtrl",
            0xA4 => "LAlt",
            0xA5 => "RAlt",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"0x{vk:X2}"
        };
    }

    public HotkeyBinding Clone() => new() { Modifiers = Modifiers, Key = Key };

    public bool EqualsBinding(HotkeyBinding? other)
    {
        return other != null && Modifiers == other.Modifiers && Key == other.Key;
    }
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}
