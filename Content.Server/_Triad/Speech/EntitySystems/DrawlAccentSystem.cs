/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
using System.Text.RegularExpressions;
using Content.Server._Triad.Speech.Components;
using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;

namespace Content.Server._Triad.Speech.EntitySystems;

// Triad: expanded from upstream SouthernAccentSystem into a shared "drawl" engine driving both the
// Southern and Cowboy accents. Shared mechanics (g-drop, a/an, caps-aware tics) live in AccentHelpers;
// this system adds the drawl-specific phonetics and the data-driven per-flavor word list + tic pools
// (IDrawlAccentComponent). Renamed from SouthernAccentSystem so the shared role is obvious; a future
// upstream edit to that file will surface here as a rename/modify conflict.
public sealed class DrawlAccentSystem : EntitySystem
{
    // Drawl-specific phonetics (not shared): "and" -> "an'", "would've" -> "woulda", standalone "I" -> "Ah".
    // The and/d've regexes are IgnoreCase (case-preserving via the capture / MatchCase) so a sentence-initial
    // "And" is caught too -- the old separate lower/UPPER pair silently missed Title-case "And".
    private static readonly Regex RegexAnd = new(@"\b(an)d\b", RegexOptions.IgnoreCase);
    private static readonly Regex RegexDve = new(@"d've\b", RegexOptions.IgnoreCase);
    // The drawl monophthong: the standalone pronoun "I" becomes "Ah". The (?!') keeps it off
    // contractions ("I'm"/"I'll") so they don't collide with the prefix's capital-handling.
    private static readonly Regex RegexI = new(@"\bI\b(?!')");
    // Triad: more drawl respellings -- the zero-inflation charm lever. Carries the accent without
    // adding words. "your" -> "yer", "for" -> "fer", "just" -> "jus'", "of" -> "o'".
    private static readonly Regex RegexYour = new(@"\byour\b", RegexOptions.IgnoreCase);
    private static readonly Regex RegexFor = new(@"\bfor\b", RegexOptions.IgnoreCase);
    private static readonly Regex RegexJust = new(@"\bjust\b", RegexOptions.IgnoreCase);
    private static readonly Regex RegexOf = new(@"\bof\b", RegexOptions.IgnoreCase);

    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SouthernAccentComponent, AccentGetEvent>(OnAccent);
        SubscribeLocalEvent<CowboyAccentComponent, AccentGetEvent>(OnAccent); // Triad: Cowboy rides the same engine
    }

    private void OnAccent(EntityUid uid, SouthernAccentComponent component, AccentGetEvent args)
    {
        args.Message = Drawl(args.Message, component);
    }

    private void OnAccent(EntityUid uid, CowboyAccentComponent component, AccentGetEvent args)
    {
        args.Message = Drawl(args.Message, component);
    }

    /// <summary>
    ///     Applies the flavor's word list, the g-dropping drawl, then an optional probabilistic prefix
    ///     and suffix drawn from the component's tic pools.
    /// </summary>
    public string Drawl(string message, IDrawlAccentComponent accent)
    {
        // Word-list swaps first (per-flavor: "southern" / "cowboy"), then phonetics, then tics.
        var msg = _replacement.ApplyReplacements(message, accent.Accent);

        //They shoulda started runnin' an' hidin' from me!
        msg = AccentHelpers.DropG(msg);
        msg = RegexAnd.Replace(msg, "$1'");                                 // and -> an', And -> An', AND -> AN'
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexDve, "da");     // would've -> woulda, WOULD'VE -> WOULDA
        msg = RegexI.Replace(msg, "Ah");                                    // standalone I -> Ah
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexYour, "yer");   // your -> yer
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexFor, "fer");    // for -> fer
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexJust, "jus'");  // just -> jus'
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexOf, "o'");      // of -> o'

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        // A swap can flip a word's vowel-sound, leaving "a outlaw" / "an space critter".
        msg = AccentHelpers.FixArticles(msg);

        if (accent.Prefixes.Count > 0 && _random.Prob(accent.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(accent.Prefixes)));

        if (accent.Suffixes.Count > 0 && _random.Prob(accent.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(accent.Suffixes)));

        return msg;
    }
}

// Triad: shared config surface for drawl-family accents (Southern, Cowboy). Each flavor names its
// own ReplacementAccent word list and its own prefix/suffix loc-key pools, tunable per entity.
public interface IDrawlAccentComponent
{
    string Accent { get; }
    List<string> Prefixes { get; }
    float PrefixProb { get; }
    List<string> Suffixes { get; }
    float SuffixProb { get; }
}
