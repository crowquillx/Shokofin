using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Shokofin.Utils;

/// <summary>
/// Fork: titles — shortens the titles of a show's seasons by dropping the part
/// that repeats the show's title. Ported from the Silo ShokoAnime VFS plugin.
/// </summary>
public static partial class SeasonTitleUtility {
    /// <summary>
    /// A season of a show, as the shortener sees it.
    /// </summary>
    /// <param name="Id">The season's id, used to find the main season.</param>
    /// <param name="Name">The season's title, as it would be shown.</param>
    /// <param name="MainTitle">The AniDB main title of the season's anime, if any.</param>
    /// <param name="AiredAt">When the season first aired, if known.</param>
    public record Season(string Id, string Name, string? MainTitle, DateTime? AiredAt);

    /// <summary>
    /// Returns a title for each of a show's seasons, in the order given,
    /// without the part that repeats the show's title: under the show
    /// "Demon Slayer", the season "Demon Slayer: Entertainment District Arc"
    /// is "Entertainment District Arc".
    /// </summary>
    /// <remarks>
    /// A season keeps its name when what would be left says too little:
    /// nothing, a year, a number, or a few characters such as "II" or "S".
    /// Every season keeps its name when fewer than half of them start with the
    /// show's title, as in a group that mixes several franchises.
    ///
    /// Several seasons with the same name are first told apart with the part
    /// of their AniDB main title that the first of them does not have.
    /// </remarks>
    /// <param name="show">The show's title.</param>
    /// <param name="mainSeasonId">The id of the season the show is named after.</param>
    /// <param name="seasons">The show's seasons, in order.</param>
    public static IReadOnlyList<string> ShortenSeasonTitles(string show, string? mainSeasonId, IReadOnlyList<Season> seasons) {
        show = show.Trim();
        var names = DistinctSeasonNames(mainSeasonId, seasons);
        static bool Matches(string name, string stem) => TryCutTitlePrefix(name, stem, out _);

        // The show's title is always stripped. A shorter stem of it, such as
        // "Ghost in the Shell" of "Ghost in the Shell: Stand Alone Complex",
        // only when a season needs it.
        var stems = new List<string>();
        foreach (var stem in TitleStems(show)) {
            if (stem == show || names.Any(name => Matches(name, stem) && !Matches(name, show)))
                stems.Add(stem);
        }

        var named = names.Count(name => stems.Any(stem => Matches(name, stem)));
        if (named * 2 < names.Count)
            return names;

        return names.Select(name => Subtitle(name, stems)).ToList();
    }

    /// <summary>
    /// Returns each season's name. Where several have the same name, the
    /// first keeps it and each of the others gets the rest of its AniDB main
    /// title appended: after "Tokyo Revengers", "Tokyo Revengers: Seiya Kessen
    /// Hen" stays apart from it. The first is the main season or the one that
    /// aired first.
    /// </summary>
    private static List<string> DistinctSeasonNames(string? mainSeasonId, IReadOnlyList<Season> seasons) {
        var names = seasons.Select(season => season.Name.Trim()).ToList();
        var sameName = new Dictionary<string, List<int>>();
        var order = new List<string>();
        for (var index = 0; index < names.Count; index++) {
            var key = names[index].ToLowerInvariant();
            if (!sameName.TryGetValue(key, out var list)) {
                sameName[key] = list = [];
                order.Add(key);
            }
            list.Add(index);
        }

        foreach (var key in order) {
            var indexes = sameName[key];
            if (indexes.Count < 2)
                continue;

            var first = indexes[0];
            foreach (var index in indexes.Skip(1)) {
                if (seasons[first].Id == mainSeasonId)
                    break;
                if (seasons[index].Id == mainSeasonId || AiredBefore(seasons[index], seasons[first]))
                    first = index;
            }

            var stems = TitleStems(seasons[first].MainTitle?.Trim() ?? string.Empty);
            foreach (var index in indexes) {
                if (index == first)
                    continue;
                var title = seasons[index].MainTitle?.Trim() ?? string.Empty;
                foreach (var stem in stems) {
                    if (TryCutTitlePrefix(title, stem, out var rest)) {
                        names[index] += rest.TrimEnd();
                        break;
                    }
                }
            }
        }

        return names;
    }

    private static bool AiredBefore(Season a, Season b)
        => a.AiredAt.HasValue && (!b.AiredAt.HasValue || a.AiredAt.Value < b.AiredAt.Value);

    [GeneratedRegex(@"\s*\((?:19|20)\d\d\)$")]
    private static partial Regex TrailingYearRegex();

