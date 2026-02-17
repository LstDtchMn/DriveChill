from pydantic import BaseModel, Field


class FanCurvePoint(BaseModel):
    temp: float = Field(ge=0, le=110, description="Temperature in °C")
    speed: float = Field(ge=0, le=100, description="Fan speed percentage")


class FanCurve(BaseModel):
    id: str
    name: str
    sensor_id: str  # Which sensor drives this curve
    fan_id: str  # Which fan this curve controls
    points: list[FanCurvePoint] = Field(
        default_factory=lambda: [
            FanCurvePoint(temp=30, speed=20),
            FanCurvePoint(temp=50, speed=40),
            FanCurvePoint(temp=70, speed=70),
            FanCurvePoint(temp=85, speed=100),
        ]
    )
    enabled: bool = True
