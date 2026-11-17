using System.Drawing;

namespace Nova.WindowsClient.Services;

internal static class BrowserWindowGeometry
{
    public static Rectangle? FindNonOverlappingBounds(Rectangle window, Rectangle obstruction, Rectangle workArea)
    {
        var blocked = Rectangle.Intersect(obstruction, workArea);
        if (window.Width <= 0 || window.Height <= 0 || workArea.Width <= 0 || workArea.Height <= 0 ||
            blocked.IsEmpty || !window.IntersectsWith(blocked))
            return null;

        var sameSizeCandidates = new List<Rectangle>();
        AddIfFits(sameSizeCandidates, workArea.Left, Clamp(window.Top, workArea.Top, workArea.Bottom - window.Height), window.Width, window.Height,
            new Rectangle(workArea.Left, workArea.Top, Math.Max(0, blocked.Left - workArea.Left), workArea.Height));
        AddIfFits(sameSizeCandidates, blocked.Right, Clamp(window.Top, workArea.Top, workArea.Bottom - window.Height), window.Width, window.Height,
            new Rectangle(blocked.Right, workArea.Top, Math.Max(0, workArea.Right - blocked.Right), workArea.Height));
        AddIfFits(sameSizeCandidates, Clamp(window.Left, workArea.Left, workArea.Right - window.Width), workArea.Top, window.Width, window.Height,
            new Rectangle(workArea.Left, workArea.Top, workArea.Width, Math.Max(0, blocked.Top - workArea.Top)));
        AddIfFits(sameSizeCandidates, Clamp(window.Left, workArea.Left, workArea.Right - window.Width), blocked.Bottom, window.Width, window.Height,
            new Rectangle(workArea.Left, blocked.Bottom, workArea.Width, Math.Max(0, workArea.Bottom - blocked.Bottom)));

        if (sameSizeCandidates.Count > 0)
            return sameSizeCandidates.MinBy(candidate => DistanceSquared(window, candidate));

        var availableRegions = new[]
        {
            new Rectangle(workArea.Left, workArea.Top, Math.Max(0, blocked.Left - workArea.Left), workArea.Height),
            new Rectangle(blocked.Right, workArea.Top, Math.Max(0, workArea.Right - blocked.Right), workArea.Height),
            new Rectangle(workArea.Left, workArea.Top, workArea.Width, Math.Max(0, blocked.Top - workArea.Top)),
            new Rectangle(workArea.Left, blocked.Bottom, workArea.Width, Math.Max(0, workArea.Bottom - blocked.Bottom))
        };
        var region = availableRegions
            .Where(candidate => candidate.Width > 0 && candidate.Height > 0)
            .OrderByDescending(candidate => (long)candidate.Width * candidate.Height)
            .ThenBy(candidate => DistanceSquared(window, candidate))
            .FirstOrDefault();

        return region.IsEmpty ? null : region;
    }

    private static void AddIfFits(
        ICollection<Rectangle> candidates,
        int x,
        int y,
        int width,
        int height,
        Rectangle available)
    {
        if (width > 0 && height > 0 && available.Width >= width && available.Height >= height)
            candidates.Add(new Rectangle(x, y, width, height));
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    private static long DistanceSquared(Rectangle first, Rectangle second)
    {
        var dx = (long)first.Left - second.Left;
        var dy = (long)first.Top - second.Top;
        return dx * dx + dy * dy;
    }
}
