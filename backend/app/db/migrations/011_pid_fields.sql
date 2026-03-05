-- Add PID controller fields to temperature_targets.
-- When pid_mode = 0 (default), the existing proportional controller is used.
-- When pid_mode = 1, the PID controller replaces proportional control.

ALTER TABLE temperature_targets ADD COLUMN pid_mode INTEGER NOT NULL DEFAULT 0;
ALTER TABLE temperature_targets ADD COLUMN pid_kp   REAL    NOT NULL DEFAULT 5.0;
ALTER TABLE temperature_targets ADD COLUMN pid_ki   REAL    NOT NULL DEFAULT 0.05;
ALTER TABLE temperature_targets ADD COLUMN pid_kd   REAL    NOT NULL DEFAULT 1.0;
