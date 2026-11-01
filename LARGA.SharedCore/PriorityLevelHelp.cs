namespace LARGA.SharedCore;

/// <summary>
/// The "(i)" explanation next to PRIORITY LEVEL - the driver's defect report (VehicleDefectPage) and the
/// web Garage's Create Ticket form show the same words. Display only: the level a report or ticket is saved
/// with only sets the order and label in the Garage (High is listed first). Nothing blocks clock-in or marks
/// the unit Under Maintenance because of it - the Note says so, so the help never promises more.
/// </summary>
public static class PriorityLevelHelp
{
    public const string Low =
        "Cosmetic or comfort issue; the taxi is safe to drive (e.g. small scratch or dent, torn seat cover, loose trim). Can wait for the next routine check.";

    public const string Medium =
        "Something not working right that could get worse, but the taxi is still safe to drive today (e.g. weak aircon, interior light out, worn wiper, strange but minor noise). Should be fixed within a few days.";

    public const string High =
        "A safety problem or likely breakdown; the unit shouldn't be driven until it's checked (e.g. brakes, tires, steering, engine warning light, headlights or brake lights out, fluid leaks).";

    public const string Note =
        "The level tells the manager how urgent it is - High is listed first in the Garage. It doesn't block clock-in or take the unit off the road by itself; a manager decides that.";
}
