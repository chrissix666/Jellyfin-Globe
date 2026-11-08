using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.Globe.Data;

/// <summary>
/// Parental ratings the way Jellyfin compares them: 10.10.7 turns a rating into a level, 12.x into a score with a
/// sub-score that depends on the metadata country. A globe's limit and a title's rating go through the same call.
/// </summary>
public static class Ratings
{
    /// <summary>The score of a rating as Jellyfin names it ("DE-12", "PG-13"), null for one it does not know.</summary>
    public static (int Score, int Sub)? Score(string rating, ILocalizationManager localization, string? country)
    {
#if NET10_0_OR_GREATER
        var score = localization.GetRatingScore(rating, country);
        return score is null ? null : (score.Score, score.SubScore ?? 0);
#else
        var level = localization.GetRatingLevel(rating, country);
        return level is null ? null : (level.Value, 0);
#endif
    }

    /// <summary>A title's rating as Jellyfin's parental control reads it: its custom rating, else its own or inherited one.</summary>
    public static (int Score, int Sub)? Of(BaseItem item, ILocalizationManager localization)
    {
        var rating = item.CustomRatingForComparison;
        if (string.IsNullOrEmpty(rating))
        {
            rating = item.OfficialRatingForComparison;
        }

        return string.IsNullOrEmpty(rating) ? null : Score(rating, localization, item.GetPreferredMetadataCountryCode());
    }

    public static bool AtMost((int Score, int Sub) value, (int Score, int Sub) limit) =>
        value.Score != limit.Score ? value.Score < limit.Score : value.Sub <= limit.Sub;
}
