namespace ScheduleIArcade.Utils;

/// <summary>Shared layout numbers used by the room builder and the headless geometry check.</summary>
internal static class ArcadeDimensions
{
    /// <summary>Interior ceiling height above the room floor, in metres.</summary>
    public const float InteriorCeilingHeight = 3.40f;

    /// <summary>Generated interior wall height (slightly above the ceiling plane).</summary>
    public const float InteriorWallHeight = 3.48f;

    public const float MinWalkwayClearance = 1.00f;
    public const float CabinetColliderInset = 0.14f;
    public const float PinballColliderHeight = 1.00f;
    public const float PacManNativeWidth = 224;
    public const float PacManNativeHeight = 288;
}
