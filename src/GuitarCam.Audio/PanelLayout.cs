namespace GuitarCam.Audio;

/// <summary>Port of computePanelRects (GuitarCam/src/shared/panelLayout.js).</summary>
public static class PanelLayout
{
    const double TargetCellAspect = 16.0 / 9;
    const double EmptyCellPenalty = 0.15;

    static (int Rows, int Cols) ChooseGrid(int n, string layout, double W, double H)
    {
        if (n <= 3) return layout == "top-to-bottom" ? (n, 1) : (1, n);
        (int rows, int cols, double score, int prefer)? best = null;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (n + cols - 1) / cols;
            double aspect = W / cols / (H / rows);
            double score = Math.Abs(Math.Log(aspect / TargetCellAspect)) + EmptyCellPenalty * (rows * cols - n);
            // tie-break by the user's preference: more rows for top-to-bottom, more columns otherwise
            int prefer = layout == "top-to-bottom" ? rows : cols;
            if (best is not { } b || score < b.score - 1e-9 || (Math.Abs(score - b.score) < 1e-9 && prefer > b.prefer))
                best = (rows, cols, score, prefer);
        }
        return (best!.Value.rows, best.Value.cols);
    }

    public static List<(int X, int Y, int W, int H)> Compute(int n, string layout, int W, int H)
    {
        var rects = new List<(int, int, int, int)>();
        if (n <= 0) return rects;
        var (rows, cols) = ChooseGrid(n, layout, W, H);
        for (int r = 0; r < rows; r++)
        {
            int start = r * cols;
            int count = Math.Min(cols, n - start);
            if (count <= 0) break;
            int y0 = (int)Math.Round((double)r * H / rows, MidpointRounding.AwayFromZero);
            int y1 = (int)Math.Round((double)(r + 1) * H / rows, MidpointRounding.AwayFromZero);
            for (int c = 0; c < count; c++)
            {
                int x0 = (int)Math.Round((double)c * W / count, MidpointRounding.AwayFromZero);
                int x1 = (int)Math.Round((double)(c + 1) * W / count, MidpointRounding.AwayFromZero);
                rects.Add((x0, y0, x1 - x0, y1 - y0));
            }
        }
        return rects;
    }
}
