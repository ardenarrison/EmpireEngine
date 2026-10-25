namespace Editor.Terminal;

// How the terminal shows characters the editor's font doesn't have. The font only has plain ASCII, so box-drawing lines and block
// shapes (which full-screen programs use for frames and bars) are drawn as rectangles instead, and other common symbols are replaced
// by close ASCII ones. Anything else shows as '?'.
public static class TerminalGlyphs
{
    [Flags] public enum Edge { None = 0, Left = 1, Right = 2, Up = 4, Down = 8 }

    // A box-drawing character as the sides its line reaches: '+' reaches all four, '-' left and right, and so on. Light, heavy,
    // double and rounded forms all use the same thin lines.
    public static Edge BoxEdges(char c)
    {
        const Edge L = Edge.Left, R = Edge.Right, U = Edge.Up, D = Edge.Down;
        int n = c;
        if (n < 0x2500 || n > 0x257F) return Edge.None;
        return n switch
        {
            0x2500 or 0x2501 or 0x2504 or 0x2505 or 0x2508 or 0x2509 or 0x254C or 0x254D or 0x2550 => L | R,
            0x2502 or 0x2503 or 0x2506 or 0x2507 or 0x250A or 0x250B or 0x254E or 0x254F or 0x2551 => U | D,
            >= 0x250C and <= 0x250F or >= 0x2552 and <= 0x2554 or 0x256D => R | D,
            >= 0x2510 and <= 0x2513 or >= 0x2555 and <= 0x2557 or 0x256E => L | D,
            >= 0x2514 and <= 0x2517 or >= 0x2558 and <= 0x255A or 0x2570 => R | U,
            >= 0x2518 and <= 0x251B or >= 0x255B and <= 0x255D or 0x256F => L | U,
            >= 0x251C and <= 0x2523 or >= 0x255E and <= 0x2560 => U | D | R,
            >= 0x2524 and <= 0x252B or >= 0x2561 and <= 0x2563 => U | D | L,
            >= 0x252C and <= 0x2533 or >= 0x2564 and <= 0x2566 => L | R | D,
            >= 0x2534 and <= 0x253B or >= 0x2567 and <= 0x2569 => L | R | U,
            >= 0x253C and <= 0x254B or >= 0x256A and <= 0x256C => L | R | U | D,
            0x2574 or 0x2578 or 0x257C => L,
            0x2575 or 0x2579 or 0x257D => U,
            0x2576 or 0x257A or 0x257E => R,
            0x2577 or 0x257B or 0x257F => D,
            _ => Edge.None,
        };
    }

    // A block-element character as the part of the cell it fills, in eighths: (left, top, right, bottom) from 0 to 8, plus how
    // solid it is (shade characters are 25, 50 and 75 percent).
    public static bool BlockRect(char c, out int left, out int top, out int right, out int bottom, out float alpha)
    {
        left = 0; top = 0; right = 8; bottom = 8; alpha = 1f;
        int n = c;
        if (n < 0x2580 || n > 0x259F) return false;
        switch (n)
        {
            case 0x2580: bottom = 4; break;                                   // upper half
            case >= 0x2581 and <= 0x2587: top = 8 - (n - 0x2580); break;      // lower 1/8 .. 7/8
            case 0x2588: break;                                               // full block
            case >= 0x2589 and <= 0x258F: right = 8 - (n - 0x2588); break;    // left 7/8 .. 1/8
            case 0x2590: left = 4; break;                                     // right half
            case 0x2591: alpha = 0.25f; break;
            case 0x2592: alpha = 0.5f; break;
            case 0x2593: alpha = 0.75f; break;
            case 0x2594: bottom = 1; break;                                   // upper 1/8
            case 0x2595: left = 7; break;                                     // right 1/8
            case 0x2596: right = 4; top = 4; break;                           // quadrants
            case 0x2597: left = 4; top = 4; break;
            case 0x2598: right = 4; bottom = 4; break;
            case 0x259D: left = 4; bottom = 4; break;
            case 0x259B or 0x259C or 0x2599 or 0x259F or 0x259A or 0x259E: break;   // combinations: drawn as a full cell
        }
        return true;
    }

    // A plain ASCII character to show in place of c. Box-drawing and block characters are drawn separately (see above).
    public static char ToAscii(char c)
    {
        if (c >= 32 && c < 127) return c;
        return c switch
        {
            '\0' or ' ' => ' ',
            '•' or '●' or '·' or '∙' or '◦' or '○' or '◉' or '■' or '▪' or '▫' or '□' or '★' or '☆' or '✻' or '✳' or '✶' or '✢' or '✽' or '⏺' => '*',
            '✓' or '✔' or '☑' => 'v',
            '✗' or '✘' or '✕' or '☒' or '×' => 'x',
            '…' => '.',
            '→' or '▶' or '►' or '❯' or '›' or '»' or '⇒' or '➜' or '▸' or '▹' => '>',
            '←' or '◀' or '◄' or '‹' or '«' or '⇐' or '◂' => '<',
            '↑' or '▲' or '△' or '⏶' => '^',
            '↓' or '▼' or '▽' or '⏷' => 'v',
            '–' or '—' or '‐' or '‑' or '−' or '―' => '-',
            '‘' or '’' or '′' => '\'',
            '“' or '”' or '″' => '"',
            '│' => '|',
            '⎿' or '└' or '├' or '┌' or '╭' or '╰' => '|',
            '©' => 'c',
            '°' => 'o',
            'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' => 'a',
            'è' or 'é' or 'ê' or 'ë' => 'e',
            'ì' or 'í' or 'î' or 'ï' => 'i',
            'ò' or 'ó' or 'ô' or 'õ' or 'ö' => 'o',
            'ù' or 'ú' or 'û' or 'ü' => 'u',
            'ñ' => 'n',
            'ç' => 'c',
            _ => '?',
        };
    }

    // The standard 256-color palette as 0xRRGGBB: 16 basic colors, a 6x6x6 cube, then 24 grays. `light` gives the 16 basic colors a
    // light background needs (the dark set's yellows and whites can't be read on white).
    public static int Palette(int index, bool light = false)
    {
        if (index < 0) index = 7;
        if (index < 16)
            return light
                ? index switch
                {
                    0 => 0x000000, 1 => 0xCD3131, 2 => 0x008000, 3 => 0x949800, 4 => 0x0451A5, 5 => 0xBC05BC, 6 => 0x0598BC, 7 => 0x555555,
                    8 => 0x666666, 9 => 0xCD3131, 10 => 0x14A014, 11 => 0xB5BA00, 12 => 0x0451A5, 13 => 0xBC05BC, 14 => 0x0598BC, _ => 0xA5A5A5,
                }
                : index switch
                {
                    0 => 0x1E1E1E, 1 => 0xCD3131, 2 => 0x0DBC79, 3 => 0xE5E510, 4 => 0x2472C8, 5 => 0xBC3FBC, 6 => 0x11A8CD, 7 => 0xE5E5E5,
                    8 => 0x666666, 9 => 0xF14C4C, 10 => 0x23D18B, 11 => 0xF5F543, 12 => 0x3B8EEA, 13 => 0xD670D6, 14 => 0x29B8DB, _ => 0xFFFFFF,
                };
        if (index < 232)
        {
            int i = index - 16;
            int Level(int v) => v == 0 ? 0 : 55 + v * 40;
            return (Level(i / 36) << 16) | (Level(i / 6 % 6) << 8) | Level(i % 6);
        }
        int gray = 8 + (index - 232) * 10;
        return (gray << 16) | (gray << 8) | gray;
    }
}
