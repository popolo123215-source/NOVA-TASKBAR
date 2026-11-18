namespace Nova.WindowsClient;

internal static class PanelDockGeometry
{
    public const double SnapThreshold = 32;

    public static bool IsVertical(string? edge) => edge is "left" or "right";

    public static string? FindNearestEdge(
        double left,
        double top,
        double width,
        double height,
        double workLeft,
        double workTop,
        double workWidth,
        double workHeight,
        double threshold = SnapThreshold)
    {
        var distances = new (string Edge, double Distance)[]
        {
            ("left", Math.Abs(left - workLeft)),
            ("right", Math.Abs(workLeft + workWidth - (left + width))),
            ("top", Math.Abs(top - workTop)),
            ("bottom", Math.Abs(workTop + workHeight - (top + height)))
        };
        var nearest = distances.MinBy(item => item.Distance);
        return nearest.Distance <= threshold ? nearest.Edge : null;
    }

    public static (double Left, double Top) GetPositionAtEdge(
        string edge,
        double workLeft,
        double workTop,
        double workWidth,
        double workHeight,
        double width,
        double height)
    {
        var targetLeft = edge switch
        {
            "left" => workLeft + 10,
            "right" => workLeft + workWidth - width - 10,
            _ => workLeft + (workWidth - width) / 2
        };
        var targetTop = edge switch
        {
            "bottom" => workTop + workHeight - height - 10,
            "left" or "right" => workTop + (workHeight - height) / 2,
            _ => workTop + 8
        };
        return (
            Math.Clamp(targetLeft, workLeft, Math.Max(workLeft, workLeft + workWidth - width)),
            Math.Clamp(targetTop, workTop, Math.Max(workTop, workTop + workHeight - height)));
    }
}
