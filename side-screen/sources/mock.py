"""Simulated readings for developing the UI without the router or hardware sensors.

The shapes imitate real use rather than noise: a direct download that bursts to ~110 MB/s
for a while, video over the proxy at a few MB/s, CPU and GPU load that drift and spike,
and temperatures, clocks and power that follow the load.
"""

from __future__ import annotations

import random

MB = 1024 * 1024
KB = 1024


class _Drift:
    """A value that wanders towards a target that changes every few seconds."""

    def __init__(self, rng: random.Random, low: float, high: float, speed: float = 0.25):
        self._rng = rng
        self.low, self.high, self.speed = low, high, speed
        self.value = rng.uniform(low, high)
        self._target = self.value
        self._hold = 0

    def step(self) -> float:
        if self._hold <= 0:
            self._target = self._rng.uniform(self.low, self.high)
            self._hold = self._rng.randint(3, 10)
        self._hold -= 1
        self.value += (self._target - self.value) * self.speed
        self.value += self._rng.gauss(0, (self.high - self.low) * 0.02)
        self.value = min(self.high, max(self.low, self.value))
        return self.value


class MockSource:
    """Produces one snapshot per call; see backend.Dashboard for the snapshot layout."""

    simulated = True

    def __init__(self, seed: int | None = None):
        rng = random.Random(seed)
        self._rng = rng
        self._browse = _Drift(rng, 20 * KB, 2 * MB)
        self._video = _Drift(rng, 1.5 * MB, 8 * MB)
        self._proxy_up = _Drift(rng, 40 * KB, 400 * KB)
        self._cpu = _Drift(rng, 6, 55, 0.35)
        self._gpu = _Drift(rng, 3, 97, 0.3)
        self._memory = _Drift(rng, 13.5, 22.0, 0.1)
        self._disk_read = _Drift(rng, 0, 40 * MB, 0.5)
        self._disk_write = _Drift(rng, 0, 12 * MB, 0.5)
        self._burst = 0  # seconds left in a large direct download
        self._video_on, self._video_left = True, rng.randint(20, 60)  # proxy streaming on/off
        self._cpu_temperature = 45.0
        self._gpu_temperature = 42.0

    def sample(self) -> dict:
        rng = self._rng
        if self._burst <= 0 and rng.random() < 0.05:
            self._burst = rng.randint(12, 35)
        if self._burst > 0:
            self._burst -= 1
            direct_down = rng.uniform(85, 116) * MB
        else:
            direct_down = self._browse.step()
        direct_up = direct_down * rng.uniform(0.008, 0.03) + rng.uniform(2, 30) * KB
        self._video_left -= 1
        if self._video_left <= 0:
            self._video_on = not self._video_on
            self._video_left = rng.randint(20, 60) if self._video_on else rng.randint(5, 15)
        proxy_down = self._video.step() if self._video_on else rng.uniform(5, 60) * KB
        proxy_up = self._proxy_up.step()

        cpu = self._cpu.step() + (25 if self._burst > 0 else 0) * rng.random()
        cpu = min(100.0, cpu)
        gpu = self._gpu.step()
        # Temperatures lag behind load instead of jumping with it.
        self._cpu_temperature += (38 + cpu * 0.5 - self._cpu_temperature) * 0.15
        self._gpu_temperature += (36 + gpu * 0.42 - self._gpu_temperature) * 0.12
        return {
            "connected": True,
            "status": "已连接 · IPv4",
            "network": {
                "direct": {"down": direct_down, "up": direct_up},
                "proxy": {"down": proxy_down, "up": proxy_up},
            },
            "cpu": {
                "name": "AMD Ryzen 7 7800X3D",
                "usage": cpu,
                "temperature": self._cpu_temperature,
                "frequency": 3.8 + cpu / 100 * 1.25 + rng.uniform(-0.05, 0.05),
                "power": 22 + cpu * 0.9,
            },
            "gpu": {
                "name": "NVIDIA GeForce RTX 4070",
                "usage": gpu,
                "temperature": self._gpu_temperature,
                "frequency": 0.21 + gpu / 100 * 2.5,
                "memoryUsed": 2.1 + gpu / 100 * 8.2,
                "memoryTotal": 12.0,
                "power": 12 + gpu * 1.9,
            },
            "memory": {"used": self._memory.step(), "total": 32.0},
            "disk": {"read": self._disk_read.step(), "write": self._disk_write.step()},
        }
