namespace LambdaBoss;

/// <summary>
///     A shortcut in Excel's OnKey format (<c>^</c> Ctrl, <c>+</c> Shift, <c>%</c> Alt, then a letter,
///     a digit or <c>{F1}</c>…<c>{F12}</c>), read into what a keyboard hook needs: the virtual key and
///     the modifiers (spec 0017). Other OnKey forms aren't supported; <see cref="TryParse" /> refuses them.
/// </summary>
internal sealed record ShortcutKey(bool Ctrl, bool Shift, bool Alt, int VirtualKey, string KeyName, string OnKey)
{
    /// <summary>As people read it: "Ctrl+Shift+C".</summary>
    public string Label =>
        string.Concat(Ctrl ? "Ctrl+" : "", Shift ? "Shift+" : "", Alt ? "Alt+" : "", KeyName);

    public static bool TryParse(string? onKey, out ShortcutKey key)
    {
        key = null!;
        if (string.IsNullOrWhiteSpace(onKey))
            return false;

        var text = onKey!.Trim();
        bool ctrl = false, shift = false, alt = false;
        var i = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '^' && !ctrl) ctrl = true;
            else if (c == '+' && !shift) shift = true;
            else if (c == '%' && !alt) alt = true;
            else break;
        }

        var rest = text[i..];
        int vk;
        string name;
        if (rest.Length == 1 && char.IsLetter(rest[0]) && rest[0] < 128)
        {
            name = char.ToUpperInvariant(rest[0]).ToString();
            vk = name[0]; // VK_A..VK_Z are 'A'..'Z'
        }
        else if (rest.Length == 1 && rest[0] is >= '0' and <= '9')
        {
            name = rest;
            vk = rest[0]; // VK_0..VK_9 are '0'..'9'
        }
        else if (rest.Length >= 4 && rest[0] == '{' && rest[^1] == '}'
                 && (rest[1] == 'F' || rest[1] == 'f')
                 && int.TryParse(rest.Substring(2, rest.Length - 3), out var f) && f is >= 1 and <= 12)
        {
            name = "F" + f;
            vk = 0x70 + f - 1; // VK_F1
        }
        else
        {
            return false;
        }

        // A bare letter or digit would fire on ordinary typing.
        if (!ctrl && !alt && vk < 0x70)
            return false;

        key = new ShortcutKey(ctrl, shift, alt, vk, name, text);
        return true;
    }

    /// <summary>The setting when it parses, else the default (which always does).</summary>
    public static ShortcutKey ParseOr(string? onKey, string fallback) =>
        TryParse(onKey, out var key) ? key : TryParse(fallback, out var dflt) ? dflt
            : throw new ArgumentException($"Bad default shortcut '{fallback}'", nameof(fallback));
}
