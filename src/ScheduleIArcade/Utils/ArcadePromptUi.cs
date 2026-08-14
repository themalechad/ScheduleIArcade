using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// A single high-contrast prompt treatment for arcade interactions. Prompts are only drawn by
/// callers after their close-range crosshair raycast succeeds; this class only controls clarity.
/// </summary>
internal static class ArcadePromptUi
{
    internal enum Choice
    {
        None,
        Primary,
        Secondary
    }

    private static GUIStyle? _titleStyle;
    private static GUIStyle? _actionStyle;
    private static GUIStyle? _detailStyle;

    public static void DrawCard(string title, string action, string detail, float bottomOffset, Color accent)
    {
        EnsureStyles();
        if (_titleStyle == null || _actionStyle == null || _detailStyle == null)
            return;

        float width = Mathf.Min(510f, Screen.width - 36f);
        float height = 76f;
        float x = (Screen.width - width) * 0.5f;
        float y = Mathf.Max(18f, Screen.height - bottomOffset - height);
        Color previous = GUI.color;

        GUI.color = new Color(0.015f, 0.025f, 0.06f, 0.96f);
        GUI.Box(new Rect(x, y, width, height), string.Empty);
        GUI.color = accent;
        GUI.Box(new Rect(x, y, width, 4f), string.Empty);
        GUI.color = Color.white;
        GUI.Label(new Rect(x + 16f, y + 9f, width - 32f, 20f), title, _titleStyle);
        GUI.Label(new Rect(x + 16f, y + 29f, width - 32f, 26f), action, _actionStyle);
        GUI.Label(new Rect(x + 16f, y + 54f, width - 32f, 17f), detail, _detailStyle);
        GUI.color = previous;
    }

    /// <summary>Draws the exterior arcade-entry fallback using the same visual language as room prompts.</summary>
    public static Choice DrawChoiceCard(string title, string action, string detail, string primary, string secondary, Color accent)
    {
        EnsureStyles();
        if (_titleStyle == null || _actionStyle == null || _detailStyle == null)
            return Choice.None;

        float width = Mathf.Min(510f, Screen.width - 36f);
        const float height = 166f;
        float x = (Screen.width - width) * 0.5f;
        float y = (Screen.height - height) * 0.5f;
        Color previous = GUI.color;

        GUI.color = new Color(0.015f, 0.025f, 0.06f, 0.97f);
        GUI.Box(new Rect(x, y, width, height), string.Empty);
        GUI.color = accent;
        GUI.Box(new Rect(x, y, width, 4f), string.Empty);
        GUI.color = Color.white;
        GUI.Label(new Rect(x + 16f, y + 13f, width - 32f, 20f), title, _titleStyle);
        GUI.Label(new Rect(x + 16f, y + 38f, width - 32f, 26f), action, _actionStyle);
        GUI.Label(new Rect(x + 16f, y + 65f, width - 32f, 20f), detail, _detailStyle);

        bool primaryClicked = GUI.Button(new Rect(x + 16f, y + 97f, width - 32f, 28f), primary);
        bool secondaryClicked = GUI.Button(new Rect(x + 16f, y + 132f, width - 32f, 22f), secondary);
        GUI.color = previous;
        return primaryClicked ? Choice.Primary : secondaryClicked ? Choice.Secondary : Choice.None;
    }

    private static void EnsureStyles()
    {
        if (_titleStyle != null)
            return;

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            wordWrap = false
        };
        _titleStyle.normal.textColor = new Color(0.60f, 0.82f, 1.00f, 1f);

        _actionStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            wordWrap = false
        };
        _actionStyle.normal.textColor = Color.white;

        _detailStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12,
            wordWrap = false
        };
        _detailStyle.normal.textColor = new Color(0.78f, 0.84f, 0.94f, 1f);
    }
}
