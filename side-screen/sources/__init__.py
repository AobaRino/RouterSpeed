"""Data sources for the side screen.

A source is any object with ``sample() -> dict`` returning one snapshot (layout documented
in backend.Dashboard) and a boolean ``simulated`` attribute. Real sources (router API,
psutil, hardware sensors) will implement the same shape; only the mock exists for now.
"""

from .mock import MockSource

__all__ = ["MockSource"]
