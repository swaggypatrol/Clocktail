# Power, current, temperature and fans
The wall, what a watt buys, current limits, the fan curve and what temperature costs.

## Power
- Board power request on the 5090 FE: **min 400 W, default 575 W, max 600 W**. The driver refuses less than 400 W. The FE request is the one enforced. A driver reset puts it back to 575 W; the service restores the daily one.
- What the last watts buy (GTA V, tuned curve): 600 W 86 fps; 550 W 82; 500 W 78; 450 W 73; 400 W 67. The last 100 W buy about 10 % frames; 475-500 W is the efficient zone. Witcher 3: 46 fps at 600 W, 43 at 500 W.
- Efficiency, board MHz per W: 4.74 at 600 W, 5.25 at 500, 5.68 at 400, 6.05 at 250 (the peak). Below ~820 mV NVVDD sits at its floor and only the clock drops.
- A 575 W wall instead of 600 W cost about 25 MHz.
- 3DMark on the founders profile: Speed Way 14733 (2895-2917 MHz, 975-980 mV), Steel Nomad 15177 (2880-2940 MHz, on the 600 W wall, 606 W peak).

## Current limits (OCP)
- NVVDD 600 A, MSVDD 180 A by default; Clocktail never goes above the default. Games draw NVVDD 250-330 A, nvgwls full load 422 A, so 600 A is far away.
- A current limit is closed-loop: current holds 1-2 % under the cap, no trip, no reset. The card walks down the curve as under a wall, and NVML reports both as `sw_power_cap`.
- NVVDD caps around 380-400 A lift XBAR and MSVDD for unknown reasons; use the wall above 400 W. Current limits are trial-only and never kept.

## Temperature
- Thermal throttling was never reached on the reference card; heat costs clock through the driver's temperature compensation and leakage instead (see `manual blackwell`).
- Steady temperature model (nvgwls full load): T = inlet + P x (0.023 + 0.030 / fan fraction) C, where inlet is the case air (~35 C on the reference rig). 600 W at 100 % fan ~67 C; 75 % ~73 C; 60 % ~79 C; 45 % passed 85 C in 40 s.
- Each degree cooler case air is a degree cooler GPU; airflow beats fan speed.
- Sweet spot at 600 W: 78-80 C (fan ~60 %): about 20 MHz better than the stock curve's 83 C target and under the 82 C compensation step. Below ~72 C needs 75 %+ fan and is not worth the noise.

## Fans
- **Finding the efficient wall (frames per watt).** Warm the card, pick one fixed scene, then step the wall down a trial at a time: `begin_session`, `set_power_limit` 600 → 400 W in 25 W steps, and at each step `watch` 60-120 s in the same scene. Each `watch` gives `efficiency.fps_per_100w` with the frames: list wall, fps, fps/100 W and find the bend, where fps start to fall faster than watts. Ask the person, then `set_daily_power_limit` there. Under 400 W the board refuses; use `set_current_limit nvvdd` 350 → 200 A the same way. On the reference card the last 100 W bought about 10 % frames, and stress ran 4.74 MHz/W at 600 W against 6.05 at 250 A. `stress_test` gives `efficiency.mhz_per_w` when there is no game to measure frames in.
- The firmware fan curve has three points (temperature : duty : rpm). Stock: 50 C 20 % (1100 rpm), 83 C 50 % (1775), 88 C 100 % (2875). The founders preset: 50 C 20 %, 79 C 60 % (1995 rpm), 86 C 100 %.
- The firmware drives by the rpm column, not the duty; Clocktail writes both. Fans stop below about 43 C (normal). Spinning, the firmware minimum is 30 %.
- The fan curve follows a temperature 1.5-2.5 C under NVML's while heating.
- Do not stress at 600 W with fans held at 45 % or less.
- `set_fan_curve` changes the curve (a trial, kept by `keep_as_daily`); `set_fan_speed` holds a fixed duty for a test (never kept). Set the curve before tuning the V/F curve: `manual method`, phase 2.
- **Fan health:** under the firmware's curve each fan's tachometer follows the target rpm to ±5 rpm, so card_report flags a fan more than 25 % (and 300 rpm) off the target as ⚠️, and a fan at 0 rpm while the curve asks for more than 1000 as ⛔ (stopped or disconnected). NVML's fan % is the PWM the firmware gives to reach that rpm, not the curve's duty column (65 % against 59.6 % at 79 °C on the reference card). With the fans stopped at idle the card warms by itself until the curve temperature reaches 50 °C, then they start (43 °C stops them again): normal.
