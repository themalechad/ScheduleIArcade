namespace ScheduleIArcade.Utils;

/// <summary>Shared layout numbers used by the room builder and the headless geometry check.</summary>
internal static class ArcadeDimensions
{
    /// <summary>
    /// Clear interior height from the floor to the ceiling underside, in metres. The floor is the
    /// Arcade building's foundation top (world y -3.90) and the ceiling underside is world y -0.50,
    /// 0.10 m under the first-floor line (y -0.40) where the vanilla upper storey begins.
    /// </summary>
    public const float InteriorCeilingHeight = 3.40f;

    /// <summary>Interior wall lining height: floor top to the first-floor line (world -3.90 .. -0.40).</summary>
    public const float InteriorWallHeight = 3.50f;

    /// <summary>Solid ceiling slab thickness (underside -0.50 to top -0.40, world).</summary>
    public const float CeilingSlabThickness = 0.10f;

    public const float MinWalkwayClearance = 1.00f;
    public const float CabinetColliderInset = 0.14f;
    public const float PinballColliderHeight = 1.00f;
    public const float PacManNativeWidth = 224;
    public const float PacManNativeHeight = 288;
}
