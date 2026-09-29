"""RouterSpeed side screen: a 1920×480 dashboard for an 8.8-inch bar display.

    python main.py                      # simulator window, exactly 1920×480 pixels
    python main.py --screen 1           # fullscreen on screen 1 (a display seen by Windows)
    python main.py --screenshot a.png   # render, save a screenshot after a few seconds, exit
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

WIDTH, HEIGHT = 1920, 480


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--screen", type=int, help="show fullscreen on this screen index instead of the simulator window")
    parser.add_argument("--screenshot", type=Path, help="save a PNG of the window and exit")
    parser.add_argument("--after", type=float, default=2.5, help="seconds to wait before --screenshot (default 2.5)")
    parser.add_argument("--seed", type=int, help="seed for the simulated data, for repeatable screenshots")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    # The layout is designed pixel-for-pixel for 1920×480, so ignore Windows display scaling.
    os.environ.setdefault("QT_ENABLE_HIGHDPI_SCALING", "0")

    from PySide6.QtCore import QTimer
    from PySide6.QtGui import QGuiApplication
    from PySide6.QtQml import QQmlApplicationEngine
    from PySide6.QtQuick import QQuickWindow
    from shiboken6 import Shiboken

    from backend import Dashboard
    from sources import MockSource

    app = QGuiApplication(sys.argv)
    app.setApplicationName("RouterSpeed Side Screen")
    dashboard = Dashboard(MockSource(args.seed))
    engine = QQmlApplicationEngine()
    engine.setInitialProperties({"dashboard": dashboard, "simulator": args.screen is None})
    engine.load(Path(__file__).with_name("qml") / "Main.qml")
    if not engine.rootObjects():
        return 1
    # rootObjects() hands back the QWindow base; the QML Window is a QQuickWindow.
    window = Shiboken.wrapInstance(Shiboken.getCppPointer(engine.rootObjects()[0])[0], QQuickWindow)

    if args.screen is not None:
        screens = QGuiApplication.screens()
        if not 0 <= args.screen < len(screens):
            print(f"No screen {args.screen}; available: " +
                  ", ".join(f"{i}={s.name()} {s.size().width()}x{s.size().height()}" for i, s in enumerate(screens)))
            return 2
        window.setScreen(screens[args.screen])
        window.setGeometry(screens[args.screen].geometry())
        window.showFullScreen()

    if args.screenshot:
        def capture() -> None:
            window.grabWindow().save(str(args.screenshot))
            app.quit()
        QTimer.singleShot(int(args.after * 1000), capture)

    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
