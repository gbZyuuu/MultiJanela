using System.Drawing;

namespace RobloxMultiManager.Core;

public static class VisibleFrameLayout
{
    /// <summary>Convert a visible target to SetWindowPos bounds without guessing DPI border sizes.</summary>
    public static Rectangle ToOuter(Rectangle target, Rectangle outer, Rectangle visible)
    {
        if (!outer.Contains(visible) || visible.Width <= 0 || visible.Height <= 0) return target;
        var left = visible.Left - outer.Left;
        var top = visible.Top - outer.Top;
        var right = outer.Right - visible.Right;
        var bottom = outer.Bottom - visible.Bottom;
        return Rectangle.FromLTRB(target.Left - left, target.Top - top, target.Right + right, target.Bottom + bottom);
    }
}
