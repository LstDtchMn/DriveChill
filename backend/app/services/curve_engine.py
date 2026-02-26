import math

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
    """Evaluate a fan curve and return the target fan speed (0-100%).

    Returns -1 (skip) if the temperature is not a finite number.
    """
    if not curve.enabled:
        return -1  # -1 means "don't control this fan"
    if not math.isfinite(current_temp):
        return -1  # NaN/Infinity from sensor — don't produce garbage speed
    return max(0, min(100, interpolate_speed(curve.points, current_temp)))


def resolve_composite_temp(
    curve: FanCurve,
    sensor_values: dict[str, float],
) -> float | None:
    """Determine the effective temperature for a curve.

    For composite curves (``sensor_ids`` non-empty), returns the MAX of all
    available sensor temperatures.  Falls back to ``sensor_id`` if no
    composite sensors are available.  Returns *None* when no temperature
    can be determined.
    """
    if curve.sensor_ids:
        temps = [
            sensor_values[sid]
            for sid in curve.sensor_ids
            if sid in sensor_values and math.isfinite(sensor_values[sid])
        ]
        if temps:
            return max(temps)
    # Fallback to single primary sensor
    val = sensor_values.get(curve.sensor_id)
    if val is not None and not math.isfinite(val):
        return None
    return val


# ── Dangerous curve detection ────────────────────────────────────────

DANGER_TEMP_THRESHOLD = 75.0   # °C
DANGER_SPEED_THRESHOLD = 20.0  # %


def check_dangerous_curve(points: list[FanCurvePoint]) -> list[dict]:
    """Check if a curve has dangerously low fan speeds at high temps.

    Returns a list of warning dicts (empty = safe).  Each warning
    contains the offending point's temp and speed.
    """
    warnings: list[dict] = []
    for pt in points:
        if pt.temp > DANGER_TEMP_THRESHOLD and pt.speed < DANGER_SPEED_THRESHOLD:
            warnings.append({
                "temp": pt.temp,
                "speed": pt.speed,
                "message": (
                    f"Fan speed {pt.speed:.0f}% at {pt.temp:.0f}°C is dangerously low. "
                    f"Temperatures above {DANGER_TEMP_THRESHOLD:.0f}°C with fans below "
                    f"{DANGER_SPEED_THRESHOLD:.0f}% risk thermal damage."
                ),
            })

    # Also check interpolated values at key high-temp checkpoints.
    # M-7: only add a checkpoint warning when no existing warning covers the
    # same danger zone (within 5°C) at the same speed (within 1%).  This
    # prevents a single dangerous point from generating 5 near-duplicate
    # warnings while still catching interpolated danger between explicit points.
    if points:
        explicit_temps = {pt.temp for pt in points}
        for check_temp in [80.0, 85.0, 90.0, 95.0, 100.0]:
            if check_temp in explicit_temps:
                continue  # already evaluated as an explicit point above
            speed = interpolate_speed(points, check_temp)
            if speed < DANGER_SPEED_THRESHOLD:
                already_warned = any(
                    abs(w["temp"] - check_temp) <= 5 and abs(w["speed"] - speed) < 1
                    for w in warnings
                )
                if not already_warned:
                    warnings.append({
                        "temp": check_temp,
                        "speed": round(speed, 1),
                        "message": (
                            f"Interpolated fan speed is {speed:.0f}% at {check_temp:.0f}°C. "
                            f"This could allow dangerous temperatures."
                        ),
                    })

    return warnings
