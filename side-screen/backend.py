"""Turns one snapshot per second into ready-to-draw values for QML.

Snapshot layout (every leaf may be None when a reading is unavailable):

    connected: bool, status: str
    network: {direct: {down, up}, proxy: {down, up}}      bytes per second
    cpu:     {name, usage %, temperature °C, frequency GHz, power W}
    gpu:     {name, usage %, temperature °C, frequency GHz, memoryUsed GB, memoryTotal GB, power W}
    memory:  {used GB, total GB}
    disk:    {read, write}                                 bytes per second

Formatting lives here so QML only lays out text: rates become (number, unit) pairs with
1024-based units, matching the Windows panel, and every card gets a 60-second history.
"""

from __future__ import annotations

import math
from collections import deque

from PySide6.QtCore import Property, QObject, QTimer, Signal

HISTORY_SECONDS = 60
RATE_UNITS = ("B/s", "KB/s", "MB/s", "GB/s", "TB/s")


def split_rate(bytes_per_second: float | None) -> tuple[str, str]:
    """(number, unit) such as ("113", "MB/s"); one decimal below 100, like the Windows panel."""
    if bytes_per_second is None or not math.isfinite(bytes_per_second) or bytes_per_second < 0:
        return "—", ""
    value, unit = float(bytes_per_second), 0
    while value >= 1024 and unit < len(RATE_UNITS) - 1:
        value /= 1024
        unit += 1
    number = f"{value:.0f}" if unit == 0 or value >= 100 else f"{value:.1f}"
    return number, RATE_UNITS[unit]


def fixed(value: float | None, digits: int = 0) -> str:
    if value is None or not math.isfinite(value):
        return "—"
    return f"{value:.{digits}f}"


class Dashboard(QObject):
    """Samples the source once a second and publishes the result as QML properties."""

    updated = Signal()

    def __init__(self, source, parent: QObject | None = None):
        super().__init__(parent)
        self._source = source
        self._history: dict[str, deque] = {}
        self._data: dict = {}
        # A simulated source can fill the charts immediately; real history starts empty.
        for _ in range(HISTORY_SECONDS if getattr(source, "simulated", False) else 0):
            self._ingest(source.sample())
        self._timer = QTimer(self)
        self._timer.setInterval(1000)
        self._timer.timeout.connect(self.tick)
        self.tick()
        self._timer.start()

    def tick(self) -> None:
        try:
            snapshot = self._source.sample()
        except Exception:  # A failing source must never take the screen down.
            snapshot = {"connected": False, "status": "数据源不可用"}
        self._ingest(snapshot)
        self.updated.emit()

    def _push(self, key: str, value: float | None) -> list[float]:
        history = self._history.setdefault(key, deque(maxlen=HISTORY_SECONDS))
        history.append(value if value is not None and math.isfinite(value) else 0.0)
        return list(history)

    def _ingest(self, snapshot: dict) -> None:
        network = snapshot.get("network") or {}
        classes = {}
        for name in ("direct", "proxy"):
            rates = network.get(name) or {}
            down, up = rates.get("down"), rates.get("up")
            down_history = self._push(f"{name}.down", down)
            up_history = self._push(f"{name}.up", up)
            down_text, down_unit = split_rate(down)
            up_text, up_unit = split_rate(up)
            peak_text, peak_unit = split_rate(max(down_history + up_history, default=0))
            classes[name] = {
                "downText": down_text, "downUnit": down_unit,
                "upText": up_text, "upUnit": up_unit,
                "downHistory": down_history, "upHistory": up_history,
                "peak": f"{peak_text} {peak_unit}".strip(),
            }

        cpu = dict(snapshot.get("cpu") or {})
        cpu["history"] = self._push("cpu.usage", cpu.get("usage"))
        gpu = dict(snapshot.get("gpu") or {})
        gpu["history"] = self._push("gpu.usage", gpu.get("usage"))

        memory = dict(snapshot.get("memory") or {})
        used, total = memory.get("used"), memory.get("total")
        memory["usage"] = used / total * 100 if used is not None and total else None
        memory["history"] = self._push("memory.usage", memory.get("usage"))
        disk = snapshot.get("disk") or {}
        read_text, read_unit = split_rate(disk.get("read"))
        write_text, write_unit = split_rate(disk.get("write"))

        self._data = {
            "connected": bool(snapshot.get("connected")),
            "status": snapshot.get("status") or "",
            "network": classes,
            "cpu": cpu,
            "gpu": gpu,
            "memory": memory,
            "disk": {"readText": read_text, "readUnit": read_unit, "writeText": write_text, "writeUnit": write_unit},
        }

    def _get(self, key: str):
        return self._data.get(key)

    connected = Property(bool, lambda self: self._data.get("connected", False), notify=updated)
    status = Property(str, lambda self: self._data.get("status", ""), notify=updated)
    simulated = Property(bool, lambda self: bool(getattr(self._source, "simulated", False)), constant=True)
    network = Property("QVariantMap", lambda self: self._get("network"), notify=updated)
    cpu = Property("QVariantMap", lambda self: self._get("cpu"), notify=updated)
    gpu = Property("QVariantMap", lambda self: self._get("gpu"), notify=updated)
    memory = Property("QVariantMap", lambda self: self._get("memory"), notify=updated)
    disk = Property("QVariantMap", lambda self: self._get("disk"), notify=updated)
