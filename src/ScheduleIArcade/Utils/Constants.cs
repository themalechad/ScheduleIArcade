namespace ScheduleIArcade.Utils;

internal static class Constants
{
    public const string ModName = "Schedule I Arcade";
    public const string ModVersion = "0.6.0";
    public const string ModAuthor = "GrandmasAnkles/XUnfairX";
    public const string PreferencesCategory = "ScheduleIArcade";

    public const string ModFolderName = "ScheduleIArcade";
    public const string BundleFileName = "scheduleiarcade";
    public const string RoomPrefabAssetName = "assets/scheduleiarcade/generated/arcaderoom.prefab";
    public const string AmbienceFileName = "arcade_ambience.wav";

    public const string CabinetPrefix = "ARCADE_CABINET_";
    public const string ScreenPrefix = "ARCADE_SCREEN_";
    public const string SpawnMarkerName = "ARCADE_SPAWN";
    public const string ExitMarkerName = "ARCADE_EXIT";

    // A close, deliberate look is required before an arcade interaction prompt is shown.
    public const float InteractionDistance = 2.35f;
    public const string DefaultGameId = "test_pattern";

    // Stable native build identity for the custom arcade. The arcade no longer uses another
    // property's identity for furniture ownership/culling. The source property is only used once
    // as a Tile/Grid template.
    public const string ArcadePropertyObjectName = "SCHEDULE_I_ARCADE_PROPERTY_PROXY";
    public const string ArcadePropertyName = "Schedule I Arcade";
    public const string ArcadePropertyCode = "scheduleiarcade";
    public const string ArcadeFloorGridGuid = "e8fa16cf-fa2a-4c20-a38f-e571fd1aa5e0";

    public static readonly string[] ArcadeWallSurfaceGuids =
    {
        "63ebe663-6727-4fc1-80d3-00ab42677b55",
        "38887e82-705b-4cd4-8f04-543b3412acc7",
        "b22c0872-eba5-4200-9d71-66535dc694e3",
        "90224423-1a8a-44a1-87af-f36d043cf1af",
        "2a53e579-4a9b-4306-8228-9c4d87898add",
        "6be88bf4-14a3-4722-8a8c-e318793599f5"
    };
}
