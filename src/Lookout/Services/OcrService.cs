using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Lookout.Services;

/// <summary>A piece of text OCR found, with its bounding box in image pixels.</summary>
public sealed record OcrCandidate(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>
/// Reads on-screen text with the built-in Windows OCR engine and matches it
/// against a target label, so the highlight feature knows where to point.
/// </summary>
public sealed class OcrService
{
    // Trailing/leading words people add that won't appear in the actual UI text.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "icon", "link", "tab", "menu", "option", "field", "box", "item",
    };

    /// <summary>Runs OCR on a JPEG and returns every recognized word and line.</summary>
    public async Task<IReadOnlyList<OcrCandidate>> RecognizeAsync(
        byte[] jpeg, CancellationToken ct = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
            return Array.Empty<OcrCandidate>();

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        ct.ThrowIfCancellationRequested();

        var result = await engine.RecognizeAsync(bitmap);

        var candidates = new List<OcrCandidate>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0)
                continue;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                candidates.Add(new OcrCandidate(word.Text, r.X, r.Y, r.Width, r.Height));
                minX = Math.Min(minX, r.X);
                minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width);
                maxY = Math.Max(maxY, r.Y + r.Height);
            }

            // Also offer the whole line as a candidate (for multi-word targets).
            candidates.Add(new OcrCandidate(line.Text, minX, minY, maxX - minX, maxY - minY));
        }
        return candidates;
    }

    /// <summary>
    /// Picks the best on-screen candidate for the target label. Returns null if
    /// nothing reasonably matches.
    /// </summary>
    public static OcrCandidate? FindBestMatch(IReadOnlyList<OcrCandidate> candidates, string target)
    {
        var normTarget = Normalize(target);
        if (normTarget.Length == 0)
            return null;

        OcrCandidate? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var normCandidate = Normalize(candidate.Text);
            if (normCandidate.Length == 0)
                continue;

            int score;
            if (normCandidate == normTarget)
                score = 100;
            else if (normCandidate.Contains(normTarget, StringComparison.Ordinal))
                score = 75 - Math.Min(25, normCandidate.Length - normTarget.Length);
            else if (normTarget.Contains(normCandidate, StringComparison.Ordinal) && normCandidate.Length >= 3)
                score = 45;
            else
                score = 0;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static string Normalize(string text)
    {
        var words = text
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !NoiseWords.Contains(w));
        return string.Join(' ', words);
    }
}
