using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiveCaptions10;

/// <summary>
/// The recognizer outputs lower-case words without punctuation. This adds sentence casing,
/// a full stop / question mark at each pause, fixes "i", drops fillers and masks profanity.
/// </summary>
internal static class CaptionFormatter
{
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    static readonly Regex Fillers = new(@"\b(?:uh|um|erm|hmm|mm)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex PronounI = new(@"\bi\b", RegexOptions.Compiled);

    // Days and unambiguous months ("may" and "march" are left alone).
    static readonly Regex ProperNouns = new(
        @"\b(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday|january|february|april|june|july|august|september|october|november|december|christmas|easter|english|american|british|europe|european|america|africa|asia|australia|canada|china|japan|france|germany|india|london|paris|new york|internet|google|microsoft|windows|youtube|iphone)\b",
        RegexOptions.Compiled);
    static readonly Regex Profanity = new(
        @"\b(?:motherfuck|fuck|bullshit|shit|bitch|asshole|cunt|bastard|whore|slut|dickhead)\w*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Vosk tends to emit these alone for music or noise.
    static readonly HashSet<string> NoiseOnly = new() { "the", "huh", "a", "and", "but", "oh" };

    static readonly HashSet<string> QuestionWords = new() { "what", "why", "how", "who", "whom", "whose", "where", "when", "which" };

    static readonly HashSet<string> Auxiliaries = new()
    {
        "are", "is", "am", "do", "does", "did", "can", "could", "would", "will", "should", "shall", "have", "has",
        "may", "might", "was", "were", "isn't", "aren't", "don't", "doesn't", "didn't", "can't", "won't",
        "wouldn't", "couldn't", "shouldn't", "haven't", "hasn't",
    };

    static readonly HashSet<string> Subjects = new() { "you", "i", "we", "they", "he", "she", "it", "there", "this", "that", "anyone", "anybody" };

    public static string FormatPartial(string raw, bool filterProfanity) => Clean(raw, filterProfanity);

    /// <summary>The newest few words after Whisper's live text: tidied, but not capitalized.</summary>
    public static string FormatTail(string raw, bool filterProfanity) => Clean(raw, filterProfanity, capitalize: false);

    /// <summary>Whisper output is already cased and punctuated; only tidy it and apply the filter.</summary>
    public static string FormatRefined(string text, bool filterProfanity)
    {
        text = Spaces.Replace(text, " ").Trim();
        if (text.Length == 0) return "";
        if (filterProfanity) text = Profanity.Replace(text, m => m.Value[0] + new string('*', m.Value.Length - 1));
        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    public static string FormatFinal(string raw, bool filterProfanity)
    {
        var text = Clean(raw, filterProfanity);
        if (text.Length == 0 || NoiseOnly.Contains(text.ToLowerInvariant())) return "";
        char last = text[^1];
        if (last is '.' or '?' or '!') return text;
        // Judge only the last sentence: "Do you think...? Experts say..." is not a question at the end.
        int previousEnd = text.LastIndexOfAny(new[] { '.', '?', '!' });
        var lastSentence = text[(previousEnd + 1)..].TrimStart();
        return text + (lastSentence.Length > 0 && LooksLikeQuestion(lastSentence) ? "?" : ".");
    }

    static string Clean(string raw, bool filterProfanity, bool capitalize = true)
    {
        var text = raw.Replace("[unk]", " ");
        text = Fillers.Replace(text, " ");
        text = Spaces.Replace(text, " ").Trim();
        if (text.Length == 0) return "";
        text = PronounI.Replace(text, "I");
        text = ProperNouns.Replace(text, m => TitleCase(m.Value));
        if (filterProfanity) text = Profanity.Replace(text, m => m.Value[0] + new string('*', m.Value.Length - 1));
        return capitalize ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;
    }

    static string TitleCase(string words)
    {
        var chars = words.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (i == 0 || chars[i - 1] == ' ') chars[i] = char.ToUpperInvariant(chars[i]);
        var result = new string(chars);
        return result switch
        {
            "Youtube" => "YouTube",
            "Iphone" => "iPhone",
            _ => result,
        };
    }

    static bool LooksLikeQuestion(string text)
    {
        var words = text.ToLowerInvariant().Split(' ', 3);
        if (QuestionWords.Contains(words[0])) return true;
        return words.Length > 1 && Auxiliaries.Contains(words[0]) && Subjects.Contains(words[1]);
    }
}
