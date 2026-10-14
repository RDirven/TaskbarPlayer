using System.Windows.Automation;

namespace TaskbarPlayer;

/// <summary>Free space in the taskbar, in physical screen coordinates.</summary>
internal sealed record TaskbarLayout(Rectangle Band, Rectangle LeftGap, Rectangle RightGap);

/// <summary>
/// Reads the real button positions of the Windows 11 taskbar through UI Automation. The XAML taskbar draws its
/// icons itself, so the classic child windows (ReBarWindow32 etc.) no longer report where the icons are.
/// Call from a background thread: Explorer may call back into our widget window while we wait.
/// </summary>
internal static class TaskbarProbe
{
    private const string FrameClass = "Taskbar.TaskbarFrameAutomationPeer";
    private const string TaskButtonPrefix = "Taskbar.TaskListButton";

    public static TaskbarLayout? Probe(IntPtr tray)
    {
        try
        {
            var root = AutomationElement.FromHandle(tray);
            var cache = new CacheRequest { TreeScope = TreeScope.Element };
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ClassNameProperty);

            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ClassNameProperty, FrameClass));

            AutomationElementCollection found;
            using (cache.Activate())
            {
                found = root.FindAll(TreeScope.Descendants, condition);
            }

            var frame = Rectangle.Empty;
            var buttons = new List<(Rectangle Rect, bool IsTask)>();
            foreach (AutomationElement element in found)
            {
                var r = element.Cached.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                var rect = Rectangle.FromLTRB((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
                string cls = element.Cached.ClassName ?? "";
                if (cls == FrameClass)
                {
                    frame = rect;
                }
                else
                {
                    buttons.Add((rect, cls.StartsWith(TaskButtonPrefix, StringComparison.Ordinal)));
                }
            }

            // Only horizontal taskbars with at least one app button are handled here.
            if (frame.IsEmpty || frame.Width < frame.Height) return null;
            buttons = buttons
                .Where(b => b.Rect.Bottom > frame.Top && b.Rect.Top < frame.Bottom)
                .OrderBy(b => b.Rect.Left)
                .ToList();
            int first = buttons.FindIndex(b => b.IsTask);
            int last = buttons.FindLastIndex(b => b.IsTask);
            if (first < 0) return null;

            // Grow the app group with adjacent buttons (Start, Search, Task view).
            const int touching = 4;
            while (first > 0 && buttons[first - 1].Rect.Right >= buttons[first].Rect.Left - touching) first--;
            while (last < buttons.Count - 1 && buttons[last + 1].Rect.Left <= buttons[last].Rect.Right + touching) last++;

            int groupLeft = buttons[first].Rect.Left;
            int groupRight = buttons.Skip(first).Take(last - first + 1).Max(b => b.Rect.Right);
            int leftBound = first > 0 ? buttons.Take(first).Max(b => b.Rect.Right) : frame.Left;
            int rightBound = last < buttons.Count - 1 ? buttons.Skip(last + 1).Min(b => b.Rect.Left) : frame.Right;

            return new TaskbarLayout(
                frame,
                Rectangle.FromLTRB(leftBound, frame.Top, Math.Max(leftBound, groupLeft), frame.Bottom),
                Rectangle.FromLTRB(groupRight, frame.Top, Math.Max(groupRight, rightBound), frame.Bottom));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
