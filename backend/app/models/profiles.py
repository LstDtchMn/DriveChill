from enum import Enum

from pydantic import BaseModel

from app.models.fan_curves import FanCurve, FanCurvePoint


class ProfilePreset(str, Enum):
    SILENT = "silent"
    BALANCED = "balanced"
    PERFORMANCE = "performance"
    FULL_SPEED = "full_speed"
    GAMING = "gaming"
    RENDERING = "rendering"
    SLEEP = "sleep"
    CUSTOM = "custom"


PRESET_CURVES: dict[ProfilePreset, list[FanCurvePoint]] = {
    ProfilePreset.SILENT: [
        FanCurvePoint(temp=0, speed=0),
        FanCurvePoint(temp=45, speed=15),
        FanCurvePoint(temp=65, speed=35),
        FanCurvePoint(temp=80, speed=60),
        FanCurvePoint(temp=90, speed=100),
    ],
    ProfilePreset.BALANCED: [
        FanCurvePoint(temp=0, speed=20),
        FanCurvePoint(temp=40, speed=30),
        FanCurvePoint(temp=60, speed=50),
        FanCurvePoint(temp=75, speed=75),
        FanCurvePoint(temp=85, speed=100),
    ],
    ProfilePreset.PERFORMANCE: [
        FanCurvePoint(temp=0, speed=35),
        FanCurvePoint(temp=35, speed=50),
        FanCurvePoint(temp=55, speed=70),
        FanCurvePoint(temp=70, speed=90),
        FanCurvePoint(temp=80, speed=100),
    ],
    ProfilePreset.FULL_SPEED: [
        FanCurvePoint(temp=0, speed=100),
        FanCurvePoint(temp=100, speed=100),
    ],
    # Gaming: aggressive GPU cooling, quiet CPU at idle
    ProfilePreset.GAMING: [
        FanCurvePoint(temp=0, speed=15),
        FanCurvePoint(temp=40, speed=20),
        FanCurvePoint(temp=55, speed=40),
        FanCurvePoint(temp=70, speed=75),
        FanCurvePoint(temp=80, speed=95),
        FanCurvePoint(temp=90, speed=100),
    ],
    # Rendering: moderate sustained load, all fans balanced
    ProfilePreset.RENDERING: [
        FanCurvePoint(temp=0, speed=25),
        FanCurvePoint(temp=45, speed=40),
        FanCurvePoint(temp=60, speed=55),
        FanCurvePoint(temp=75, speed=70),
        FanCurvePoint(temp=85, speed=85),
        FanCurvePoint(temp=95, speed=100),
    ],
    # Sleep: absolute minimum RPM, zero-RPM where supported
    ProfilePreset.SLEEP: [
        FanCurvePoint(temp=0, speed=0),
        FanCurvePoint(temp=50, speed=0),
        FanCurvePoint(temp=60, speed=10),
        FanCurvePoint(temp=75, speed=30),
        FanCurvePoint(temp=85, speed=60),
        FanCurvePoint(temp=95, speed=100),
    ],
}


class Profile(BaseModel):
    id: str
    name: str
    preset: ProfilePreset = ProfilePreset.CUSTOM
    curves: list[FanCurve] = []
    is_active: bool = False