    /// <summary>
    /// Returns the title and the shorter forms of it a related title may start
    /// with instead, longest first: without a year at the end, and up to each
    /// ": ", " - " and "! " in it.
    /// </summary>
    private static List<string> TitleStems(string title) {
        title = title.Trim();
        if (title.Length == 0)
            return [];

        var stems = new List<string> { title };
        var baseTitle = TrailingYearRegex().Replace(title, string.Empty);
        stems.Add(baseTitle);
        foreach (var (text, keep) in new (string, int)[] { (": ", 0), (" - ", 0), ("! ", 1) }) {
            var offset = 0;
            while (true) {
                var at = baseTitle.IndexOf(text, offset, StringComparison.Ordinal);
                if (at < 0)
                    break;
                stems.Add(baseTitle[..(at + keep)]);
                offset = at + text.Length;
            }
        }

        return stems
            .Select((stem, index) => (stem, index))
            .OrderByDescending(tuple => tuple.stem.Length)
            .ThenBy(tuple => tuple.index)
            .Select(tuple => tuple.stem)
            .Distinct(StringComparer.Ordinal)
            .Where(stem => stem.EnumerateRunes().Any(IsTitleCharacter))
            .ToList();
    }

    private static bool IsTitleCharacter(Rune rune)
        => Rune.IsLetter(rune) || Rune.IsDigit(rune);

    /// <summary>
    /// Finds what follows <paramref name="prefix"/> in <paramref name="title"/>.
    /// Only letters and digits are compared, in any case, since names for one
    /// franchise differ in case and punctuation. The prefix must end where a
    /// word of the title ends, so "Gate" is not a prefix of "Gatekeepers" or
    /// of "Gate-keepers".
    /// </summary>
    private static bool TryCutTitlePrefix(string title, string prefix, out string rest) {
        rest = string.Empty;
        var position = 0;
        var matched = false;
        foreach (var want in prefix.EnumerateRunes()) {
            if (!IsTitleCharacter(want))
                continue;

            // Skip anything in the title that isn't a letter or digit.
            while (position < title.Length) {
                var skip = Rune.GetRuneAt(title, position);
                if (IsTitleCharacter(skip))
                    break;
                position += skip.Utf16SequenceLength;
            }
            if (position >= title.Length)
                return false;

            var got = Rune.GetRuneAt(title, position);
            if (Rune.ToLowerInvariant(got) != Rune.ToLowerInvariant(want))
                return false;
            position += got.Utf16SequenceLength;
            matched = true;
        }

        if (!matched)
            return false;

        if (position >= title.Length)
            return true;

        var next = Rune.GetRuneAt(title, position);
        if (IsTitleCharacter(next))
            return false;

        if (next.Value is '-' or '\'' or '’') {
            var afterPosition = position + next.Utf16SequenceLength;
            if (afterPosition < title.Length && IsTitleCharacter(Rune.GetRuneAt(title, afterPosition)))
                return false;
        }

        rest = title[position..];
        return true;
    }

    /// <summary>
    /// Returns the name without the longest of the stems it starts with, or
    /// the name when it starts with none or the rest says too little.
    /// </summary>
    private static string Subtitle(string name, IReadOnlyList<string> stems) {
        foreach (var stem in stems) {
            if (!TryCutTitlePrefix(name, stem, out var rest))
                continue;
            rest = TidySubtitle(rest);
            return SaysEnough(rest) ? rest : name;
        }
        return name;
    }

    private const string SubtitleSeparators = " \t:-–—~?!.…,;/";

    /// <summary>
    /// Drops the separator a subtitle followed, as in ": Pailsen Files" and
    /// " - Solid State Society", and starts its first word with a capital, as
    /// in "the Movie".
    /// </summary>
    private static string TidySubtitle(string rest) {
        var trimmed = rest.TrimStart(SubtitleSeparators.ToCharArray());
        if (rest[..(rest.Length - trimmed.Length)].Contains('~'))
            trimmed = trimmed.TrimEnd('~');
        trimmed = trimmed.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        var first = Rune.GetRuneAt(trimmed, 0);
        var nextPosition = first.Utf16SequenceLength;
        // A single letter is a word of its own, as in "x Bridge".
        if (nextPosition >= trimmed.Length || !Rune.IsLetter(Rune.GetRuneAt(trimmed, nextPosition)))
            return trimmed;

        return Rune.ToUpperInvariant(first).ToString() + trimmed[nextPosition..];
    }

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(@"^(?:\d+(?:st|nd|rd|th)?|[IVX]+)$")]
    private static partial Regex OnlyNumberRegex();

    /// <summary>
    /// The fewest letters and digits a subtitle may have to stand without the
    /// show's title. "Next" and "Arise" do; "II", "S", "2nd" and "OVA" do not.
    /// </summary>
    private const int MinSubtitleCharacters = 4;

    /// <summary>
    /// Reports whether a subtitle can be shown without the show's title.
    /// Notes in parentheses, such as "(2023)" and "(2021 Part 2)", do not
    /// count, so a season that differs only by its year keeps its whole name.
    /// </summary>
    private static bool SaysEnough(string subtitle) {
        var core = ParentheticalRegex().Replace(subtitle, string.Empty).Trim();
        if (OnlyNumberRegex().IsMatch(core))
            return false;
        return core.EnumerateRunes().Count(IsTitleCharacter) >= MinSubtitleCharacters;
    }
}
