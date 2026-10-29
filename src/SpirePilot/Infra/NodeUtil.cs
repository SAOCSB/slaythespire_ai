using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace SpirePilot.Infra;

/// <summary>Scene-tree helpers. Main thread only.</summary>
internal static class NodeUtil
{
    public static bool Valid(GodotObject? o) => o != null && GodotObject.IsInstanceValid(o);

    public static bool Alive(Node? n) => Valid(n) && !n!.IsQueuedForDeletion() && n.IsInsideTree();

    public static bool VisibleInTree(CanvasItem? c) => Alive(c) && c!.IsVisibleInTree();

    /// <summary>Visible, enabled clickable control.</summary>
    public static bool Usable(NClickableControl? b) => VisibleInTree(b) && b!.IsEnabled;

    public static IEnumerable<T> Descendants<T>(Node? root, int maxDepth = 64) where T : class
    {
        if (!Alive(root)) yield break;
        var stack = new Stack<(Node node, int depth)>();
        stack.Push((root!, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (node is T match && !ReferenceEquals(node, root)) yield return match;
            if (depth >= maxDepth) continue;
            int count;
            try { count = node.GetChildCount(); }
            catch (ObjectDisposedException) { continue; }
            for (int i = count - 1; i >= 0; i--)
            {
                Node? child;
                try { child = node.GetChild(i); }
                catch (ObjectDisposedException) { continue; }
                if (Alive(child)) stack.Push((child!, depth + 1));
            }
        }
    }

    public static T? First<T>(Node? root) where T : class => Descendants<T>(root).FirstOrDefault();

    /// <summary>Nodes sorted top-to-bottom, left-to-right on screen (child order is unreliable for card grids).</summary>
    public static List<T> ByScreenPosition<T>(IEnumerable<T> nodes) where T : Control =>
        nodes.OrderBy(n => Math.Round(n.GlobalPosition.Y / 8)).ThenBy(n => n.GlobalPosition.X).ToList();

    public static Node? SceneRoot => (Engine.GetMainLoop() as SceneTree)?.Root;

    public static T? Get<T>(Node? parent, string path) where T : class
    {
        if (!Alive(parent)) return null;
        try { return parent!.GetNodeOrNull(path) as T; }
        catch (Exception) { return null; }
    }
}
