from app.models.fan_curves import FanCurve, FanCurvePoint


def interpolate_speed(points: list[FanCurvePoint], temperature: float) -> float:
    """Calculate fan speed for a given temperature using linear interpolation.

    Points must have at least one entry. If temperature is below the first point
    or above the last point, the corresponding boundary speed is returned.
    """
    if not points:
        return 0.0

    # Sort by temperature
    sorted_points = sorted(points, key=lambda p: p.temp)

    # Below minimum temp
    if temperature <= sorted_points[0].temp:
        return sorted_points[0].speed

    # Above maximum temp
    if temperature >= sorted_points[-1].temp:
        return sorted_points[-1].speed

    # Find the two surrounding points and interpolate
    for i in range(len(sorted_points) - 1):
        p1 = sorted_points[i]
        p2 = sorted_points[i + 1]
        if p1.temp <= temperature <= p2.temp:
            temp_range = p2.temp - p1.temp
            if temp_range == 0:
                return p1.speed
            ratio = (temperature - p1.temp) / temp_range
            return p1.speed + ratio * (p2.speed - p1.speed)

    return sorted_points[-1].speed


def evaluate_curve(curve: FanCurve, current_temp: float) -> float:
    """Evaluate a fan curve and return the target fan speed (0-100%)."""
    if not curve.enabled:
        return -1  # -1 means "don't control this fan"
    return max(0, min(100, interpolate_speed(curve.points, current_temp)))
